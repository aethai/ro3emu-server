using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GVGConfig (data/config/GVGConfig.json): a name and its value.</summary>
[ConfigConstants("GVGConfig")]
public readonly partial record struct GvgConstants(IReadOnlyDictionary<string, JsonElement> Values)
{
    [ConfigConstant("GVGEmperiumReviveTime")] public partial long? GVGEmperiumReviveTime { get; }
    [ConfigConstant("GVGEndAutoLeaveTime")] public partial long? GVGEndAutoLeaveTime { get; }
    [ConfigConstant("GVGFieldBuff")] public partial long? GVGFieldBuff { get; }
    [ConfigConstant("GVGFieldGuardTime")] public partial long? GVGFieldGuardTime { get; }
    [ConfigConstant("GVGFieldOccupyAmount")] public partial long? GVGFieldOccupyAmount { get; }
    [ConfigConstant("GVGFieldOccupyMaxSpeed")] public partial long? GVGFieldOccupyMaxSpeed { get; }
    [ConfigConstant("GVGFieldOccupySpeed")] public partial long? GVGFieldOccupySpeed { get; }
    [ConfigConstant("GVGFieldResourceGrowInterval")] public partial long? GVGFieldResourceGrowInterval { get; }
    [ConfigConstant("GVGFieldResourceGrowNum")] public partial long? GVGFieldResourceGrowNum { get; }
    [ConfigConstant("GVGGuildResourceAutoGrowNum")] public partial long? GVGGuildResourceAutoGrowNum { get; }
    [ConfigConstant("GVGResourceLimit")] public partial long? GVGResourceLimit { get; }
    [ConfigConstant("GVGReviveCountDown")] public partial long? GVGReviveCountDown { get; }
}
