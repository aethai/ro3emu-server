using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Guards that <see cref="RoleSystems"/> stays small (&lt;=5 dependencies), every concrete <see cref="IRoleLifecycle"/>
/// implementation in the server assembly is registered in DI, and the exact execution order of Load/Login/LoginKeys/
/// LeaveKeys/Move/Leave matches Snapshots/role-lifecycle-order.txt.
/// Regenerate with RO3EMU_UPDATE_SNAPSHOTS=1 only when an ordering change is intentional.
/// </summary>
public sealed class RoleLifecycleOrderTests
{
    [Fact]
    public void RoleSystemsHasAtMostFiveDependencies()
    {
        var ctors = typeof(RoleSystems).GetConstructors();
        var ctor = Assert.Single(ctors);
        Assert.True(ctor.GetParameters().Length <= 5,
            $"RoleSystems has {ctor.GetParameters().Length} dependencies (expected <= 5).");
    }

    [Fact]
    public async Task EveryRoleLifecycleIsRegisteredAndOrderMatchesSnapshot()
    {
        var expectedTypes = typeof(ServerHost).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IRoleLifecycle).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(expectedTypes);

        await using var server = await TestServer.StartSyntheticAsync();
        var diTypes = server.App.Services.GetServices<IRoleLifecycle>()
            .Select(l => l.GetType())
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expectedTypes, diTypes);

        var actual = server.Service<RoleSystems>().DescribeOrder();
        string file = Path.Combine(ClientData.RepoRoot, "tests", "Ro3Emu.Tests", "Snapshots", "role-lifecycle-order.txt");
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
            int count = Math.Min(expected.Length, actual.Count);
            for (int i = 0; i < count; i++)
            {
                if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
                {
                    Assert.Fail(string.Format(CultureInfo.InvariantCulture,
                        "RoleLifecycle order changed at line {0}:\n  expected: {1}\n  actual:   {2}",
                        i + 1, expected[i], actual[i]));
                }
            }
            Assert.Fail(string.Format(CultureInfo.InvariantCulture,
                "RoleLifecycle snapshot line count changed: expected {0}, actual {1}.",
                expected.Length, actual.Count));
        }
    }
}
