using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MultiverseConstant (data/config/MultiverseConstant.json): a name and its value.</summary>
[ConfigConstants("MultiverseConstant")]
public readonly partial record struct MultiverseConstants(IReadOnlyDictionary<string, JsonElement> Values)
{
    [ConfigConstant("MainStageCountDownTime")] public partial long? MainStageCountDownTime { get; }
    [ConfigConstant("MultiverseDanRewardLimit")] public partial long? MultiverseDanRewardLimit { get; }
    [ConfigConstant("MultiverseMedalMaxWear")] public partial long? MultiverseMedalMaxWear { get; }
    [ConfigConstant("ServerMatchUpCircle")] public partial long? ServerMatchUpCircle { get; }
}
