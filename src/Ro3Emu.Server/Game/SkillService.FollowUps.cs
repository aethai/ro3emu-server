using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Follow-up skills, which no skill tree teaches: a cast's _kBuff1 gives a status whose BuffConfig._kLogicData names one row
/// of another group, a group in no SkillTreeConfig cell (and in no JobProfessSkillConfig list) whose rows have
/// _iAutoAssembly 1. Shadow Guard (15323) gives "Thousand Shadows Status" (1532201, [1532201] Thousand Shadows 15322 at the
/// same level), Thousand Shadows gives "Shadow Loop Status" (1532301, [1532001] Shadow Loop 15320), and Move back Shadow
/// Echo (15329) gives "Move back Afterimage Status" (1532901, [1533001] its return 15330). No client Lua reads _kLogicData;
/// the client's bar shows DBSkillData.skillWheel and keeps originSkillWheel as the slot's own skill (MG_SkillGrowth.proto
/// DBSkillData 6 and 8, LC_DAL_SkillDevelop._refreshEquipSkillList, LC_System_Skill._onJoystickSkillListChange builds the
/// slots from the first and m_kBaseJoystickSkillIdMap from the second). So while the status lasts, the slot of the row that
/// gave it shows the named row in skillWheel and its origin keeps the row; the row may then be cast, and its cast spends
/// the status (inferred, as the press is the one the status offered).
/// </summary>
public sealed partial class SkillService
{
    private const int MaxFollowUps = 4;
    private Dictionary<long, long>? _followUps;

    /// <summary>Status buff -> the follow-up row it names.</summary>
    private Dictionary<long, long> FollowUps => _followUps ??= BuildFollowUps();

    private Dictionary<long, long> BuildFollowUps()
    {
        var taught = new HashSet<long>();
        foreach (var cell in SkillTreeConfig.All(_data)) taught.UnionWith([cell.X1, cell.X2, cell.X3, cell.X4, cell.X5, cell.X6, cell.X7]);
        var found = new Dictionary<long, long>();
        foreach (var skill in SkillConfig.All(_data))
            foreach (var tuple in skill.Buff1)
                if (tuple is [var status, ..] && BuffConfig.Get(_data, status) is { LogicData: [var named] }
                    && SkillConfig.Get(_data, named) is { BasicType: 1, AutoAssembly: 1 } next
                    && next.SkillId != skill.SkillId && !taught.Contains(next.SkillId))
                    found[status] = named;
        return found;
    }

    private static bool Live(GameSession session, long status) =>
        session.Buffs.Any(b => b.ConfigId == status && b.ExpireMs > MonsterService.NowMs);

    /// <summary>The row a wheel slot holding <paramref name="rowId"/> shows now. The statuses chain, each follow-up row giving
    /// the next (Shadow Guard, Thousand Shadows, Shadow Loop), and a cast spends the status before it: the slot shows the row
    /// of the last live status along the chain.</summary>
    internal long FollowUpOf(GameSession session, long rowId)
    {
        long shown = rowId;
        for (int step = 0; step < MaxFollowUps && SkillConfig.Get(_data, rowId) is { } row; step++)
        {
            long next = 0;
            foreach (var tuple in row.Buff1)
                if (tuple is [var status, ..] && FollowUps.TryGetValue(status, out long named))
                {
                    next = named;
                    if (Live(session, status)) shown = named;
                    break;
                }
            if (next == 0) break;
            rowId = next;
        }
        return shown;
    }

    /// <summary>The live status that offers this row's group and level (a mode row of it included), or null.</summary>
    internal ActiveBuff? FollowUpStatus(GameSession session, SkillConfig row)
    {
        long now = MonsterService.NowMs;
        foreach (var buff in session.Buffs)
            if (buff.ExpireMs > now && FollowUps.TryGetValue(buff.ConfigId, out long named)
                && SkillConfig.Get(_data, named) is { } next && next.SkillId == row.SkillId && next.Level == row.Level)
                return buff;
        return null;
    }

    private void ApplyFollowUps(GameSession session, DBSkillData state)
    {
        foreach (var slot in state.SkillWheel.WhellSkills.Values)
            if (slot.SkillConfigId > 0) slot.SkillConfigId = (int)FollowUpOf(session, slot.SkillConfigId);
        ApplyBuffModes(session, state);
        session.FollowUpsShown = ShownStatuses(session);
    }

    private string ShownStatuses(GameSession session) =>
        string.Join(',', session.Buffs.Where(b => b.ExpireMs > MonsterService.NowMs && FollowUps.ContainsKey(b.ConfigId)).Select(b => b.ConfigId).Order())
        + "|" + ShownModes(session);

    private Dictionary<(long Group, long Level, long Mode), long>? _modeRows;

    /// <summary>The configured mode rows (SkillConfig._iMode above 0) by group, level and mode.</summary>
    private Dictionary<(long Group, long Level, long Mode), long> ModeRows => _modeRows ??= SkillConfig.All(_data)
        .Where(r => r.Mode > 0).GroupBy(r => (r.SkillId, r.Level, r.Mode)).ToDictionary(g => g.Key, g => g.First().Id);

    /// <summary>The mode (BuffConfig type 15) a live buff gives this row's group, and the latest end of the buffs that give it;
    /// 0 when no buff does (a mode from a talent or a mount is told by 31132 and 31027, not by the wheel). A buff that lasts until
    /// it is spent (<see cref="ActiveBuff.UntilSpent"/>) gives the mode no end: <paramref name="end"/> is then 0.</summary>
    private long BuffMode(GameSession session, SkillConfig row, out long end)
    {
        end = 0;
        bool given = false, untilSpent = false;
        long now = MonsterService.NowMs;
        int group = (int)row.SkillId;
        var tags = SkillParameterRules.CategoriesOf(_data, row.Row);
        foreach (var buff in session.Buffs)
            if (buff.ExpireMs > now && GivesMode(buff, group, tags))
            {
                given = true;
                untilSpent |= buff.UntilSpent;
                end = Math.Max(end, buff.ExpireMs);
            }
        if (!given) return 0;
        if (untilSpent) end = 0; // (inferred) a mode that lasts until spent has no end to count down to
        long mode = SkillParameterRules.Mode(group, tags, SkillParameterRules.Active(session, _data));
        return ModeRows.ContainsKey((row.SkillId, row.Level, mode)) ? mode : 0;
    }

    private bool GivesMode(ActiveBuff buff, int group, int[] tags) =>
        SkillParameterRules.BuffModifiers(_data, buff.ConfigId).Any(mod => mod.Type == 15 && SkillParameterRules.Matches(mod, group, tags));

    /// <summary>While a buff selects a mode, the slot of that skill shows the mode's row with the buff's end as its countdown
    /// (Genesis 5025: Focused Arrow Strike under Unlimited Shooting is the mode 1 row, enabled, mode 1, highlightEndTimeMs the
    /// buff's end); originSkillWheel keeps the skill's own row. A mode with no end (a buff until spent: Cannon Enhance on Arm Cannon)
    /// sends highlightEndTimeMs 0, which UI_Sys_Main_JoyStickSkillItemWidgetBaseCtrl._refreshSkillHighCountDown draws no countdown for;
    /// the server's bound sent as the end showed 3598 seconds counting down.</summary>
    private void ApplyBuffModes(GameSession session, DBSkillData state)
    {
        foreach (var (key, slot) in state.SkillWheel.WhellSkills)
        {
            if (key < 1 || key > BarSlots || SkillConfig.Get(_data, slot.SkillConfigId) is not { Mode: 0 } row) continue;
            long mode = BuffMode(session, row, out long end);
            if (mode == 0 || !ModeRows.TryGetValue((row.SkillId, row.Level, mode), out long modeRow)) continue;
            slot.SkillConfigId = (int)modeRow;
            slot.Enable = true;
            slot.Mode = (int)mode;
            slot.HighlightEndTimeMs = end;
        }
    }

    private string ShownModes(GameSession session)
    {
        if (session.Progress == null) return "";
        var shown = new List<string>();
        foreach (int group in session.Progress.SkillBar.Values.Distinct().Order())
            if (Level(session.Progress, group) is var level and > 0 && SkillConfig.Get(_data, RealId(group, level)) is { } row
                && BuffMode(session, row, out long end) is var mode and > 0)
                shown.Add($"{group}:{mode}:{end}");
        return string.Join(',', shown);
    }

    /// <summary>After a buff change: a follow-up status that came or went changes the wheel the client was sent.</summary>
    internal async Task RefreshFollowUpsAsync(GameSession session)
    {
        if (session.SkillPlans == null || !session.Synced.ContainsKey(SkillDataSync) || ShownStatuses(session) == session.FollowUpsShown) return;
        await SyncAsync(session);
    }
}
