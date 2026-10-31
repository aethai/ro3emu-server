using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BarroomTypeConfig (data/config/BarroomTypeConfig.json): Tavern room topics (_iType 0) and
/// languages (_iType 1), the ids a room's barRoomType / languageType carry.</summary>
[ConfigTable("BarroomTypeConfig")]
public readonly partial record struct BarroomTypeConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iNameID")] public partial long NameId { get; }
    [ConfigField("_iType")] public partial long Type { get; }
}
