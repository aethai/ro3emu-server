using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildWarTechConfig (data/config/GuildWarTechConfig.json): one level of a Guild War Technology.
/// Lua_CFG_GuildWarTechConfig._initAllData groups the rows by _iGroup and _iLv; the client sends the group as the techId.</summary>
[ConfigTable("GuildWarTechConfig")]
public readonly partial record struct GuildWarTechConfig(JsonElement Row)
{
    [ConfigField("_iGroup")] public partial long Group { get; }
    [ConfigField("_iLv")] public partial long Level { get; }
    /// <summary>An LC_ConditionList id the next level needs (LC_GuildHelper.GetTechStatus).</summary>
    [ConfigField("_iLimit")] public partial long Limit { get; }
    /// <summary>[item, count]: 126, Guild Supplies.</summary>
    [ConfigField("_iCostMoney")] public partial long[] CostMoney { get; }
    /// <summary>SpecialEffectConfig ids of this level: the level's whole effect, not the step from the level below.</summary>
    [ConfigField("_iSpSkillID")] public partial long[] SpecialEffects { get; }
    /// <summary>Where the effect applies: 1000 Stronghold Battle, 1001 Convoy Escort Battle, 1002 Siege Battle.</summary>
    [ConfigField("_iPlayingMethodID")] public partial long[] PlayingMethods { get; }
}
