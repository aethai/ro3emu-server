using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's CampFireAffixNewConfig (data/config/CampFireAffixNewConfig.json).</summary>
[ConfigTable("CampFireAffixNewConfig")]
public readonly partial record struct CampFireAffixNewConfig(JsonElement Row)
{
    [ConfigField("_iActionParams")] public partial long[][] ActionParams { get; }
    [ConfigField("_iActionType")] public partial long ActionType { get; }
    /// <summary>Milliseconds after its phase starts that the action runs (the prize draw: 240000).</summary>
    [ConfigField("_iDelayTime")] public partial long DelayTime { get; }
    [ConfigField("_iId")] public partial long Id { get; }
    /// <summary>Milliseconds between the repeats of a repeating action (the Party Blessing: 30000).</summary>
    [ConfigField("_iInterval")] public partial long Interval { get; }
    /// <summary>The trigger's parameters: [chance in 10000ths, the dying NPC] for a monster death.</summary>
    [ConfigField("_iParams")] public partial long[] Params { get; }
    [ConfigField("_iPhase")] public partial long Phase { get; }
    /// <summary>How many times the action runs (the Party Blessing: 9999).</summary>
    [ConfigField("_iTriggerTime")] public partial long TriggerTime { get; }
    [ConfigField("_iTriggerType")] public partial long TriggerType { get; }
}
