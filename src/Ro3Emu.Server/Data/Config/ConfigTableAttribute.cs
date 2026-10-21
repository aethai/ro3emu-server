namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// Marks a typed view of one of the client's config tables: a <c>partial record struct</c> over a row,
/// <c>(JsonElement Row)</c>, whose <see cref="ConfigFieldAttribute"/> properties the source generator implements. The
/// generator also adds <c>TableName</c>, <c>Info</c>, <c>Get</c> and <c>All</c>, and lists the table in
/// <c>ConfigTables.All</c>, which <see cref="ConfigValidator"/> checks at startup.
/// </summary>
/// <param name="name">The table's file name in data/config, as the client names it (SkillConfig).</param>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class ConfigTableAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    /// <summary>The export may lack the table; otherwise a client export without it stops the server.</summary>
    public bool Optional { get; init; }

    /// <summary>Each key holds a list of rows (AwardConfig: an award id and its entries); <c>Get</c> returns the
    /// list.</summary>
    public bool Grouped { get; init; }
}

/// <summary>A field of a <see cref="ConfigTableAttribute"/> view: the client's column name (_iCD) on a partial
/// property whose type selects how the value is read (<see cref="ConfigKind"/>).</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ConfigFieldAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    /// <summary>Some rows of the export lack the column; otherwise every row must carry it.</summary>
    public bool Optional { get; init; }
}

/// <summary>
/// Marks a typed view of one of the client's key/value tables (PublicParamConfig: a parameter name and its value): a
/// <c>partial record struct</c> over the table, <c>(IReadOnlyDictionary&lt;string, JsonElement&gt; Values)</c>, whose
/// <see cref="ConfigConstantAttribute"/> properties the source generator implements. A property is null when the
/// export lacks the key; 0 is a value. The generator adds <c>TableName</c>, <c>Info</c> and <c>Of(data)</c> and lists
/// the table for the startup check.
/// </summary>
/// <param name="name">The table's file name in data/config.</param>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class ConfigConstantsAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    /// <summary>The export may lack the table; otherwise a client export without it stops the server.</summary>
    public bool Optional { get; init; }

    /// <summary>Each key holds a row and the value is this field of it (GuildSettingConfig: _kValue).</summary>
    public string? ValueField { get; init; }
}

/// <summary>A key of a <see cref="ConfigConstantsAttribute"/> view, on a nullable partial property whose type selects
/// how the value is read (<see cref="ConfigKind"/>).</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ConfigConstantAttribute(string key) : Attribute
{
    public string Key { get; } = key;

    /// <summary>The export may lack the key; otherwise a client export without it stops the server.</summary>
    public bool Optional { get; init; }
}
