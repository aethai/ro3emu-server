using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TradeCredibilityConfig (data/config/TradeCredibilityConfig.json): the Exchange reputation tiers,
/// each a score range with its icon and text (UI_Sys_TradeReputationWidgetCtrl).</summary>
[ConfigTable("TradeCredibilityConfig")]
public readonly partial record struct TradeCredibilityConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_kCredibilityScores")] public partial long[] CredibilityScores { get; }
}
