using System.Runtime.CompilerServices;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The memoized build part of a stat sheet (<see cref="CharacterStatsService.Stats(GameSession)"/>). A cast, a swing, a hit
/// taken and a regeneration step read the sheet several times, across work items, while nothing it depends on changes;
/// rebuilding it each time was a fifth of the server's allocation under load (#484). The memo is reused only while every
/// input matches what it was built from, and never longer than <see cref="StatsMemoMaxAgeMs"/>.
/// </summary>
public sealed partial class CharacterStatsService
{
    /// <summary>The longest a memoized build part is reused. It also bounds a change no input below tracks: a function gate
    /// opening at a day boundary (FunctionService CondServerStage), or a state edited in place before its owner's
    /// <see cref="Refresh"/>.</summary>
    private const long StatsMemoMaxAgeMs = 3000;

    /// <summary>Tests (Ro3Emu:Testing:VerifyStatsCache): every reuse of a memoized sheet is checked against a fresh build.</summary>
    private readonly bool _verifyMemo = options?.Value.Testing.VerifyStatsCache == true;

    /// <summary>Sheets built from their inputs (not counting a nested build or a verification): what the memo saves.</summary>
    internal long Builds { get; private set; }

    /// <summary>
    /// The build part of a sheet (before HP, SP and the dynamic properties) and what it was computed from: the progress
    /// and its scalars, the skills and the items by signature, and every state object the attribute providers read, by
    /// reference (the services replace these objects when they commit a change). HP is an input only when a totem's HP
    /// condition (SpecialEffectConfig op 10) read it.
    /// </summary>
    private sealed class StatsMemoEntry
    {
        internal required long BuiltMs, NextBuffExpiryMs;
        internal required RoleProgress Progress;
        internal required int Career, S0, S1, S2, S3, S4, S5;
        internal required long Level, SpeedBonus, Mount, StatusBits, SceneGeneration, Skills, Items;
        internal required long? CurrentHp;
        internal required bool Dead, ReadHp, AllFunctionsOpen, TalentEffects;
        internal required ActiveBuff[] Buffs;
        internal required (long Method, object? Levels) GuildTech;
        internal required object?[] Inputs;
        internal required StatSheet Sheet;
    }

    /// <summary>The state objects the attribute providers read (ExtraAttributes, TalentAttributes, FunctionService gates),
    /// compared by reference. Order matters: <see cref="SameInputs"/> walks the same list.</summary>
    private static object?[] Inputs(GameSession s) =>
    [
        s.Items, s.Profile.EquipLevels, s.Profile.Emblems, s.Profile.Totems, s.Profile.Paragon, s.Profile.CareerSpecial,
        s.Profile.MountState, s.Pets, s.Mounts, s.Season.SeasonTalent, s.Season.SeasonRefine, s.Season.LifeJob,
        s.Season.LifeRuntime, s.SeasonState, s.TalentProcBuffs, s.GmAttributes, s.Tasks, s.Guild, s.Instances.Tower,
        s.Instances.TimedBattle, s.Activities.NewActivities,
    ];

    private (long Method, object? Levels) GuildTechKey(GameSession s) => character.GuildTechKey?.Invoke(s) ?? default;

    private static bool SameInputs(object?[] inputs, GameSession s) =>
        ReferenceEquals(inputs[0], s.Items) && ReferenceEquals(inputs[1], s.Profile.EquipLevels)
        && ReferenceEquals(inputs[2], s.Profile.Emblems) && ReferenceEquals(inputs[3], s.Profile.Totems)
        && ReferenceEquals(inputs[4], s.Profile.Paragon) && ReferenceEquals(inputs[5], s.Profile.CareerSpecial)
        && ReferenceEquals(inputs[6], s.Profile.MountState) && ReferenceEquals(inputs[7], s.Pets)
        && ReferenceEquals(inputs[8], s.Mounts) && ReferenceEquals(inputs[9], s.Season.SeasonTalent)
        && ReferenceEquals(inputs[10], s.Season.SeasonRefine) && ReferenceEquals(inputs[11], s.Season.LifeJob)
        && ReferenceEquals(inputs[12], s.Season.LifeRuntime) && ReferenceEquals(inputs[13], s.SeasonState)
        && ReferenceEquals(inputs[14], s.TalentProcBuffs) && ReferenceEquals(inputs[15], s.GmAttributes)
        && ReferenceEquals(inputs[16], s.Tasks) && ReferenceEquals(inputs[17], s.Guild)
        && ReferenceEquals(inputs[18], s.Instances.Tower) && ReferenceEquals(inputs[19], s.Instances.TimedBattle)
        && ReferenceEquals(inputs[20], s.Activities.NewActivities);

    /// <summary>Learned and granted skill levels (passives, job resonance, talent and function conditions). Both
    /// dictionaries are copied in place on commit (CharacterService.CopyProgress, SkillService.CopySkills).</summary>
    private static long SkillSignature(RoleProgress p)
    {
        long h = p.Skills.Count * 31L + p.GrantedSkills.Count;
        foreach (var (id, level) in p.Skills) h = h * 1_000_003 + id * 131L + level;
        foreach (var (id, level) in p.GrantedSkills) h = h * 1_000_003 + id * 137L + level;
        return h;
    }

    /// <summary>
    /// The items as the build reads them: worn gear (grid above 0), socketed cards (below 0), totems (PackType_Totem),
    /// Soul Echoes (PackType_Emblem) and the items a condition counts (FunctionService CondItemCount, among the watched
    /// entries). A committed change replaces an item's Attrs and Enchantments (CharacterService.CopyItem,
    /// EmblemService.PublishItems) and its slot and stack fields, so those references and fields cover it. Loot stacking in
    /// the bag changes none of them, so it no longer ends the memo.
    /// </summary>
    private long ItemSignature(List<InvItem> items)
    {
        long h = 0, read = 0;
        foreach (var item in items)
        {
            if (item.Grid == 0 && item.Pack is not (TotemPack or EmblemPack) && !character.WatchesStateEntry(item.Entry)) continue;
            read++;
            h = h * 1_000_003 + item.Guid;
            h = h * 31 + item.Entry;
            h = h * 31 + item.Stack;
            h = h * 31 + item.Pack;
            h = h * 31 + item.Grid;
            h = h * 31 + (item.Attrs == null ? 0 : RuntimeHelpers.GetHashCode(item.Attrs));
            h = h * 31 + RuntimeHelpers.GetHashCode(item.Enchantments);
        }
        return h * 31 + read;
    }

    /// <summary>PackType_Totem and PackType_Emblem (Lua_GameDefine), where the build finds totems and Soul Echoes.</summary>
    private const int TotemPack = 4, EmblemPack = 5;

    private bool Unchanged(StatsMemoEntry memo, GameSession session, RoleProgress p, long now)
    {
        if (now < memo.BuiltMs || now - memo.BuiltMs > StatsMemoMaxAgeMs
            // A buff past its expiry stops counting for the talents that check it (SkillParameterRules.HasBuffGroup)
            // before BuffService removes it from the list.
            || now >= memo.NextBuffExpiryMs) return false;
        if (!ReferenceEquals(memo.Progress, p) || memo.Career != p.Career || memo.Level != p.Level
            || memo.S0 != p.Stats[0] || memo.S1 != p.Stats[1] || memo.S2 != p.Stats[2]
            || memo.S3 != p.Stats[3] || memo.S4 != p.Stats[4] || memo.S5 != p.Stats[5]
            || memo.SpeedBonus != session.Combat.SpeedBonus || memo.Mount != MountService.Rider(session)
            || memo.ReadHp && memo.CurrentHp != session.CurrentHp || memo.StatusBits != session.StatusBits
            || memo.Dead != session.Dead || memo.SceneGeneration != session.SceneGeneration
            || memo.AllFunctionsOpen != session.Profile.GmAllFunctionsOpen
            || memo.TalentEffects != session.Season.TalentEffectsEnabled
            || memo.Buffs.Length != session.Buffs.Count || !SameInputs(memo.Inputs, session)
            || GuildTechKey(session) is var tech && (tech.Method != memo.GuildTech.Method
                || !ReferenceEquals(tech.Levels, memo.GuildTech.Levels))) return false;
        for (int i = 0; i < memo.Buffs.Length; i++)
            if (!ReferenceEquals(memo.Buffs[i], session.Buffs[i])) return false;
        return memo.Skills == SkillSignature(p) && memo.Items == ItemSignature(session.Items);
    }

    private StatsMemoEntry Memo(GameSession session, RoleProgress p, long now, bool readHp, StatSheet sheet)
    {
        long nextExpiry = long.MaxValue;
        foreach (var buff in session.Buffs)
            if (buff.ExpireMs > now && buff.ExpireMs < nextExpiry) nextExpiry = buff.ExpireMs;
        return new StatsMemoEntry
        {
            BuiltMs = now, NextBuffExpiryMs = nextExpiry, Progress = p, Career = p.Career, Level = p.Level,
            S0 = p.Stats[0], S1 = p.Stats[1], S2 = p.Stats[2], S3 = p.Stats[3], S4 = p.Stats[4], S5 = p.Stats[5],
            SpeedBonus = session.Combat.SpeedBonus, Mount = MountService.Rider(session), StatusBits = session.StatusBits,
            SceneGeneration = session.SceneGeneration, Skills = SkillSignature(p), Items = ItemSignature(session.Items),
            CurrentHp = session.CurrentHp, Dead = session.Dead, ReadHp = readHp,
            AllFunctionsOpen = session.Profile.GmAllFunctionsOpen, TalentEffects = session.Season.TalentEffectsEnabled, Buffs = [.. session.Buffs], Inputs = Inputs(session),
            GuildTech = GuildTechKey(session),
            Sheet = sheet,
        };
    }

    /// <summary>A build that knows whether a totem's HP condition read the current HP (<see cref="GameSession.StatsReadHp"/>).
    /// Nested builds (the condition's own MaxHP probe) leave the outer build's answer alone.</summary>
    private StatSheet TrackedBuild(GameSession session, RoleProgress p, out bool readHp, bool count = true)
    {
        bool outer = session.StatsBuildDepth++ == 0;
        if (outer) { session.StatsReadHp = false; if (count) Builds++; }
        try
        {
            var sheet = BuildStats(session, p);
            readHp = session.StatsReadHp;
            return sheet;
        }
        finally { session.StatsBuildDepth--; }
    }

    /// <summary>
    /// Ro3Emu:Testing:VerifyStatsCache: a reused sheet must equal a fresh build. The game loop logs and drops a failed
    /// work item, so a difference ends the test process instead of an exception nobody sees: some input changed without
    /// the memo noticing. A buff that expired between the check and the rebuild is not a difference.
    /// </summary>
    private void VerifyMemo(GameSession session, RoleProgress p, StatSheet reused, StatsMemoEntry memo)
    {
        var fresh = TrackedBuild(session, p, out _, count: false);
        if (SameValues(fresh.Data, reused.Data) && SameValues(fresh.Base, reused.Base)) return;
        if (session.NowMs >= memo.NextBuffExpiryMs) return;
        var differs = fresh.Data.Keys.Union(reused.Data.Keys)
            .Where(id => fresh.Data.GetValueOrDefault(id) != reused.Data.GetValueOrDefault(id))
            .Select(id => $"{id}: {reused.Data.GetValueOrDefault(id)} -> {fresh.Data.GetValueOrDefault(id)}");
        Environment.FailFast($"Stale stats memo for role {session.Role?.Id} ({session.NowMs - memo.BuiltMs} ms old): " +
            string.Join(", ", differs));

        static bool SameValues(Dictionary<int, long> a, Dictionary<int, long> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out long v) && v == kv.Value);
    }
}
