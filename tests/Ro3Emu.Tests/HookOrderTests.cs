using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Freezes the exact execution order of every subscriber registered on every exposed hook across the composed
/// <see cref="ServerHost"/>. Works identically across legacy <c>List&lt;Action/Func&gt;</c> properties and migrated
/// <see cref="IHookOrderSource"/> hooks so hook migrations and host-composition refactors prove order preservation.
/// Regenerate Snapshots/hook-order.txt with RO3EMU_UPDATE_SNAPSHOTS=1 only when an ordering change is intentional.
/// </summary>
public sealed class HookOrderTests
{
    private const BindingFlags ExposedInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public async Task HookSubscriberOrderMatchesSnapshot()
    {
        await using var server = await TestServer.StartSyntheticAsync();
        var actual = CaptureOrder(server.App.Services);

        string file = Path.Combine(ClientData.RepoRoot, "tests", "Ro3Emu.Tests", "Snapshots", "hook-order.txt");
        if (Environment.GetEnvironmentVariable("RO3EMU_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, string.Join('\n', actual) + "\n");
        }

        Assert.True(File.Exists(file),
            "Missing snapshot " + file + ". Run with RO3EMU_UPDATE_SNAPSHOTS=1 to generate it.");

        var expected = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            string diff = FirstDiff(expected, actual);
            Assert.Fail("Hook subscriber order changed relative to Snapshots/hook-order.txt.\n" + diff);
        }
    }

    private static List<string> CaptureOrder(IServiceProvider services)
    {
        var hookTypes = typeof(ServerHost).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition
                && !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                && !t.Name.Contains('<', StringComparison.Ordinal)
                && t.Namespace != null
                && (string.Equals(t.Namespace, "Ro3Emu.Server.Game", StringComparison.Ordinal)
                    || t.Namespace.StartsWith("Ro3Emu.Server.Game.", StringComparison.Ordinal)))
            .Select(t => (Type: t, Members: HookMembers(t)))
            .Where(x => x.Members.Count > 0)
            .OrderBy(x => x.Type.FullName, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(hookTypes);

        List<string> lines = [];
        foreach (var (type, members) in hookTypes)
        {
            object? instance = services.GetService(type);
            Assert.True(instance != null, $"Hook owner {type.FullName} was not resolved from IServiceProvider.");

            foreach (var member in members)
            {
                object? value = member switch
                {
                    PropertyInfo prop => prop.GetValue(instance),
                    FieldInfo field => field.GetValue(instance),
                    _ => null,
                };
                Assert.NotNull(value);

                List<string> subscribers = value switch
                {
                    IHookOrderSource source => [.. source.DescribeSubscribers().Select(static s => s.Name)],
                    IEnumerable list => [.. list.Cast<Delegate>().Select(HookDescriptor.Describe)],
                    _ => throw new InvalidOperationException($"Unsupported hook member {type.Name}.{member.Name}"),
                };

                lines.Add(string.Format(CultureInfo.InvariantCulture, "{0}.{1} ({2})",
                    type.Name, member.Name, subscribers.Count));
                foreach (var subscriber in subscribers)
                    lines.Add("  " + subscriber);
            }
        }

        return lines;
    }

    private static List<MemberInfo> HookMembers(Type type)
    {
        List<MemberInfo> members = [];

        foreach (var prop in type.GetProperties(ExposedInstance | BindingFlags.DeclaredOnly))
        {
            if (prop.GetIndexParameters().Length != 0) continue;
            var getter = prop.GetGetMethod(nonPublic: true);
            if (getter == null || !(getter.IsPublic || getter.IsAssembly)) continue;
            if (IsHookType(prop.PropertyType)) members.Add(prop);
        }

        foreach (var field in type.GetFields(ExposedInstance | BindingFlags.DeclaredOnly))
        {
            if (field.Name.Contains('<', StringComparison.Ordinal)) continue;
            if (!(field.IsPublic || field.IsAssembly)) continue;
            if (IsHookType(field.FieldType)) members.Add(field);
        }

        members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return members;
    }

    private static bool IsHookType(Type type)
    {
        if (typeof(IHookOrderSource).IsAssignableFrom(type)) return true;
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>)) return false;
        var element = type.GetGenericArguments()[0];
        return typeof(Delegate).IsAssignableFrom(element);
    }

    private static string FirstDiff(string[] expected, List<string> actual)
    {
        int count = Math.Min(expected.Length, actual.Count);
        for (int i = 0; i < count; i++)
        {
            if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "Line {0}:\n  expected: {1}\n  actual:   {2}",
                    i + 1, expected[i], actual[i]);
            }
        }
        return string.Format(CultureInfo.InvariantCulture,
            "Snapshot line count changed: expected {0}, actual {1}.", expected.Length, actual.Count);
    }
}
