using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ro3Emu.Tests;

/// <summary>
/// Downward-only ratchets on server code shape, measured with Roslyn over src/Ro3Emu.Server.
/// New code may not raise any count; when a change lowers one, lower the constant here to match.
/// </summary>
public sealed class CodeShapeTests
{
    private const int MaxMethodLines = 80;
    private const int MaxGameMethodParams = 10;
    private const int ManyDependencies = 15;
    private const int MaxLineLength = 200;

    private const int MethodsOver80Lines = 97;
    private const int MethodsOver10ParamsInGame = 7;
    private const int ClassesWith15OrMoreDependencies = 29;
    private const int RoleNullForgiving = 961;
    private const int LinesOver200Chars = 96;
    private const int ExposedHookLists = 107;

    private static readonly string ObjDir = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
    private static readonly string BinDir = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;
    private static readonly string ConfigTablesDir =
        Path.Combine("Data", "Config", "Tables") + Path.DirectorySeparatorChar;

    private static readonly Lazy<ShapeSnapshot> ServerSnapshot = new(ScanServer);

    [Fact]
    public void MethodsOver80LinesOnlyGoDown() =>
        AssertRatchet(nameof(MethodsOver80Lines), MethodsOver80Lines, ServerSnapshot.Value.LongMethods);

    [Fact]
    public void GameMethodsStayWithin10Parameters() =>
        AssertRatchet(nameof(MethodsOver10ParamsInGame), MethodsOver10ParamsInGame,
            ServerSnapshot.Value.WideGameMethods);

    [Fact]
    public void ClassesWith15OrMoreDependenciesOnlyGoDown() =>
        AssertRatchet(nameof(ClassesWith15OrMoreDependencies), ClassesWith15OrMoreDependencies,
            ServerSnapshot.Value.HeavyClasses);

    [Fact]
    public void RoleNullForgivingAccessesOnlyGoDown() =>
        AssertRatchet(nameof(RoleNullForgiving), RoleNullForgiving, ServerSnapshot.Value.RoleBangs);

    [Fact]
    public void LinesOver200CharsOnlyGoDown() =>
        AssertRatchet(nameof(LinesOver200Chars), LinesOver200Chars, ServerSnapshot.Value.LongLines);

    [Fact]
    public void ExposedHookListsInGameOnlyGoDown() =>
        AssertRatchet(nameof(ExposedHookLists), ExposedHookLists, ServerSnapshot.Value.HookLists);

    [Fact]
    public void ScannerDetectsEveryTrackedShapeViolation()
    {
        string body = string.Join('\n', Enumerable.Repeat("        x++;", 81));
        string longLine = "    // " + new string('a', 200);
        string params15 = string.Join(", ",
            Enumerable.Range(1, 15).Select(i => string.Create(CultureInfo.InvariantCulture, $"int p{i}")));
        string sample = $$"""
            namespace Sample;
            public sealed class Heavy({{params15}})
            {
                public List<Func<Task>> OnEvent { get; } = [];
                internal List<Action<int>> OnTick = [];
                private List<Func<Task>> _privateOnly = [];
            {{longLine}}
                public void TooLong(dynamic session)
                {
                    int x = session.Role!.Id;
            {{body}}
                }
                public void TooWide(int a1, int a2, int a3, int a4, int a5, int a6, int a7, int a8, int a9, int a10, int a11) { }
            }
            """;

        var snapshot = ScanTexts([("Game/Sample.cs", sample)]);
        Assert.Single(snapshot.LongMethods);
        Assert.Single(snapshot.WideGameMethods);
        Assert.Single(snapshot.HeavyClasses);
        Assert.Single(snapshot.RoleBangs);
        Assert.Single(snapshot.LongLines);
        Assert.Equal(2, snapshot.HookLists.Count);
    }

    private static void AssertRatchet(string name, int ceiling, IReadOnlyList<string> items)
    {
        if (items.Count > ceiling)
        {
            string preview = string.Join(Environment.NewLine, items.Take(15));
            Assert.Fail(string.Format(CultureInfo.InvariantCulture,
                "{0} grew from {1} to {2}. Fix the new violation instead of raising the ceiling.{3}{4}",
                name, ceiling, items.Count, Environment.NewLine, preview));
        }

        if (items.Count < ceiling)
        {
            Assert.Fail(string.Format(CultureInfo.InvariantCulture,
                "{0} dropped from {1} to {2}. Lower the constant in CodeShapeTests.cs to {2}.",
                name, ceiling, items.Count));
        }
    }

    private static ShapeSnapshot ScanServer()
    {
        string serverRoot = Path.Combine(ClientData.RepoRoot, "src", "Ro3Emu.Server");
        var files = Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(ObjDir, StringComparison.Ordinal)
                && !p.Contains(BinDir, StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (Path.GetRelativePath(serverRoot, p).Replace('\\', '/'), File.ReadAllText(p)));
        return ScanTexts(files);
    }

    private static ShapeSnapshot ScanTexts(IEnumerable<(string RelPath, string Text)> files)
    {
        List<string> longMethods = [];
        List<string> wideGameMethods = [];
        Dictionary<string, (int Params, string Location)> maxDepsByType = new(StringComparer.Ordinal);
        List<string> roleBangs = [];
        List<string> longLines = [];
        List<string> hookLists = [];

        foreach (var (relPath, text) in files)
        {
            if (IsConfigTableView(relPath)) continue;
            CollectLongLines(relPath, text, longLines);
            var root = CSharpSyntaxTree.ParseText(text, path: relPath).GetRoot();
            bool inGame = relPath.StartsWith("Game/", StringComparison.Ordinal);

            CollectLongMethods(relPath, root, longMethods);
            if (inGame) CollectWideMethods(relPath, root, wideGameMethods);
            CollectHeavyTypes(relPath, root, maxDepsByType);
            CollectRoleBangs(relPath, root, roleBangs);
            if (inGame) CollectHookLists(relPath, root, hookLists);
        }

        List<string> heavyClasses =
        [
            .. maxDepsByType
                .Where(kv => kv.Value.Params >= ManyDependencies)
                .OrderByDescending(kv => kv.Value.Params)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => string.Format(CultureInfo.InvariantCulture,
                    "{0} ({1} deps) at {2}", kv.Key, kv.Value.Params, kv.Value.Location)),
        ];

        return new(longMethods, wideGameMethods, heavyClasses, roleBangs, longLines, hookLists);
    }

    private static bool IsConfigTableView(string relPath) =>
        relPath.Replace('/', Path.DirectorySeparatorChar).StartsWith(ConfigTablesDir, StringComparison.Ordinal);

    private static void CollectLongLines(string relPath, string text, List<string> target)
    {
        using var reader = new StringReader(text);
        int lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (line.Length > MaxLineLength)
                target.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1} ({2} chars)", relPath, lineNumber, line.Length));
        }
    }

    private static void CollectLongMethods(string relPath, SyntaxNode root, List<string> target)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is not BaseMethodDeclarationSyntax method) continue;
            if (method is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax)) continue;
            var span = method.GetLocation().GetLineSpan();
            int lines = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
            if (lines <= MaxMethodLines) continue;
            string name = method is MethodDeclarationSyntax m ? m.Identifier.ValueText : ".ctor";
            int startLine = span.StartLinePosition.Line + 1;
            target.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1} {2} ({3} lines)", relPath, startLine, name, lines));
        }
    }

    private static void CollectWideMethods(string relPath, SyntaxNode root, List<string> target)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (HasAttribute(method, "LoggerMessage")) continue;
            int count = method.ParameterList.Parameters.Count;
            if (count <= MaxGameMethodParams) continue;
            int line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            target.Add(string.Format(CultureInfo.InvariantCulture,
                "{0}:{1} {2} ({3} params)", relPath, line, method.Identifier.ValueText, count));
        }
    }

    private static bool HasAttribute(MemberDeclarationSyntax member, string name) =>
        member.AttributeLists.SelectMany(list => list.Attributes)
            .Any(attr =>
            {
                string raw = attr.Name.ToString();
                int dot = raw.LastIndexOf('.');
                string shortName = dot >= 0 ? raw[(dot + 1)..] : raw;
                return string.Equals(shortName, name, StringComparison.Ordinal)
                    || string.Equals(shortName, name + "Attribute", StringComparison.Ordinal);
            });

    private static void CollectHeavyTypes(string relPath, SyntaxNode root,
        Dictionary<string, (int Params, string Location)> maxDepsByType)
    {
        foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            int count = type.ParameterList?.Parameters.Count ?? 0;
            foreach (var ctor in type.Members.OfType<ConstructorDeclarationSyntax>())
                count = Math.Max(count, ctor.ParameterList.Parameters.Count);
            if (count == 0) continue;

            string key = QualifiedTypeName(type);
            int line = type.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (!maxDepsByType.TryGetValue(key, out var existing) || count > existing.Params)
                maxDepsByType[key] = (count, string.Format(CultureInfo.InvariantCulture, "{0}:{1}", relPath, line));
        }
    }

    private static void CollectRoleBangs(string relPath, SyntaxNode root, List<string> target)
    {
        foreach (var bang in root.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>())
        {
            if (!bang.IsKind(SyntaxKind.SuppressNullableWarningExpression)) continue;
            bool isRole = bang.Operand switch
            {
                IdentifierNameSyntax id => string.Equals(id.Identifier.ValueText, "Role", StringComparison.Ordinal),
                MemberAccessExpressionSyntax ma => string.Equals(ma.Name.Identifier.ValueText, "Role", StringComparison.Ordinal),
                _ => false,
            };
            if (!isRole) continue;
            int line = bang.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            target.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1} {2}", relPath, line, bang.ToString()));
        }
    }

    private static void CollectHookLists(string relPath, SyntaxNode root, List<string> target)
    {
        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            bool exposed = member.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.InternalKeyword));
            if (!exposed) continue;

            (TypeSyntax? type, string name) = member switch
            {
                PropertyDeclarationSyntax prop => (prop.Type, prop.Identifier.ValueText),
                FieldDeclarationSyntax field => (field.Declaration.Type, field.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText ?? ""),
                _ => (null, ""),
            };
            if (type == null || !IsDelegateList(type)) continue;
            int line = member.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            target.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1} {2}", relPath, line, name));
        }
    }

    private static bool IsDelegateList(TypeSyntax type)
    {
        GenericNameSyntax? outer = type switch
        {
            GenericNameSyntax g => g,
            QualifiedNameSyntax q when q.Right is GenericNameSyntax g => g,
            _ => null,
        };
        if (outer == null || !string.Equals(outer.Identifier.ValueText, "List", StringComparison.Ordinal)) return false;
        if (outer.TypeArgumentList.Arguments.Count != 1) return false;

        TypeSyntax arg = outer.TypeArgumentList.Arguments[0];
        GenericNameSyntax? inner = arg switch
        {
            GenericNameSyntax g => g,
            QualifiedNameSyntax q when q.Right is GenericNameSyntax g => g,
            _ => null,
        };
        if (inner != null)
            return inner.Identifier.ValueText is "Func" or "Action";
        return arg is IdentifierNameSyntax id && string.Equals(id.Identifier.ValueText, "Action", StringComparison.Ordinal);
    }

    private static string QualifiedTypeName(TypeDeclarationSyntax type)
    {
        Stack<string> parts = [];
        for (SyntaxNode? cur = type; cur != null; cur = cur.Parent)
        {
            switch (cur)
            {
                case TypeDeclarationSyntax t:
                    parts.Push(t.Identifier.ValueText);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.Push(ns.Name.ToString());
                    break;
            }
        }
        return string.Join('.', parts);
    }

    private sealed record ShapeSnapshot(
        IReadOnlyList<string> LongMethods,
        IReadOnlyList<string> WideGameMethods,
        IReadOnlyList<string> HeavyClasses,
        IReadOnlyList<string> RoleBangs,
        IReadOnlyList<string> LongLines,
        IReadOnlyList<string> HookLists);
}
