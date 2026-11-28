using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Which client functions (enum_FunctionType: bag, skills, party, chat channels...) are open: the 5056 FunctionOpenInfo
/// dataset, computed from FunctionConfig._kOpenCondition → LC_ConditionList sub-conditions [type, param...]:
/// 1002 base level ≥ param, 1019 job rank ≥ param (inferred), 1101 role has a running task, 3031 server-open days, 3030 playbook-open seconds,
/// 4004 quest delivered (TaskService.IsDelivered), 1014 cleared Spirit Tower floor, 11001 active life skill, 1102 learned skill. Resent when the level or
/// job changes, so functions unlock as the player grows.
/// </summary>
public sealed partial class FunctionService
{
    public const uint FunctionOpenSync = 5056;
    public const int SkillFunction = 10, SeasonTalentFunction = 1003; // FunctionConfig: skills and season talents

    private const int CondBaseLevel = 1002, CondTowerFloor = 1014, CondJobRank = 1019, CondRoleHasTask = 1101,
        CondLearnedSkill = 1102, CondServerStage = 3031, CondServerStage2 = 3030, CondQuestDone = 4004, CondItemCount = 1020;
    private const int CondJourneyAttr = 15100, CondJourneyDialogue = 15101;
    private const int CondGuildPosition = 9007, CondGuildBuildingLevel = 9008;
    private const int CondChickenRank = 6508;

    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly SkillService _skills;
    private readonly SeasonService _seasons;
    private readonly TimeProvider _clock;

    public FunctionService(GameData data, CharacterService character, SkillService skills, SeasonService seasons, TimeProvider clock)
    {
        this._data = data;
        this._character = character;
        this._skills = skills;
        this._seasons = seasons;
        this._clock = clock;
        character.ProgressHandlers.Subscribe(SyncAsync);
        // CondItemCount conditions (functions, talent trees, quests) count these items.
        character.WatchStateEntries(() => LCConditionList.All(data).SelectMany(row => row.SubConditionValues(CondItemCount)));
        // LC_DAL_ServerInfo.PackServerBasicInfo: iOpenDayCount (the server level and EXP catch-up read it).
        character.ServerDay = ServerDay;
    }

    /// <summary>The server's open day as the client's iOpenDayCount: day 1 is the season start.</summary>
    public int ServerDay() =>
        (int)Math.Clamp(Math.Floor((_clock.GetUtcNow().ToUnixTimeSeconds() - _seasons.StartTime) / 86400d) + 1, int.MinValue, int.MaxValue);

    /// <summary>5056. A quest flush after every kill asks for it; when the open set is what the client was last sent,
    /// nothing is built and only the version advances, as an empty change does (CharacterService.SyncAsync).</summary>
    public Task SyncAsync(GameSession session)
    {
        if (session.Profile.DeferFunctionSync) return Task.CompletedTask;
        if (session.Synced.GetValueOrDefault(FunctionOpenSync) is FunctionOpenInfo sent && SameOpenSet(session, sent))
        {
            CharacterService.NextVersion(session, FunctionOpenSync);
            return Task.CompletedTask;
        }
        return CharacterService.SyncAsync(session, FunctionOpenSync, Build(session));
    }

    public FunctionOpenInfo Build(GameSession session)
    {
        var info = new FunctionOpenInfo();
        foreach (long id in FunctionIds)
            if (Shown(session, id)) info.FunctionOpenInfos[id] = true;
        return info;
    }

    /// <summary>FunctionConfig's ids in table order, read once: every kill's quest flush walks them.</summary>
    private long[] FunctionIds => _functionIds ??= [.. _data.Table("FunctionConfig").Values.Select(row => GameData.Int(row, "_iID"))];
    private long[]? _functionIds;

    /// <summary>A function the client is told is open. To-Do/Dodo read 5004 and 5059 immediately on unlock; hold only these
    /// two until the daily login sync.</summary>
    private bool Shown(GameSession session, long id) =>
        id > 0 && (session.Activities.DailyReady || id is not (25 or 5902)) && IsOpen(session, id);

    /// <summary><see cref="Build"/> would give exactly <paramref name="sent"/>: every shown function is there, and nothing else.</summary>
    private bool SameOpenSet(GameSession session, FunctionOpenInfo sent)
    {
        int shown = 0;
        foreach (long id in FunctionIds)
        {
            if (!Shown(session, id)) continue;
            if (!sent.FunctionOpenInfos.TryGetValue(id, out bool open) || !open) return false;
            shown++;
        }
        return shown == sent.FunctionOpenInfos.Count;
    }

    /// <summary>A FunctionConfig row's open condition holds (a function without a row is not gated).</summary>
    public bool IsOpen(GameSession session, long functionId)
    {
        if (session.Profile.GmAllFunctionsOpen) return true; // GM panel openallfunc (GmCommands.Presets)
        // Function 1003 has parent 10 and delivered-task gate 10103160.
        // TaskService's no-quest fallback is permissive, so this feature specifically fails closed.
        if (functionId == SeasonTalentFunction && (session.Tasks == null
            || !_data.Table("SeasonTalentTreeConfig").Values.Any(r => GameData.Int(r, "_iId") > 0)
            || !_data.Table("SeasonTalentConfig").Values.Any(r => GameData.Int(r, "_iId") > 0)
            || !IsOpen(session, SkillFunction)
            || !TaskService.IsDelivered(session, 10101170) || !TaskService.IsDelivered(session, 10103160))) return false;
        // OpenContent: functions the original never opened (level 999/9999) whose client panel works.
        if (_seasons.OpenContent.Forces(functionId)) return true;
        return _data.Row("FunctionConfig", functionId) is not { } row || ConditionMet(session, GameData.Int(row, "_kOpenCondition"));
    }

    /// <summary>
    /// A service that owns the meaning of particular condition ids (AdventureGroupService: the group quests' 88000081-85,
    /// level 999 in this export) decides them; null leaves the condition to the table.
    /// </summary>
    public Func<GameSession, long, bool?>? ConditionOverride { get; set; }

    /// <summary>The level of a building of the session's guild (GuildBuildingsInfo[type].iLevel), null when the session has no
    /// guild or the guild no such building. GuildService sets it.</summary>
    public Func<GameSession, long, long?>? GuildBuildingLevel { get; set; }

    /// <summary>An LC_ConditionList entry (also TaskConfig._iCondition): every sub-condition holds.</summary>
    public bool ConditionMet(GameSession session, long conditionId)
    {
        if (conditionId == 0) return true;
        if (ConditionOverride?.Invoke(session, conditionId) is { } decided) return decided;
        if (_data.Row("LC_ConditionList", conditionId) is not { } cond) return false;
        if (!cond.TryGetProperty("m_kSubConditionList", out var subs) || subs.ValueKind != JsonValueKind.Array) return true;
        var p = session.Progress!;
        foreach (var sub in subs.EnumerateArray())
        {
            if (!sub.TryGetProperty("m_kData", out var d) || d.ValueKind != JsonValueKind.Array || d.GetArrayLength() == 0) continue;
            int type = (int)(GameData.Number(d[0]) ?? 0);
            long param = d.GetArrayLength() > 1 ? (long)(GameData.Number(d[1]) ?? 0) : 0;
            bool ok = type switch
            {
                CondBaseLevel => p.Level >= param,
                CondTowerFloor => ClearedTowerFloor(session) >= param,
                CondJobRank => (_data.Row("JobConfig", p.Career) is { } job ? GameData.Int(job, "_iJobRank") : 1) >= param,
                // LC_InteractCondition.CheckRoleHaveTask:732-752 requires the named task to be Running.
                CondRoleHasTask => session.Tasks is { } tasks && tasks.Infos.TryGetValue(param, out var task)
                    && task.Status == TaskState.Running,
                // LC_InteractCondition.CheckHasLearnedSkill [1102, skill] (:3510-3539): ServerAllSkills[skill] > 0, the
                // current plan's skillAssigns level of that skill group (the rental side quests' 88000101-106).
                CondLearnedSkill => param is > 0 and <= int.MaxValue && _skills.Level(p, (int)param) > 0,
                // LC_InteractCondition.lua:1957-1979 checks iOpenDayCount. Start is the server's first day.
                // OpenContent counts every server-day gate as met from the first day.
                CondServerStage or CondServerStage2 when _seasons.OpenContent.ServerDayFree => true,
                CondServerStage => Math.Floor((_clock.GetUtcNow().ToUnixTimeSeconds() - _seasons.StartTime) / 86400d) + 1 >= param,
                // LC_InteractCondition.CheckPlayBook: GetPlayBookOpenTime() + offset <= server system time.
                CondServerStage2 => _clock.GetUtcNow().ToUnixTimeSeconds() >= _seasons.StartTime + param,
                CondQuestDone => TaskService.IsDelivered(session, param),
                // LC_InteractCondition.CheckItemCount: [1020, item, count], LC_ItemHelper.GetItemCount(item) >= count.
                CondItemCount => d.GetArrayLength() >= 3 && _character.CountOf(session, (int)param) >= (long)(GameData.Number(d[2]) ?? 0),
                // LC_InteractCondition.CheckJourneyAttr / CheckJourneyDialogue: per-role Journey progress.
                CondJourneyAttr => JourneyAttributeMet(session, d),
                CondJourneyDialogue => JourneyDialogueMet(session, d),
                // LC_InteractCondition.CheckLifeLevel: both the stored level AND active-slot membership.
                11001 => d.GetArrayLength() >= 3 && LifeJobService.ActiveLevel(session, param) >= (long)(GameData.Number(d[2]) ?? 0),
                1100 => param != conditionId && IsOpen(session, param),
                // LC_InteractCondition.CheckGuildPosition [9007, op, position] (lua :2809-2848): the guild position
                // compared with the operator table (:313-356, 1 ==, 2 ~=, 3 >, 4 >=, 5 <, 6 <=); the Guild Store's
                // packs are [9007,6,3], Guild Master to Vice Guild Master. No guild: false.
                CondGuildPosition => session.Guild is { GuildId: > 0 } guild && d.GetArrayLength() >= 3
                    && Compare(param, guild.Official, (long)(GameData.Number(d[2]) ?? 0)),
                // LC_InteractCondition.CheckGuildBuildingLevel [9008, building, op, level] (:2908-2950): the guild's
                // GuildBuildingsInfo[building].iLevel compared with the operator table; no guild or building: false.
                CondGuildBuildingLevel => d.GetArrayLength() >= 4 && GuildBuildingLevel?.Invoke(session, param) is { } built
                    && Compare((long)(GameData.Number(d[2]) ?? 0), built, (long)(GameData.Number(d[3]) ?? 0)),
                // LC_InteractCondition.CheckChickenRankLevel [6508, op, rank] (:2096-2119): the Survivor Battle rank
                // (LC_ChickenHelper.GetCurRankInfo) against the rank, 1 >=, 2 <=, 3 ==.
                CondChickenRank => d.GetArrayLength() >= 3 && session.Instances.TimedBattle is { } battle
                    && (long)(GameData.Number(d[2]) ?? 0) is var rank && param switch
                    {
                        1 => battle.ChickenRank >= rank,
                        2 => battle.ChickenRank <= rank,
                        3 => battle.ChickenRank == rank,
                        _ => false,
                    },
                _ => false,
            };
            bool inverse = sub.TryGetProperty("m_bInverse", out var inv) && inv.ValueKind == JsonValueKind.True;
            if (ok == inverse) return false; // all sub-conditions must hold (m_eType 1)
        }
        return true;
    }

    /// <summary>LC_InteractCondition operator table (lua :313-356): 1 ==, 2 ~=, 3 &gt;, 4 &gt;=, 5 &lt;, 6 &lt;=.</summary>
    private static bool Compare(long op, long value, long param) => op switch
    {
        1 => value == param,
        2 => value != param,
        3 => value > param,
        4 => value >= param,
        5 => value < param,
        6 => value <= param,
        _ => false,
    };

    private static bool JourneyAttributeMet(GameSession session, JsonElement values)
    {
        if (values.GetArrayLength() < 4 || session.Activities.NewActivities?.ActivityInfo.GetValueOrDefault(112)?.ActivityParam?.Journey is not { } journey) return false;
        long attribute = (long)(GameData.Number(values[1]) ?? 0);
        long min = (long)(GameData.Number(values[2]) ?? 0), max = (long)(GameData.Number(values[3]) ?? 0);
        long current = journey.Attributes.GetValueOrDefault(attribute);
        return attribute > 0 && min <= current && current <= max;
    }

    private static bool JourneyDialogueMet(GameSession session, JsonElement values)
    {
        if (values.GetArrayLength() < 2 || session.Activities.NewActivities?.ActivityInfo.GetValueOrDefault(112)?.ActivityParam?.Journey is not { } journey) return false;
        for (int index = 1; index < values.GetArrayLength(); index++)
            if (journey.CompleteAct.ContainsKey((long)(GameData.Number(values[index]) ?? 0))) return true;
        return false;
    }

    /// <summary>
    /// LC_InteractCondition.CheckClimbTowerLayer compares the cleared display floor.
    /// DBClimTower.layerConfID is a PetFloorConfig key, never itself a floor number; a missing row means no clear.
    /// </summary>
    private long ClearedTowerFloor(GameSession session) =>
        session.Instances.Tower is { LayerConfID: var configId } && _data.Row("PetFloorConfig", configId) is { } row
            ? GameData.Int(row, "_iFloor") : 0;
}
