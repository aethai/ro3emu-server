using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>ActivityConstant.ExploreNotes and ExploreNoteDropRestrict govern the two random sources listed
/// by the Movement rules. Field conches pay their own award and do not spend this restriction.</summary>
internal sealed class ExplorationNoteDrops(GameData data, SeasonService seasons, TimeProvider clock, Func<GameSession, bool> open)
{
    internal sealed record Roll(int Item, long DayStart);
    internal Func<int, int> Draw { get; set; } = Random.Shared.Next;

    private int Value(string key) => data.Table("ActivityConstant").TryGetValue(key, out var value)
        ? (int)(GameData.Number(value) ?? 0) : 0;

    // RestrictConfig recover type 1: use the same 05:00 realm day as the other life restrictions (inferred).
    internal (long Start, long End) Day()
    {
        var today = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), seasons.Zone).DateTime.AddHours(-5).Date.AddHours(5);
        return (seasons.Unix(today), seasons.Unix(today.AddDays(1)));
    }

    internal RestrictInfo? Remaining(GameSession s)
    {
        if (s.Restrictions == null || !open(s)) return null;
        int id = Value("ExploreNoteDropRestrict");
        if (RestrictConfig.Get(data, id) is not { RecoverType: 1, MaxRestrictNum: > 0 } rule) return null;
        var (start, end) = Day();
        var saved = s.Restrictions.RestrictCount.GetValueOrDefault(id);
        long left = saved?.LastRecoverTime == start ? Math.Clamp(saved.Count, 0, rule.MaxRestrictNum) : rule.MaxRestrictNum;
        return new RestrictInfo
        {
            Id = id, Count = left, LimitCount = rule.MaxRestrictNum,
            HistoryUseCount = rule.MaxRestrictNum - left, LastRecoverTime = start, NextRecoverTime = end
        };
    }

    internal Roll? Choose(GameSession s)
    {
        if (Remaining(s) is not { Count: > 0 } left) return null;
        int catchUp = Value("ExploreCatchUpDays");
        // (inferred) Options are basis points per successful action, catch-up begins on the numbered season day.
        int day = (TimeZoneInfo.ConvertTime(clock.GetUtcNow(), seasons.Zone).Date - seasons.Start.Date).Days + 1;
        int chance = Value(catchUp > 0 && day >= catchUp ? "ExploreCatchUpOptions" : "ExploreBaseOptions");
        if (chance is <= 0 or > 10000 || Draw(10000) >= chance) return null;
        if (!data.Table("ActivityConstant").TryGetValue("ExploreNotes", out var notes)
            || notes.ValueKind != JsonValueKind.Array || notes.GetArrayLength() == 0) return null;
        // (inferred) No per-note weights are supplied in ExploreNotes.
        int item = (int)(GameData.Number(notes[Draw(notes.GetArrayLength())]) ?? 0);
        return item > 0 && ItemConfig.Get(data, item) != null ? new(item, left.LastRecoverTime) : null;
    }

    // Recheck the shared balance at settlement. An old roll cannot spend tomorrow's allowance.
    internal RestrictInfo? Spend(GameSession s, Roll? roll)
    {
        if (roll == null || Remaining(s) is not { Count: > 0 } left || left.LastRecoverTime != roll.DayStart) return null;
        left.Count--;
        left.HistoryUseCount++;
        return left;
    }
}
