using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's ActivityConstant (data/config/ActivityConstant.json): a name and its value.</summary>
[ConfigConstants("ActivityConstant")]
public readonly partial record struct ActivityConstants(IReadOnlyDictionary<string, JsonElement> Values)
{
    [ConfigConstant("PronteraDefenseMainCityDefendAward")]
    public partial long? PronteraDefenseMainCityDefendAward { get; }
    [ConfigConstant("PronteraDefenseOtherCityDefendAward")]
    public partial long? PronteraDefenseOtherCityDefendAward { get; }
    [ConfigConstant("SeasonEveBarrageCacheCount")] public partial long? SeasonEveBarrageCacheCount { get; }
    [ConfigConstant("SeasonEveChapterNoteCount")] public partial long? SeasonEveChapterNoteCount { get; }
    [ConfigConstant("SeasonEveCompleteChapterProgress")] public partial long? SeasonEveCompleteChapterProgress { get; }
    [ConfigConstant("SeasonEveNoWishAward")] public partial long? SeasonEveNoWishAward { get; }
    [ConfigConstant("SeasonEveProgressAward")] public partial long[][]? SeasonEveProgressAward { get; }
    [ConfigConstant("SeasonEveProgressCheckAddMax")] public partial long? SeasonEveProgressCheckAddMax { get; }
    [ConfigConstant("SeasonEveProgressCheckCoefficient")]
    public partial long[][]? SeasonEveProgressCheckCoefficient { get; }
    [ConfigConstant("SeasonEveProgressCheckInterval")] public partial long? SeasonEveProgressCheckInterval { get; }
    [ConfigConstant("SeasonEveProgressCheckPoint")] public partial long[][]? SeasonEveProgressCheckPoint { get; }
    [ConfigConstant("SeasonEveProgressFullTime")] public partial long? SeasonEveProgressFullTime { get; }
    [ConfigConstant("SeasonEveProgressMailId")] public partial long? SeasonEveProgressMailId { get; }
    [ConfigConstant("SeasonEveTotalProgress")] public partial long? SeasonEveTotalProgress { get; }
    [ConfigConstant("SeasonEveWishMailId")] public partial long? SeasonEveWishMailId { get; }
}
