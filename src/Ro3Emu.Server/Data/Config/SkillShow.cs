using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's skill show (data/skills/skill_*.json, GameData.SkillTemplate): its timeline.</summary>
internal static class SkillShow
{
    /// <summary>The show's m_kAtkPoints in seconds on its timeline, in table order.</summary>
    public static double[] AttackPoints(JsonElement? show) =>
        show is { } row && row.TryGetProperty("m_kAtkPoints", out var points) && points.ValueKind == JsonValueKind.Array
            ? [.. points.EnumerateArray().Select(point => GameData.Number(point) ?? 0)] : [];

    /// <summary>
    /// The show's timeline: each m_kShows entry's effect id (its [effect, kind] pair) with its m_kPoints time in seconds,
    /// in table order. Both are keyed by the show's state ("1000000"); an empty Lua list exports as {}.
    /// </summary>
    public static (long Effect, double Seconds)[] Timeline(JsonElement? show)
    {
        if (show is not { } row || First(row, "m_kShows") is not { } shows || First(row, "m_kPoints") is not { } points) return [];
        var times = points.EnumerateArray().Select(p => GameData.Number(p) ?? 0).ToArray();
        return [.. shows.EnumerateArray().Select((entry, i) => (Entry: entry, Index: i))
            .Where(e => e.Index < times.Length && e.Entry.ValueKind == JsonValueKind.Array && e.Entry.GetArrayLength() > 0)
            .Select(e => ((long)(GameData.Number(e.Entry[0]) ?? 0), times[e.Index]))];
    }

    /// <summary>The first array under a state-keyed table (m_kShows, m_kPoints), or null.</summary>
    private static JsonElement? First(JsonElement row, string field) =>
        row.TryGetProperty(field, out var table) && table.ValueKind == JsonValueKind.Object
            ? table.EnumerateObject().Select(p => p.Value).Cast<JsonElement?>().FirstOrDefault(v => v?.ValueKind == JsonValueKind.Array)
            : null;

    /// <summary>The show's m_fTotalTime in seconds, or 0 when absent.</summary>
    public static double TotalTime(JsonElement? show) =>
        show is { } row && row.TryGetProperty("m_fTotalTime", out var total) ? GameData.Number(total) ?? 0 : 0;

    /// <summary>The show's m_fAtkRange in meters, or 0 when absent.</summary>
    public static double AttackRange(JsonElement? show) =>
        show is { } row && row.TryGetProperty("m_fAtkRange", out var atk) ? GameData.Number(atk) ?? 0 : 0;

    /// <summary>
    /// Milliseconds after a cast's start from which it can be interrupted, or null if never before its end: m_fInterruptTime on
    /// the client's timeline (LC_Skill_RunTimeContext.calcDynamicTimeLine). m_kState.Time holds the chant's end and the show's
    /// end; a time inside the chant counts as the chant's end.
    /// </summary>
    public static int? InterruptOffset(JsonElement? show, int chantMs, int actionMs) =>
        TimelineOffset(show, "m_fInterruptTime", chantMs, actionMs);

    /// <summary>The same offset for m_fInterruptTime_move, from when a move may end the show (LC_Skill_RunTimeContext).</summary>
    public static int? MoveInterruptOffset(JsonElement? show, int chantMs, int actionMs) =>
        TimelineOffset(show, "m_fInterruptTime_move", chantMs, actionMs);

    private static int? TimelineOffset(JsonElement? show, string field, int chantMs, int actionMs)
    {
        if (show is not { } row || !row.TryGetProperty(field, out var value) || GameData.Number(value) is not { } time || time <= 0
            || !row.TryGetProperty("m_kState", out var state) || !state.TryGetProperty("Time", out var times)
            || times.ValueKind != JsonValueKind.Array || times.GetArrayLength() < 3) return null;
        double chant = GameData.Number(times[1]) ?? 0, end = GameData.Number(times[2]) ?? 0;
        double action = Math.Max(0, (time - chant) / Math.Max(0.001, end - chant));
        return (int)Math.Min(int.MaxValue, Math.Round(chantMs + action * actionMs));
    }
}
