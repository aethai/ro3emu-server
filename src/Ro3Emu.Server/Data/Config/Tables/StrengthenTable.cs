using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's StrengthenTable (data/config/StrengthenTable.json).</summary>
[ConfigTable("StrengthenTable")]
public readonly partial record struct StrengthenTable(JsonElement Row)
{
    [ConfigField("_iEquipType")] public partial long EquipType { get; }
    [ConfigField("_iPower", Optional = true)] public partial long Power { get; }
    [ConfigField("_iStrengthenInterval")] public partial long StrengthenInterval { get; }
}
