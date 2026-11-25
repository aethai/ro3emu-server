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
    private const int MaxClassDependencies = 20;
    private const int MaxGameClassLines = 2500;
    private const int MaxLineLength = 200;

    private const int MethodsOver80Lines = 89;
    private const int MethodsOver10ParamsInGame = 7;
    private const int ClassesWith15OrMoreDependencies = 26;
    private const int ClassesOver20Dependencies = 4;
    private const int ClassesOver2500LinesInGame = 0;
    private const int RoleNullForgiving = 961;
    private const int LinesOver200Chars = 92;
    private const int ExposedHookLists = 0;
    private const int NumericIsOpenCalls = 0;
    private const int NumericStatIndexers = 0;
    private const int NumericRouteMaps = 105;
    private const int NumericSendGameCalls = 48;
    private const int SystemClockCallsInGame = 0;
    private const int AdHocResetOffsetsInGame = 0;
    private const int PrivateRepoRootHelpersInTests = 0;
    private const int ServerHostCreateOutsideTestServer = 193;
    private const int ClearAllPoolsInTests = 61;
    private const int TaskDelayCallsInTests = 533;

    private static readonly string ObjDir = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
    private static readonly string BinDir = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;
    private static readonly string ConfigTablesDir =
        Path.Combine("Data", "Config", "Tables") + Path.DirectorySeparatorChar;

    private static readonly Lazy<ShapeSnapshot> ServerSnapshot = new(ScanServer);
    private static readonly Lazy<TestShapeSnapshot> TestsSnapshot = new(ScanTests);

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
    public void NoClassExceeds20Dependencies() =>
        AssertRatchet(nameof(ClassesOver20Dependencies), ClassesOver20Dependencies,
            ServerSnapshot.Value.OverloadedClasses);

    [Fact]
    public void NoGameClassExceeds2500Lines() =>
        AssertRatchet(nameof(ClassesOver2500LinesInGame), ClassesOver2500LinesInGame,
            ServerSnapshot.Value.OversizedGameClasses);

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
    public void NumericIsOpenCallsInGameOnlyGoDown() =>
        AssertRatchet(nameof(NumericIsOpenCalls), NumericIsOpenCalls, ServerSnapshot.Value.NumericIsOpen);

    [Fact]
    public void NumericStatIndexersInGameOnlyGoDown() =>
        AssertRatchet(nameof(NumericStatIndexers), NumericStatIndexers, ServerSnapshot.Value.NumericStatIndexers);

    [Fact]
    public void NumericRouteMapsOnlyGoDown() =>
        AssertRatchet(nameof(NumericRouteMaps), NumericRouteMaps, ServerSnapshot.Value.NumericRouteMaps);

    [Fact]
    public void NumericSendGameCallsOnlyGoDown() =>
        AssertRatchet(nameof(NumericSendGameCalls), NumericSendGameCalls, ServerSnapshot.Value.NumericSendGame);

    [Fact]
    public void SystemClockCallsInGameOnlyGoDown() =>
        AssertRatchet(nameof(SystemClockCallsInGame), SystemClockCallsInGame, ServerSnapshot.Value.SystemClockCalls);

    [Fact]
    public void AdHocResetOffsetsInGameOnlyGoDown() =>
        AssertRatchet(nameof(AdHocResetOffsetsInGame), AdHocResetOffsetsInGame, ServerSnapshot.Value.AdHocResetOffsets);

    [Fact]
    public void PrivateRepoRootHelpersInTestsOnlyGoDown() =>
        AssertRatchet(nameof(PrivateRepoRootHelpersInTests), PrivateRepoRootHelpersInTests, TestsSnapshot.Value.PrivateRepoRoots);

    [Fact]
    public void ServerHostCreateOutsideTestServerOnlyGoesDown() =>
        AssertRatchet(nameof(ServerHostCreateOutsideTestServer), ServerHostCreateOutsideTestServer, TestsSnapshot.Value.ServerHostCreates);

    [Fact]
    public void ClearAllPoolsInTestsOnlyGoDown() =>
        AssertRatchet(nameof(ClearAllPoolsInTests), ClearAllPoolsInTests, TestsSnapshot.Value.ClearAllPools);

    [Fact]
    public void TaskDelayCallsInTestsOnlyGoDown() =>
        AssertRatchet(nameof(TaskDelayCallsInTests), TaskDelayCallsInTests, TestsSnapshot.Value.TaskDelays);

    [Fact]
    public void ServerHostStaysThin()
    {
        string path = Path.Combine(ClientData.RepoRoot, "src", "Ro3Emu.Server", "ServerHost.cs");
        int lines = File.ReadAllLines(path).Length;
        Assert.True(lines <= 150, string.Create(CultureInfo.InvariantCulture,
            $"ServerHost.cs grew to {lines} lines (limit 150). Add new services to a feature composition module under Composition/."));
    }

    [Fact]
    public void GmCommandsDoesNotInjectServiceProvider()
    {
        var ctors = typeof(Ro3Emu.Server.Game.GmCommands).GetConstructors();
        Assert.DoesNotContain(ctors.SelectMany(c => c.GetParameters()), p => p.ParameterType == typeof(IServiceProvider));
    }

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
                public void TooLong(dynamic session, dynamic functions, dynamic stats, dynamic routes)
                {
                    int x = session.Role!.Id;
                    bool open = functions.IsOpen(session, 13);
                    long hp = stats[101];
                    routes.Map(30400, (s, b) => Task.CompletedTask);
                    session.SendGameAsync(30400, new object());
                    var now = DateTime.UtcNow;
                    var ticks = Environment.TickCount64;
                    var gameDay = now.AddHours(-5).Date;
            {{body}}
                }
                public void TooWide(int a1, int a2, int a3, int a4, int a5, int a6, int a7, int a8, int a9, int a10, int a11) { }
            }
            """;

        var snapshot = ScanTexts([("Game/Sample.cs", sample)]);
        Assert.Single(snapshot.LongMethods);
        Assert.Single(snapshot.WideGameMethods);
        Assert.Single(snapshot.HeavyClasses);
        Assert.Empty(snapshot.OverloadedClasses);
        Assert.Empty(snapshot.OversizedGameClasses);
        Assert.Single(snapshot.RoleBangs);
        Assert.Single(snapshot.LongLines);
        Assert.Equal(2, snapshot.HookLists.Count);
        Assert.Single(snapshot.NumericIsOpen);
        Assert.Single(snapshot.NumericStatIndexers);
        Assert.Single(snapshot.NumericRouteMaps);
        Assert.Single(snapshot.NumericSendGame);
        Assert.Equal(2, snapshot.SystemClockCalls.Count);
        Assert.Single(snapshot.AdHocResetOffsets);
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
        Dictionary<string, (int Lines, int Files)> gameClassLines = new(StringComparer.Ordinal);
        List<string> roleBangs = [];
        List<string> longLines = [];
        List<string> hookLists = [];
        List<string> numericIsOpen = [];
        List<string> numericStatIndexers = [];
        List<string> numericRouteMaps = [];
        List<string> numericSendGame = [];
        List<string> systemClockCalls = [];
        List<string> adHocResetOffsets = [];

        foreach (var (relPath, text) in files)
        {
            if (IsConfigTableView(relPath)) continue;
            CollectLongLines(relPath, text, longLines);
            var root = CSharpSyntaxTree.ParseText(text, path: relPath).GetRoot();
            bool inGame = relPath.StartsWith("Game/", StringComparison.Ordinal);

            CollectLongMethods(relPath, root, longMethods);
            if (inGame) CollectWideMethods(relPath, root, wideGameMethods);
            CollectHeavyTypes(relPath, root, maxDepsByType);
            if (inGame) CollectGameClassLines(root, gameClassLines);
            CollectRoleBangs(relPath, root, roleBangs);
            if (inGame) CollectHookLists(relPath, root, hookLists);
            if (inGame) CollectNumericIsOpen(relPath, root, numericIsOpen);
            if (inGame) CollectNumericStatIndexers(relPath, root, numericStatIndexers);
            CollectNumericRouteMaps(relPath, root, numericRouteMaps);
            CollectNumericSendGame(relPath, root, numericSendGame);
            if (inGame) CollectSystemClockCalls(relPath, root, systemClockCalls);
            if (inGame && !string.Equals(relPath, "Game/GameCalendar.cs", StringComparison.Ordinal))
                CollectAdHocResetOffsets(relPath, root, adHocResetOffsets);
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

        List<string> overloadedClasses =
        [
            .. maxDepsByType
                .Where(kv => kv.Value.Params > MaxClassDependencies)
                .OrderByDescending(kv => kv.Value.Params)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => string.Format(CultureInfo.InvariantCulture,
                    "{0} ({1} deps) at {2}", kv.Key, kv.Value.Params, kv.Value.Location)),
        ];

        List<string> oversizedGameClasses =
        [
            .. gameClassLines
                .Where(kv => kv.Value.Lines > MaxGameClassLines)
                .OrderByDescending(kv => kv.Value.Lines)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => string.Format(CultureInfo.InvariantCulture,
                    "{0} ({1} lines across {2} file(s))", kv.Key, kv.Value.Lines, kv.Value.Files)),
        ];

        return new(longMethods, wideGameMethods, heavyClasses, overloadedClasses, oversizedGameClasses,
            roleBangs, longLines, hookLists, numericIsOpen, numericStatIndexers, numericRouteMaps,
            numericSendGame, systemClockCalls, adHocResetOffsets);
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

    private static void CollectGameClassLines(SyntaxNode root,
        Dictionary<string, (int Lines, int Files)> gameClassLines)
    {
        foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (type.Parent is TypeDeclarationSyntax) continue;
            var span = type.GetLocation().GetLineSpan();
            int lines = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
            string key = QualifiedTypeName(type);
            gameClassLines.TryGetValue(key, out var existing);
            gameClassLines[key] = (existing.Lines + lines, existing.Files + 1);
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

    private static void CollectNumericIsOpen(string relPath, SyntaxNode root, List<string> target)
    {
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string? method = call.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                IdentifierNameSyntax id => id.Identifier.ValueText,
                _ => null,
            };
            if (!string.Equals(method, "IsOpen", StringComparison.Ordinal)) continue;
            var args = call.ArgumentList.Arguments;
            if (args.Count < 2 || !IsNumericLiteral(args[1].Expression)) continue;
            int line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            target.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1} {2}", relPath, line, call.ToString()));
        }
    }

    private static void CollectNumericStatIndexers(string relPath, SyntaxNode root, List<string> target)
    {
        foreach (var access in root.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
        {
            if (access.ArgumentList.Arguments.Count != 1) continue;
            if (!IsNumericLiteral(access.ArgumentList.Arguments[0].Expression)) continue;
            string? name = access.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                _ => null,
            };
            if (name is not ("stats" or "CombatStats" or "casterStats" or "BattleAttr"
                or "baseStats" or "targetStats" or "attackerStats" or "defenderStats")) continue;
            int line = access.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            target.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1} {2}", relPath, line, access.ToString()));
        }
    }

    private static void CollectNumericRouteMaps(string relPath, SyntaxNode root, List<string> target)
    {
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax ma) continue;
            if (!string.Equals(ma.Name.Identifier.ValueText, "Map", StringComparison.Ordinal)) continue;
            string? receiver = ma.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                MemberAccessExpressionSyntax inner => inner.Name.Identifier.ValueText,
                _ => null,
            };
            if (receiver is not ("routes" or "router" or "Routes" or "Router")) continue;
            var args = call.ArgumentList.Arguments;
            if (args.Count < 2 || !IsNumericLiteral(args[0].Expression)) continue;
            int line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            target.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1} {2}", relPath, line, call.ToString()));
        }
    }

    private static void CollectNumericSendGame(string relPath, SyntaxNode root, List<string> target)
    {
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string? method = call.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                IdentifierNameSyntax id => id.Identifier.ValueText,
                _ => null,
            };
            if (!string.Equals(method, "SendGameAsync", StringComparison.Ordinal)) continue;
            var args = call.ArgumentList.Arguments;
            if (args.Count < 2) continue;
            if (!IsNumericLiteral(args[0].Expression) && !IsNumericLiteral(args[1].Expression)) continue;
            int line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            target.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1} {2}", relPath, line, call.ToString()));
        }
    }

    private static void CollectSystemClockCalls(string relPath, SyntaxNode root, List<string> target)
    {
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            string typeName = access.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                MemberAccessExpressionSyntax inner => inner.Name.Identifier.ValueText,
                _ => "",
            };
            string memberName = access.Name.Identifier.ValueText;
            bool forbidden = (typeName == "DateTime" && memberName is "Now" or "UtcNow" or "Today")
                || (typeName == "DateTimeOffset" && memberName is "Now" or "UtcNow")
                || (typeName == "Environment" && memberName is "TickCount" or "TickCount64");
            if (!forbidden) continue;
            int line = access.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            target.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1} {2}", relPath, line, access.ToString()));
        }
    }

    private static void CollectAdHocResetOffsets(string relPath, SyntaxNode root, List<string> target)
    {
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax ma
                || !string.Equals(ma.Name.Identifier.ValueText, "AddHours", StringComparison.Ordinal)) continue;
            var args = call.ArgumentList.Arguments;
            if (args.Count != 1) continue;
            if (args[0].Expression is PrefixUnaryExpressionSyntax unary
                && unary.IsKind(SyntaxKind.UnaryMinusExpression)
                && unary.Operand is LiteralExpressionSyntax lit
                && lit.IsKind(SyntaxKind.NumericLiteralExpression)
                && lit.Token.Value is int or long or double
                && Convert.ToDouble(lit.Token.Value, CultureInfo.InvariantCulture) == 5.0)
            {
                int line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                target.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1} {2}", relPath, line, call.ToString()));
            }
        }
    }

    private static bool IsNumericLiteral(ExpressionSyntax expr) => expr switch
    {
        LiteralExpressionSyntax lit => lit.IsKind(SyntaxKind.NumericLiteralExpression),
        PrefixUnaryExpressionSyntax unary => unary.Operand is LiteralExpressionSyntax l
            && l.IsKind(SyntaxKind.NumericLiteralExpression),
        _ => false,
    };

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

    private static TestShapeSnapshot ScanTests()
    {
        string testsRoot = Path.Combine(ClientData.RepoRoot, "tests", "Ro3Emu.Tests");
        List<string> privateRepoRoots = [];
        List<string> serverHostCreates = [];
        List<string> clearAllPools = [];
        List<string> taskDelays = [];

        foreach (string path in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(ObjDir, StringComparison.Ordinal) && !p.Contains(BinDir, StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            string relPath = Path.GetRelativePath(testsRoot, path).Replace('\\', '/');
            if (relPath is "ClientData.cs" or "CodeShapeTests.cs" or "TestServer.cs") continue;
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: relPath).GetRoot();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (method.Identifier.ValueText == "RepoRoot" && method.ParameterList.Parameters.Count == 0
                    && method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                {
                    int line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    privateRepoRoots.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1}", relPath, line));
                }
            }

            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax ma) continue;
                if (ma.Name.Identifier.ValueText == "Create"
                    && ma.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == "ServerHost")
                {
                    int line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    serverHostCreates.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1}", relPath, line));
                }
                else if (ma.Name.Identifier.ValueText == "ClearAllPools")
                {
                    int line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    clearAllPools.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1}", relPath, line));
                }
                else if (ma.Name.Identifier.ValueText == "Delay"
                    && ma.Expression is IdentifierNameSyntax taskId && taskId.Identifier.ValueText == "Task")
                {
                    int line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    taskDelays.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1}", relPath, line));
                }
            }
        }

        return new(privateRepoRoots, serverHostCreates, clearAllPools, taskDelays);
    }

    private sealed record TestShapeSnapshot(
        IReadOnlyList<string> PrivateRepoRoots,
        IReadOnlyList<string> ServerHostCreates,
        IReadOnlyList<string> ClearAllPools,
        IReadOnlyList<string> TaskDelays);

    private sealed record ShapeSnapshot(
        IReadOnlyList<string> LongMethods,
        IReadOnlyList<string> WideGameMethods,
        IReadOnlyList<string> HeavyClasses,
        IReadOnlyList<string> OverloadedClasses,
        IReadOnlyList<string> OversizedGameClasses,
        IReadOnlyList<string> RoleBangs,
        IReadOnlyList<string> LongLines,
        IReadOnlyList<string> HookLists,
        IReadOnlyList<string> NumericIsOpen,
        IReadOnlyList<string> NumericStatIndexers,
        IReadOnlyList<string> NumericRouteMaps,
        IReadOnlyList<string> NumericSendGame,
        IReadOnlyList<string> SystemClockCalls,
        IReadOnlyList<string> AdHocResetOffsets);
}
