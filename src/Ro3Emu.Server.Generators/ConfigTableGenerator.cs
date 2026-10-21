using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ro3Emu.Server.Generators;

/// <summary>
/// Implements the typed views of the client's config tables.
/// <list type="bullet">
/// <item><c>[ConfigTable("Name")] partial record struct X(JsonElement Row)</c>: a view of one row. Every
/// <c>[ConfigField("_iField")] partial</c> property gets the reader its type selects (ConfigValue); the type also gets
/// <c>TableName</c>, <c>Info</c>, <c>Get</c> and <c>All</c>.</item>
/// <item><c>[ConfigConstants("Name")] partial record struct X(IReadOnlyDictionary&lt;string, JsonElement&gt;
/// Values)</c>: a view of a key/value table. Every <c>[ConfigConstant("Key")] partial</c> property, of a nullable type,
/// reads its key's value or null; the type also gets <c>TableName</c>, <c>Info</c> and <c>Of</c>.</item>
/// </list>
/// Every view is listed in <c>ConfigTables.All</c>, which the server checks against the game data at startup.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ConfigTableGenerator : IIncrementalGenerator
{
    private const string Namespace = "Ro3Emu.Server.Data.Config";
    private const string TableAttribute = Namespace + ".ConfigTableAttribute";
    private const string FieldAttribute = Namespace + ".ConfigFieldAttribute";
    private const string ConstantsAttribute = Namespace + ".ConfigConstantsAttribute";
    private const string ConstantAttribute = Namespace + ".ConfigConstantAttribute";
    private const string Data = "global::Ro3Emu.Server.Data.GameData";
    private const string Linq = "global::System.Linq.Enumerable";
    private const string Collections = "global::System.Collections.Generic";
    private const string Array = "global::System.Text.Json.JsonValueKind.Array";
    private const string Element = "System.Text.Json.JsonElement";
    private const string Dictionary = "System.Collections.Generic.IReadOnlyDictionary<string, System.Text.Json.JsonElement>";

    private static readonly DiagnosticDescriptor NotAView = new("RO3CFG001", "Not a config view",
        "'{0}' must be a partial record struct whose primary constructor is ({1})",
        "ConfigTables", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedType = new("RO3CFG002", "Unsupported config field type",
        "'{0}' has type '{1}', which no config reader supports (see docs/guide/config-tables.md)", "ConfigTables",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotPartial = new("RO3CFG003", "Config field is not a partial property",
        "'{0}' must be a partial property with a get accessor only", "ConfigTables",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateField = new("RO3CFG004", "Duplicate config field",
        "Field '{0}' is declared twice in '{1}'", "ConfigTables", DiagnosticSeverity.Error, isEnabledByDefault: true);

    // Property type -> (ConfigKind, ConfigValue reader). Constants read nullable types with the *Constant readers.
    private static readonly Dictionary<string, (string Kind, string Reader)> FieldKinds = new()
    {
        ["long"] = ("Integral", "Integral"),
        ["int"] = ("Integral32", "Integral32"),
        ["double"] = ("Number", "Number"),
        ["bool"] = ("Bool", "Bool"),
        ["string"] = ("Text", "Text"),
        ["long[]"] = ("Ints", "Ints"),
        ["double[]"] = ("Numbers", "Numbers"),
        ["long[][]"] = ("IntRows", "IntRows"),
        ["double[][]"] = ("NumberRows", "NumberRows"),
        ["string[]"] = ("Texts", "Texts"),
        ["(long Id, double Value)[]"] = ("Pairs", "Pairs"),
        [Element + "?"] = ("Raw", "Raw"),
    };

    private static readonly Dictionary<string, (string Kind, string Reader)> ConstantKinds = new()
    {
        ["long?"] = ("Integral", "IntegralConstant"),
        ["int?"] = ("Integral32", "Integral32Constant"),
        ["double?"] = ("Number", "NumberConstant"),
        ["bool?"] = ("Bool", "BoolConstant"),
        ["string?"] = ("Text", "TextConstant"),
        ["long[]?"] = ("Ints", "IntsConstant"),
        ["double[]?"] = ("Numbers", "NumbersConstant"),
        ["long[][]?"] = ("IntRows", "IntRowsConstant"),
        ["string[]?"] = ("Texts", "TextsConstant"),
        [Element + "?"] = ("Raw", "RawConstant"),
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var tables = context.SyntaxProvider.ForAttributeWithMetadataName(TableAttribute,
            static (node, _) => node is RecordDeclarationSyntax,
            static (ctx, _) => Describe(ctx, keyed: false));
        var constants = context.SyntaxProvider.ForAttributeWithMetadataName(ConstantsAttribute,
            static (node, _) => node is RecordDeclarationSyntax,
            static (ctx, _) => Describe(ctx, keyed: true));
        context.RegisterSourceOutput(tables, static (spc, view) => Emit(spc, view));
        context.RegisterSourceOutput(constants, static (spc, view) => Emit(spc, view));
        context.RegisterSourceOutput(tables.Collect().Combine(constants.Collect()),
            static (spc, all) => EmitRegistry(spc, [.. all.Left, .. all.Right]));
    }

    private static View Describe(GeneratorAttributeSyntaxContext ctx, bool keyed)
    {
        var type = (INamedTypeSymbol)ctx.TargetSymbol;
        var attribute = ctx.Attributes[0];
        var record = (RecordDeclarationSyntax)ctx.TargetNode;
        string parameter = keyed ? "Values" : "Row", parameterType = keyed ? Dictionary : Element;
        var diagnostics = new List<Diag>();
        bool shaped = type.IsRecord && type.TypeKind == TypeKind.Struct && type.ContainingType == null
            && record.Modifiers.Any(SyntaxKind.PartialKeyword)
            && record.ParameterList?.Parameters.Count == 1
            && record.ParameterList.Parameters[0].Identifier.Text == parameter
            && type.GetMembers(parameter).OfType<IPropertySymbol>().FirstOrDefault()?.Type.ToDisplayString() == parameterType;
        if (!shaped)
            diagnostics.Add(new Diag(NotAView, record.Identifier.GetLocation(), type.Name, parameterType + " " + parameter));
        var fields = Fields(type, keyed, diagnostics);
        string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
        return new View(ns, type.Name, type.IsReadOnly, FirstArgument(attribute), Named(attribute, "Optional"),
            Named(attribute, "Grouped"), keyed, NamedText(attribute, "ValueField"),
            new EquatableArray<Field>([.. fields]), new EquatableArray<Diag>([.. diagnostics]));
    }

    private static List<Field> Fields(INamedTypeSymbol type, bool keyed, List<Diag> diagnostics)
    {
        var kinds = keyed ? ConstantKinds : FieldKinds;
        string attributeName = keyed ? ConstantAttribute : FieldAttribute;
        var fields = new List<Field>();
        var seen = new HashSet<string>();
        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            var fieldAttribute = property.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == attributeName);
            if (fieldAttribute == null) continue;
            var location = property.Locations.FirstOrDefault() ?? Location.None;
            string name = FirstArgument(fieldAttribute);
            string typeName = property.Type.ToDisplayString();
            if (!property.IsPartialDefinition || property.SetMethod != null || property.GetMethod == null)
                diagnostics.Add(new Diag(NotPartial, location, property.Name, ""));
            else if (!kinds.TryGetValue(typeName, out var kind))
                diagnostics.Add(new Diag(UnsupportedType, location, property.Name, typeName));
            else if (!seen.Add(name))
                diagnostics.Add(new Diag(DuplicateField, location, name, type.Name));
            else
                fields.Add(new Field(property.Name, name, typeName, kind.Kind, kind.Reader,
                    Named(fieldAttribute, "Optional"), SyntaxFacts.GetText(property.DeclaredAccessibility)));
        }
        return fields;
    }

    private static string FirstArgument(AttributeData attribute) =>
        attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string ?? "" : "";

    private static bool Named(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value is true;

    private static string? NamedText(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value as string;

    private static void Emit(SourceProductionContext spc, View view)
    {
        foreach (var d in view.Diagnostics)
            spc.ReportDiagnostic(Diagnostic.Create(d.Descriptor, d.Location, d.First, d.Second));
        if (view.Diagnostics.Count > 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by Ro3Emu.Server.Generators.ConfigTableGenerator");
        sb.AppendLine("#nullable enable");
        if (view.Namespace.Length > 0) sb.Append("namespace ").Append(view.Namespace).AppendLine(";");
        sb.AppendLine();
        sb.Append(view.ReadOnly ? "readonly " : "").Append("partial record struct ").AppendLine(view.Name);
        sb.AppendLine("{");
        sb.Append("    public const string TableName = \"").Append(view.Table).AppendLine("\";");
        sb.AppendLine();
        EmitInfo(sb, view);
        sb.AppendLine();
        if (view.Keyed) EmitConstantsAccess(sb, view.Name);
        else if (view.Grouped) EmitGroupedAccess(sb, view.Name);
        else EmitAccess(sb, view.Name);
        foreach (var f in view.Fields)
        {
            sb.AppendLine();
            sb.Append("    ").Append(f.Accessibility).Append(" partial ").Append(Qualified(f.Type)).Append(' ')
                .Append(f.Property).Append(" => global::").Append(Namespace).Append(".ConfigValue.").Append(f.Reader);
            if (view.Keyed)
                sb.Append("(Values, \"").Append(f.Name).Append("\", ").Append(Text(view.ValueField)).AppendLine(");");
            else sb.Append("(Row, \"").Append(f.Name).AppendLine("\");");
        }
        sb.AppendLine("}");
        spc.AddSource(Hint(view) + ".g.cs", sb.ToString());
    }

    private static void EmitInfo(StringBuilder sb, View view)
    {
        sb.Append("    public static global::").Append(Namespace)
            .Append(".ConfigTableInfo Info { get; } = new(TableName, ").Append(Literal(view.Optional)).Append(", ")
            .Append(Literal(view.Grouped)).AppendLine(",");
        sb.AppendLine("    [");
        foreach (var f in view.Fields)
            sb.Append("        new(\"").Append(f.Name).Append("\", global::").Append(Namespace).Append(".ConfigKind.")
                .Append(f.Kind).Append(", ").Append(Literal(f.Optional)).AppendLine("),");
        sb.Append("    ]");
        if (view.Keyed) sb.Append(", Keyed: true, ValueField: ").Append(Text(view.ValueField));
        sb.AppendLine(");");
    }

    private static void EmitAccess(StringBuilder sb, string name)
    {
        sb.Append("    public static ").Append(name).Append("? Get(").Append(Data).AppendLine(" data, long id) =>");
        sb.Append("        data.Row(TableName, id) is { } row ? new ").Append(name).AppendLine("(row) : null;");
        sb.AppendLine();
        sb.Append("    public static ").Append(Collections).Append(".IEnumerable<").Append(name).Append("> All(")
            .Append(Data).AppendLine(" data) =>");
        sb.Append("        ").Append(Linq).Append(".Select(data.Table(TableName).Values, row => new ").Append(name)
            .AppendLine("(row));");
    }

    private static void EmitGroupedAccess(StringBuilder sb, string name)
    {
        sb.Append("    public static ").Append(Collections).Append(".IReadOnlyList<").Append(name).Append("> Get(")
            .Append(Data).AppendLine(" data, long id) =>");
        sb.Append("        data.Row(TableName, id) is { ValueKind: ").Append(Array).AppendLine(" } group");
        sb.Append("            ? [.. ").Append(Linq).Append(".Select(group.EnumerateArray(), row => new ").Append(name)
            .AppendLine("(row))] : [];");
        sb.AppendLine();
        sb.Append("    public static ").Append(Collections).Append(".IEnumerable<").Append(name).Append("> All(")
            .Append(Data).AppendLine(" data) =>");
        sb.Append("        ").Append(Linq).AppendLine(".SelectMany(data.Table(TableName).Values, group =>");
        sb.Append("            group.ValueKind == ").AppendLine(Array);
        sb.Append("                ? ").Append(Linq).Append(".Select(group.EnumerateArray(), row => new ").Append(name)
            .AppendLine("(row))");
        sb.Append("                : ").Append(Linq).Append(".Empty<").Append(name).AppendLine(">());");
    }

    private static void EmitConstantsAccess(StringBuilder sb, string name)
    {
        sb.Append("    public static ").Append(name).Append(" Of(").Append(Data).Append(" data) => new(data.Table(TableName));")
            .AppendLine();
    }

    private static void EmitRegistry(SourceProductionContext spc, ImmutableArray<View> views)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by Ro3Emu.Server.Generators.ConfigTableGenerator");
        sb.AppendLine("#nullable enable");
        sb.Append("namespace ").Append(Namespace).AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Every declared config table view, checked against the game data at startup.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class ConfigTables");
        sb.AppendLine("{");
        sb.Append("    public static ").Append(Collections)
            .AppendLine(".IReadOnlyList<ConfigTableInfo> All { get; } =");
        sb.AppendLine("    [");
        foreach (var view in views.Where(v => v.Diagnostics.Count == 0).OrderBy(v => v.Table, StringComparer.Ordinal))
            sb.Append("        global::").Append(Hint(view)).AppendLine(".Info,");
        sb.AppendLine("    ];");
        sb.AppendLine("}");
        spc.AddSource("ConfigTables.g.cs", sb.ToString());
    }

    private static string Literal(bool value) => value ? "true" : "false";

    private static string Text(string? value) => value == null ? "null" : "\"" + value + "\"";

    private static string Hint(View view) => (view.Namespace.Length > 0 ? view.Namespace + "." : "") + view.Name;

    private static string Qualified(string type) => type == Element + "?" ? "global::" + Element + "?" : type;

    private sealed record View(string Namespace, string Name, bool ReadOnly, string Table, bool Optional, bool Grouped,
        bool Keyed, string? ValueField, EquatableArray<Field> Fields, EquatableArray<Diag> Diagnostics);

    private sealed record Field(string Property, string Name, string Type, string Kind, string Reader, bool Optional,
        string Accessibility);

    private sealed record Diag(DiagnosticDescriptor Descriptor, Location Location, string First, string Second);
}
