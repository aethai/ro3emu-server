using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Client presentation and dynamic parameters for actual combat events.</summary>
public sealed partial class CombatService
{
    public const uint SkillSlotModify = 31027;
    public const uint UseInSkillHigh = 31106, ExtraUseInSkillHigh = 31107;
    public const uint SkillBulletHigh = 31108, SkillEffectHigh = 31109;
    public const uint SkillAffectedLegacy = 31118, SkillEffect = 31119, SkillBullet = 31120;
    public const uint ExtraUseInSkill = 31121, SkillSelectionChange = 31122;
    public const uint BossSkillCd = 31125, BossClearSkillCds = 31126, BossAllSkillCds = 31127;
    public const uint DamageStatistics = 31129, SkillAffectedEx = 31132, TrainingOperation = 31133;

    /// <summary>Transient state is scoped to a role/connection; never leaks to another selected role.</summary>
    public sealed class ProtocolState
    {
        public Dictionary<int, int> TemporarySlotRows { get; } = [];
        public string ParameterSignature { get; set; } = "";
        /// <summary>Skill mode (BuffConfig type 15) last told per group, for the cooldown entries the client needs when a mode starts or ends.</summary>
        public Dictionary<int, int> GroupModes { get; } = [];
        public bool TrainingPaused { get; set; }
        public Dictionary<(long Target, bool White, int Tag), bool> SkillSelection { get; } = [];
    }

    private Dictionary<(int Group, int Level, int Mode), int>? _configuredModeRows;

    private Dictionary<(int Group, int Level, int Mode), int> ConfiguredModeRows =>
        _configuredModeRows ??= _data.Table("SkillConfig").Values
            .Where(r => GameData.Int(r, "_iSkillID") > 0 && GameData.Int(r, "_iLevel") > 0)
            .GroupBy(r => ((int)GameData.Int(r, "_iSkillID"), (int)GameData.Int(r, "_iLevel"), (int)GameData.Int(r, "_iMode")))
            .ToDictionary(g => g.Key, g => (int)GameData.Int(g.First(), "_iID"));

    /// <summary>
    /// The effective configured row for an actual cast. BuffConfig type 15 selects a mode;
    /// a mode can never be selected merely by submitting its row id (inferred server gate).
    /// </summary>
    public JsonElement? EffectiveSkillRow(GameSession session, int requestedRowId)
    {
        if (session.Progress == null || _data.Row("SkillConfig", requestedRowId) is not { } requested) return null;
        int group = (int)GameData.Int(requested, "_iSkillID"), level = (int)GameData.Int(requested, "_iLevel");
        int mode = SkillParameterRules.Mode(group, SkillParameterRules.CategoriesOf(_data, requested), SkillParameterRules.Active(session, _data));
        int requestedMode = (int)GameData.Int(requested, "_iMode");
        if (requestedMode != 0 && requestedMode != mode) return null;
        if (mode != 0 && ConfiguredModeRows.TryGetValue((group, level, mode), out int selectedId)
            && _data.Row("SkillConfig", selectedId) is { } selected)
            return SkillParameterRules.Effective(session, _data, selected);
        if (requestedMode != 0) return null;
        return SkillParameterRules.Effective(session, _data, requested);
    }

    /// <summary>A group's configured row of a level in the mode the session's type 15 tuples select, or its mode 0 row when
    /// that mode has none. The tuples' other fields are not applied: <see cref="CastExtraAsync"/> applies them.</summary>
    internal JsonElement? ModeRow(GameSession session, int group, int level)
    {
        if (!ConfiguredModeRows.TryGetValue((group, level, 0), out int plain) || _data.Row("SkillConfig", plain) is not { } row) return null;
        int mode = SkillParameterRules.Mode(group, SkillParameterRules.CategoriesOf(_data, row), SkillParameterRules.Active(session, _data));
        return ConfiguredModeRows.TryGetValue((group, level, mode), out int selected) ? _data.Row("SkillConfig", selected) : row;
    }

    public bool SkillModeAllowed(GameSession session, JsonElement row) =>
        EffectiveSkillRow(session, (int)GameData.Int(row, "_iID")) is { } effective
        && GameData.Int(effective, "_iID") == GameData.Int(row, "_iID");

    /// <summary>SkillConfig._iQueue plus BuffConfig type 44; never trusts a request's queue claim.</summary>
    public bool CanQueueSkill(GameSession session, int group)
    {
        if (session.Progress == null || _skills.Level(session.Progress, group) is not > 0) return false;
        int rowId = _skills.RealId(group, _skills.Level(session.Progress, group));
        if (_data.Row("SkillConfig", rowId) is not { } row) return false;
        long allowed = GameData.Int(row, "_iQueue");
        var tags = SkillParameterRules.CategoriesOf(_data, row);
        foreach (var mod in SkillParameterRules.Active(session, _data))
            if (mod.Type == 44 && SkillParameterRules.Matches(mod, group, tags)) allowed = SkillParameterRules.Apply(allowed, mod);
        return allowed != 0;
    }

    /// <summary>Reconcile actual buff/energy-derived parameters and wheel replacements after login/skill/buff changes.</summary>
    public async Task RefreshSkillParametersAsync(GameSession session)
    {
        if (session.Role == null || session.Progress == null) return;
        var passives = PassiveModifiers(session);
        if (!passives.SequenceEqual(session.PassiveSkillModifiers)) session.PassiveSkillModifiers = passives;
        var mods = SkillParameterRules.Active(session, _data);
        var update = new SkillBeAffectedParamExNotify { IsAll = true, Uuid = session.Role.Id };
        // Scope 0 names its group; a scope-1 tag selector reaches every learned skill whose row carries its tags.
        var groups = new SortedSet<int>(mods.Where(m => m.Scope == 0).Select(m => m.Group));
        if (mods.Any(m => m.Scope == 1))
            foreach (int learned in session.Progress.Skills.Keys)
                if (_skills.Level(session.Progress, learned) is var at and > 0
                    && _data.Row("SkillConfig", _skills.RealId(learned, at)) is { } learnedRow
                    && mods.Any(m => m.Scope == 1 && SkillParameterRules.Matches(m, learned, SkillParameterRules.CategoriesOf(_data, learnedRow))))
                    groups.Add(learned);
        var modes = new Dictionary<int, int>();
        foreach (var group in groups)
        {
            int level = _skills.Level(session.Progress, group);
            if (level <= 0 || _data.Row("SkillConfig", _skills.RealId(group, level)) is not { } baseRow) continue;
            var row = EffectiveSkillRow(session, (int)GameData.Int(baseRow, "_iID")) ?? baseRow;
            var tags = SkillParameterRules.CategoriesOf(_data, baseRow);
            var groupInfo = new BeAffectedParamInfoEx();
            foreach (var type in mods.Where(m => SkillParameterRules.Matches(m, group, tags)).Select(m => m.Type).Distinct().Order())
            {
                if (type == 15) groupInfo.AffectParam[type] = new BeAffectedValueEx { AffectValue = modes[group] = SkillParameterRules.Mode(group, tags, mods) };
                else if (type == 44) groupInfo.AffectParam[type] = new BeAffectedValueEx { AffectValue = CanQueueSkill(session, group) ? 1 : 0 };
                else if (SkillParameterRules.Field(type) is { } field)
                {
                    var value = new BeAffectedValueEx();
                    if (type is 29 or 30)
                    {
                        if (row.TryGetProperty(field, out var array) && array.ValueKind == JsonValueKind.Array)
                            foreach (var v in array.EnumerateArray()) value.AffectValueList.Add((long)(GameData.Number(v) ?? 0));
                    }
                    else value.AffectValue = GameData.Int(row, field);
                    groupInfo.AffectParam[type] = value;
                }
            }
            if (groupInfo.AffectParam.Count > 0) update.AffectParamDict[group] = groupInfo;
        }
        // Empty proto maps may decode as Lua nil. A zero-group entry makes isAll clear the old map safely;
        // LC_DynamicParamMgr.ProcessDynamicSkillParam ignores its absent affectParam.
        if (update.AffectParamDict.Count == 0) update.AffectParamDict[0] = new BeAffectedParamInfoEx();
        string signature = Convert.ToHexString(update.ToByteArray());
        if (session.SkillProtocol.ParameterSignature != signature)
        {
            session.SkillProtocol.ParameterSignature = signature;
            await SendParametersWithModeCooldownsAsync(session, update, modes);
        }
        await RefreshTemporarySlotsAsync(session);
    }

    /// <summary>
    /// The 31132 parameters, with the 31113 cooldown entries of the modes (type 15) that start or end. Genesis files a
    /// cooldown entry under the new mode before it tells the mode, and another after the mode ends: the client keys its
    /// cooldowns by group * 10000 + mode (LC_Skill_Function.GetSkillCDInfoKey) and its bar reads the key of the mode row
    /// it shows, so without an entry the replaced skill (Focused Arrow Strike under Unlimited Shooting) neither swaps
    /// nor lights up. Each entry tells the cooldown the cast check holds for that key (<see cref="CooldownEnd"/>).
    /// </summary>
    private static async Task SendParametersWithModeCooldownsAsync(GameSession session, SkillBeAffectedParamExNotify update, Dictionary<int, int> modes)
    {
        var told = session.SkillProtocol.GroupModes;
        var started = new SkillCdNotify { Uuid = update.Uuid };
        var ended = new SkillCdNotify { Uuid = update.Uuid };
        long now = Now;
        foreach (var (group, mode) in modes)
            if (mode > 0 && told.GetValueOrDefault(group) != mode) started.Infos.Add(ModeCooldown(session, group, mode, now));
        foreach (var (group, mode) in told)
            if (mode > 0 && modes.GetValueOrDefault(group) != mode)
            {
                // The ended mode's key is closed; the plain key keeps the cooldown the server still holds, or is left alone
                // when none runs (an empty entry would wipe a running one on the client: LC_Scene_Sync._skillCDNotify).
                ended.Infos.Add(new SkillCdInfo { SkillId = group, SkillMode = mode, CdStartTimestampMs = now, CdEndTimestampMs = now });
                if (CooldownEnd(session, group, 0) > now) ended.Infos.Add(ModeCooldown(session, group, 0, now));
            }
        told.Clear();
        foreach (var (group, mode) in modes) if (mode > 0) told[group] = mode;
        if (started.Infos.Count > 0) await session.SendGameAsync(SkillCdUpdate, started);
        await session.SendGameAsync(SkillAffectedEx, update);
        if (ended.Infos.Count > 0) await session.SendGameAsync(SkillCdUpdate, ended);
    }

    /// <summary>The 31113 entry of a group's mode key: its running cooldown, or an empty entry when none runs.</summary>
    private static SkillCdInfo ModeCooldown(GameSession session, int group, int mode, long now)
    {
        var info = new SkillCdInfo { SkillId = group, SkillMode = mode };
        long end = CooldownEnd(session, group, mode);
        if (end > now) { info.CdStartTimestampMs = now; info.CdEndTimestampMs = end; }
        return info;
    }

    /// <summary>When a cast of this group under this mode is ready again: the group's shared cooldown or the mode's own.</summary>
    internal static long CooldownEnd(GameSession session, int group, int mode) =>
        Math.Max(session.SkillCds.GetValueOrDefault(group), session.SkillModeCds.GetValueOrDefault((group, mode)));

    private async Task RefreshTemporarySlotsAsync(GameSession session)
    {
        var old = session.SkillProtocol.TemporarySlotRows;
        var plan = session.SkillPlans is { } state && state.CurrentPlanIndex >= 0 && state.CurrentPlanIndex < state.SkillAssignPlans.Count
            ? state.SkillAssignPlans[state.CurrentPlanIndex] : null;
        foreach (int slot in session.Progress!.SkillBar.Keys.Concat(old.Keys).Distinct().Order())
        {
            int group = session.Progress.SkillBar.GetValueOrDefault(slot);
            int learned = group > 0 ? _skills.Level(session.Progress, group) : 0;
            int selected = plan != null && slot > 0 && slot <= plan.ManualLoadSkill.Count
                && plan.ManualLoadSkill[slot - 1].SkillId == group ? plan.ManualLoadSkill[slot - 1].SkillLevel : learned;
            int baseId = learned > 0 && selected > 0 && selected <= learned ? _skills.RealId(group, selected) : 0;
            int next = baseId > 0 && EffectiveSkillRow(session, baseId) is { } row ? (int)GameData.Int(row, "_iID") : baseId;
            int current = old.GetValueOrDefault(slot);
            if (next == baseId && current == 0) continue;
            if (current == next) continue;
            if (next == 0)
                await session.SendGameAsync(SkillSlotModify, new SkillSlotModifyNotify { Type = 4, SkillId = current, Slot = slot });
            else
                await session.SendGameAsync(SkillSlotModify, new SkillSlotModifyNotify
                {
                    Type = 1, SourceSkillId = current == 0 ? baseId : current, SkillId = next,
                    Slot = slot, Enable = true,
                });
            if (next == baseId) old.Remove(slot);
            else old[slot] = next;
        }
    }

    /// <summary>One authoritative cast timeline. High-level routes share the same Lua handler, not another cast.</summary>
    private static bool OwnsActiveSkillSource(GameSession owner, long uid) => owner.Role != null
        && owner.SkillObjects.Any(o => o.Uid == uid && o.OwnerSession == owner && o.OwnerId == owner.Role.Id
            && o.WorldScene == owner.WorldScene && o.ExpiresMs > Now && !o.Removing);

    public Task SendCastTimelineAsync(GameSession caster, UseInSkillInfo info, bool highLevel = false, bool extra = false)
    {
        bool ownedSource = extra && OwnsActiveSkillSource(caster, info.SrcID);
        if (caster.Role == null || (info.SrcID != caster.Role.Id && !ownedSource) || info.SessionId <= 0
            || _data.Row("SkillConfig", info.SkillID) == null) return Task.CompletedTask;
        uint route = extra ? highLevel ? ExtraUseInSkillHigh : ExtraUseInSkill : highLevel ? UseInSkillHigh : UseInSkill;
        return ToPlayerAndViewersAsync(caster, route, info);
    }

    /// <summary>
    /// A skill animation the server starts on a player, with no hit (a scripted slide's end, DungeonService.Slide): the same
    /// 31100 timeline a cast sends, for the row's _iActionTime.
    /// </summary>
    public Task PlaySkillAnimationAsync(GameSession s, int skillId)
    {
        if (s.Role == null || SkillConfig.Get(_data, skillId) is not { } skill) return Task.CompletedTask;
        return SendCastTimelineAsync(s, new UseInSkillInfo
        {
            SrcID = s.Role.Id, SkillID = skillId, TargetId = s.Role.Id, TargetPos = s.Position.Clone(), Face = s.Face,
            SessionId = ++_sessionIds, SkillMs = (int)skill.ActionTime, StartTime = Now, PlayAct = true,
        });
    }

    public Task SendMonsterTimelineAsync(MonsterUnit monster, UseInSkillInfo info, bool highLevel = false, bool extra = false)
    {
        if (monster.Dead || info.SrcID != monster.Uid || info.SessionId <= 0
            || _data.Row("SkillConfig", info.SkillID) == null) return Task.CompletedTask;
        uint route = extra ? highLevel ? ExtraUseInSkillHigh : ExtraUseInSkill : highLevel ? UseInSkillHigh : UseInSkill;
        return _world.ToUnitViewersAsync(monster, route, info);
    }

    /// <summary>Standalone presentation only, after the script resolves a real effect; never replaces 31101.</summary>
    public Task SendSkillEffectAsync(GameSession source, SkillEffectDisplayNotify effect, bool highLevel = false) =>
        source.Role != null && effect.SourceId == source.Role.Id && effect.EffectId > 0
            && _data.Row("SkillConfig", effect.SkillId) != null
            ? ToPlayerAndViewersAsync(source, highLevel ? SkillEffectHigh : SkillEffect, effect) : Task.CompletedTask;

    /// <summary>A Lua_Config_EffectData gfx at a monster, for a proc whose row names its effect. LC_PerformEffect_Mgr.NormalGfx
    /// plays it on the unit sourceId names, and at targetPos when that unit is gone (the swing may have just killed it);
    /// masterId is the owner LC_QualityCtrl.FilterSkillGfxPass filters by.</summary>
    internal Task SendGfxAtAsync(GameSession owner, MonsterUnit at, long effect, long skill) => owner.Role == null ? Task.CompletedTask
        : ToPlayerAndViewersAsync(owner, SkillEffect, new SkillEffectDisplayNotify
        {
            SourceId = at.Uid, MasterId = owner.Role.Id, TargetId = at.Uid, EffectId = (int)effect, EffectType = DisplayGfx,
            TargetPos = at.Pos.Clone(), SkillId = skill,
        });

    private const int DisplayGfx = 2; // enum_DisplayEffectType.Gfx

    /// <summary>Server-timed bullet only when a skill script owns projectile travel.</summary>
    public Task SendSkillBulletAsync(GameSession source, SkillFixTimeBulletNotify bullet, bool highLevel = false) =>
        source.Role != null && bullet.SourceId == source.Role.Id && bullet.EffectId > 0 && bullet.FixedTime > 0
            && _data.Row("SkillConfig", bullet.SkillConfigId) != null
            ? ToPlayerAndViewersAsync(source, highLevel ? SkillBulletHigh : SkillBullet, bullet) : Task.CompletedTask;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> _bulletEffects = new();

    /// <summary>
    /// The projectile a skill's hit flies: the first EffectData in [show*100, show*100+99] whose m_kDamage carries
    /// m_kBulletData with a prefab (Fire Bolt 1200411 fx_bullet_magicfirearrow_new, Double Strafe 1300304), else 0.
    /// The client launches such bullets only from 31120 (LC_Scene_Sync._skillFixTimeBullet ->
    /// LC_PerformEffect_Agent.SkillCreateFixTimeBullet) or, for a template with m_kClientPoints, from the cast's own
    /// timeline (LC_Skill_RunTimeContext.PlayClientEffect), so those get none from here. A 31101 hit never launches one.
    /// </summary>
    private int BulletEffect(long scriptShow) => _bulletEffects.GetOrAdd(scriptShow, show =>
    {
        if (_data.SkillTemplate(show) is { } template && SkillEffectData.HasClientPoints(template)) return 0;
        var effects = _data.SkillTemplateTable("Lua_Config_EffectData");
        for (long id = show * 100; id < show * 100 + 100; id++)
            if (SkillEffectData.Find(effects, id)?.BulletName != null) return (int)id;
        return 0;
    });

    /// <summary>
    /// 31120 for each damaged target of a hit whose skill flies a bullet, sent just before the 31101 to the same viewers.
    /// One notify launches <c>split</c> bullets m_fBulletHatchInterval apart (LC_BulletEmit_Mgr.Update), so Fire Bolt
    /// Lv. 5 drops five. (inferred) one notify per target per hit, timed at the hit; fixedTime (unread by the client)
    /// carries the bullet's configured life time.
    /// </summary>
    private async Task SendHitBulletsAsync(IEnumerable<GameSession> viewers, JsonElement row, SkillHitInfo hit, long masterId)
    {
        int effect = BulletEffect(GameData.Int(row, "_iScriptShow"));
        if (effect == 0 || hit.Dsts.Count == 0) return;
        var recipients = viewers.ToArray();
        long now = Now;
        foreach (var dst in hit.Dsts)
        {
            if (dst.DamageType is DamageType.Heal or DamageType.HealMana) continue;
            var bullet = new SkillFixTimeBulletNotify
            {
                SourceId = hit.SrcID, TargetId = dst.DstID, EffectId = effect, FixedTime = BulletLifeTime(effect),
                Split = Math.Max(1, hit.Split), SkillConfigId = hit.SkillId, MasterId = masterId, StartTimestampMs = now,
            };
            foreach (var viewer in recipients) await viewer.SendGameAsync(SkillBullet, bullet);
        }
    }

    /// <summary>SkillConfig._kScriptLogic of Blitz Beat's rows, the manual 13102 and the automatic 13110 alike.</summary>
    private const string BlitzBeatLogic = "BlitzBeat";

    /// <summary>enum_DisplayEffectType.PendantShow (Lua_GameDefine): LC_PerformEffect_Mgr.PlayPendantShow flies the
    /// caster's pendant (the falcon) along the effect's bullet.</summary>
    private const int DisplayPendantShow = 5;

    /// <summary>The falcon's EffectData id from the show: show * 1000 + 200 (Blitz Beat 13102200, a bullet with
    /// m_kBulletFalcon). (inferred) From a capture of the Genesis server; no client table links the show to it.</summary>
    private const int FalconEffectShowScale = 1000, FalconEffectOffset = 200;

    /// <summary>
    /// Blitz Beat is cast and shown on the HIGH_LEVEL routes (31106/31107/31109) although its rows' _iSkillEffectLevel is 0.
    /// (inferred) From a capture of the Genesis server: the client handles 31100/31106 and 31119/31109 with the same
    /// handlers (LC_Scene_Sync._useInSkill, _skillEffectDisplay), so nothing on the client asks for it.
    /// </summary>
    internal static bool FalconHigh(JsonElement row) => new SkillConfig(row).ScriptLogic == BlitzBeatLogic;

    /// <summary>
    /// The Hunter's falcon flight: the cast's 31109 SkillEffectDisplayNotify, sent with the cast timeline, names the falcon
    /// effect (<see cref="FalconEffectOffset"/>) with effectType PendantShow at the target. Seen on the Genesis server for
    /// the manual (1310205) and automatic (1311005) cast, with no bullet notify for it.
    /// </summary>
    private Task SendFalconAsync(GameSession caster, JsonElement row, long skillId, long targetId) =>
        caster.Role == null || targetId <= 0 ? Task.CompletedTask
        : SendSkillEffectAsync(caster, new SkillEffectDisplayNotify
        {
            SourceId = caster.Role.Id, TargetId = targetId, EffectType = DisplayPendantShow,
            EffectId = (int)(new SkillConfig(row).ScriptShow * FalconEffectShowScale + FalconEffectOffset),
            MasterId = caster.Role.Id, SkillId = skillId,
        }, highLevel: true);

    /// <summary>
    /// Blitz Beat's automatic proc (a Basic Attack's chance): the cast timeline on the extra high-level route (31107, as the
    /// Genesis server sends for the automatic row 1311005) without the caster's action (PlayAct false), the falcon flight,
    /// then the extra cast's hit. The proc is no cast, so the caster pays and starts nothing. Without the timeline the client
    /// played the flight twice and no hit sound (inferred: only the timeline separates it from a manual cast). The timeline
    /// and its 31101 share one session id: the client ties a hit to its cast by sessionId and rootCtxGuid (LC_DamageHelper).
    /// </summary>
    internal async Task CastAutoFalconAsync(GameSession s, JsonElement row, IReadOnlyList<MonsterUnit> targets)
    {
        if (s.Role == null || targets.Count == 0) return;
        var target = targets[0];
        long skillId = new SkillConfig(row).Id, id = ++_sessionIds;
        await SendCastTimelineAsync(s, new UseInSkillInfo
        {
            SrcID = s.Role.Id, SkillID = (int)skillId, TargetId = target.Uid, TargetPos = target.Pos.Clone(), Face = s.Face,
            SessionId = id, ChantMs = 0, SkillMs = SkillMs(s, row), StartTime = Now, PlayAct = false,
        }, highLevel: true, extra: true);
        await SendFalconAsync(s, row, skillId, target.Uid);
        await CastExtraAsync(s, row, targets, sessionId: id);
    }

    /// <summary>SkillConfig._kScriptLogic of Warg Strike's mode 0 rows (13312, show 13316), the rows a manual cast and the
    /// automatic one of Focused Arrow Strike (13317's logic data) both cast.</summary>
    private const string WargStrikeLogic = "WugStrike_";

    /// <summary>
    /// The wolf's effects, from a capture of the Genesis server for a manual Warg Strike (1331203): no client table names
    /// them (show 13316's m_kShows is only [[1331601, 1]]). 1331605 is the 31108 bullet of the charge, 1331602 its 31109
    /// PendantShow (a wolf bullet with the animation Skill7) and 1331204 the hit's showPoint (the wolf-attack hit sound).
    /// </summary>
    private const int WolfBullet = 1331605, WolfEffect = 1331602, WargHitEffect = 1331204;

    /// <summary>(inferred) The capture's timings: the charge 334 ms after the cast timeline, its bullet 158 ms long, about
    /// 190 ms before the hit.</summary>
    private const int WolfDelayMs = 334, WolfBulletMs = 158;

    /// <summary>
    /// Warg Strike is cast and shown on the HIGH_LEVEL routes (31106/31108/31109), as in the capture, although its rows'
    /// _iSkillEffectLevel is 0. (inferred) As for <see cref="FalconHigh"/>, nothing on the client asks for it.
    /// </summary>
    internal static bool WargStrikeHigh(JsonElement row) => new SkillConfig(row).ScriptLogic == WargStrikeLogic;

    /// <summary>The 31101 showPoint of a row's hits, on monsters and players alike: Warg Strike's from the capture (inferred
    /// for its automatic cast, which casts the same row), else the show's own damage effect.</summary>
    private int HitShowPoint(JsonElement row) => WargStrikeHigh(row) ? WargHitEffect : HitEffect(new SkillConfig(row).ScriptShow);

    /// <summary>
    /// The wolf's charge of Warg Strike: a 31108 bullet and a 31109 PendantShow (<see cref="WolfEffect"/>). Without them the
    /// wolf never moved on screen.
    /// </summary>
    private async Task SendWolfAsync(GameSession caster, long skillId, long targetId)
    {
        if (caster.Role == null || targetId <= 0) return;
        await SendSkillBulletAsync(caster, new SkillFixTimeBulletNotify
        {
            SourceId = caster.Role.Id, TargetId = targetId, EffectId = WolfBullet, FixedTime = WolfBulletMs,
            TargetPosition = FindMonster(caster.WorldScene, targetId)?.Pos.Clone(), SkillConfigId = (int)skillId,
            MasterId = caster.Role.Id, SourcePosition = caster.Position.Clone(), StartTimestampMs = Now,
        }, highLevel: true);
        await SendSkillEffectAsync(caster, new SkillEffectDisplayNotify
        {
            SourceId = caster.Role.Id, TargetId = targetId, EffectId = WolfEffect, EffectType = DisplayPendantShow,
            MasterId = caster.Role.Id, SkillId = skillId,
        }, highLevel: true);
    }

    /// <summary>SkillConfig._kScriptLogic of Arrow Storm's rows (13302, show 13302).</summary>
    private const string ArrowStormLogic = "ArrowStorm";

    /// <summary>
    /// Arrow Storm's effects in its show's EffectData range (13302 * 100 up) that the show's timeline never plays (m_kShows only
    /// [1330202, 1330205, 1330210, 1330211], and 1330205 is empty): 1330203 fx_skill_arrowstorm_01 on the caster's hand (DHandL),
    /// 1330204 fx_skill_arrowstorm_02 the green ring with falling arrows and 1330208 fx_skill_arrowstorm_04 the arrow bursts, both
    /// target effects (m_iCasterTargetEffect 2). (inferred: Genesis capture + client test; the show's timeline does not play them.)
    /// </summary>
    private const int ArrowStormHandGlow = 1330203, ArrowStormRing = 1330204, ArrowStormBursts = 1330208;

    internal static bool IsArrowStorm(JsonElement row) => new SkillConfig(row).ScriptLogic == ArrowStormLogic;

    /// <summary>
    /// Arrow Storm's ground: sent with the cast timeline, as on the Genesis server, where the ring stands at the target point with
    /// the skill's bubble and before its hit numbers (its m_kAtkPoints [0] lands the hit at once). 31119 Gfx, the route the client
    /// test played them from: the glow on the caster, the ring and the bursts at the cast's point. LC_PerformEffect_Mgr plays a Gfx
    /// on the unit sourceId names and at targetPos only when there is none, so the ground effects name no source; masterId is the
    /// caster, whose quality settings LC_QualityCtrl.FilterSkillGfxPass applies. (inferred: Genesis capture + client test; the
    /// show's timeline does not play them.)
    /// </summary>
    private async Task SendArrowStormAsync(GameSession caster, long skillId, long targetId, Vec3 point)
    {
        if (caster.Role == null) return;
        long id = caster.Role.Id;
        await SendSkillEffectAsync(caster, ArrowStormGfx(id, ArrowStormHandGlow, skillId, id, caster.Position.Clone()));
        foreach (int effect in (int[])[ArrowStormRing, ArrowStormBursts])
            await ToPlayerAndViewersAsync(caster, SkillEffect, ArrowStormGfx(0, effect, skillId, targetId, point.Clone(), id));
    }

    private static SkillEffectDisplayNotify ArrowStormGfx(long source, int effect, long skillId, long targetId, Vec3 at, long master = 0) => new()
    {
        SourceId = source, MasterId = master != 0 ? master : source, TargetId = targetId, EffectId = effect, EffectType = DisplayGfx, TargetPos = at, SkillId = skillId,
    };

    /// <summary>
    /// Warg Strike's automatic cast (Focused Arrow Strike, every ${7} seconds): on the Genesis server the wolf leaves the
    /// rider, charges the target and strikes while the bubble still names Focused Arrow Strike (Genesis capture, Discord
    /// report "Focused Arrow Strike should auto cast Warg Strike"). As <see cref="CastAutoFalconAsync"/>: the extra
    /// high-level timeline without the caster's action (PlayAct false), the wolf's charge, then the extra cast's hit on the
    /// same session id. Which messages carry it is (inferred) from the manual cast and the falcon's proc.
    /// </summary>
    internal async Task CastAutoWolfAsync(GameSession s, JsonElement row, IReadOnlyList<MonsterUnit> targets)
    {
        if (s.Role == null || targets.Count == 0) return;
        var target = targets[0];
        long skillId = new SkillConfig(row).Id, id = ++_sessionIds;
        await SendCastTimelineAsync(s, new UseInSkillInfo
        {
            SrcID = s.Role.Id, SkillID = (int)skillId, TargetId = target.Uid, TargetPos = target.Pos.Clone(), Face = s.Face,
            SessionId = id, ChantMs = 0, SkillMs = SkillMs(s, row), StartTime = Now, PlayAct = false,
        }, highLevel: true, extra: true);
        await SendWolfAsync(s, skillId, target.Uid);
        await CastExtraAsync(s, row, targets, sessionId: id);
    }

    private int BulletLifeTime(int effect) =>
        SkillEffectData.Find(_data.SkillTemplateTable("Lua_Config_EffectData"), effect)?.BulletLifeTime ?? 0;

    /// <summary>Reflect a real target eligibility transition for an AOI-visible unit (31122).</summary>
    public async Task SetSkillTargetSelectionAsync(GameSession viewer, WorldUnit target, int skillTag, bool white, bool enabled)
    {
        if (viewer.Role == null || skillTag <= 0 || !target.Viewers.Contains(viewer) || target.WorldScene != viewer.WorldScene) return;
        var key = (target.Uid, white, skillTag);
        bool previous = viewer.SkillProtocol.SkillSelection.GetValueOrDefault(key);
        if (previous == enabled) return;
        if (enabled) viewer.SkillProtocol.SkillSelection[key] = true;
        else viewer.SkillProtocol.SkillSelection.Remove(key);
        var info = new SkillBlackWhiteOpInfo
        {
            ObjectId = target.Uid,
            Operation = enabled ? SkillBlackWhiteOpInfo.Types.SkillBlackWhiteOpInfoOperation.Add
                : SkillBlackWhiteOpInfo.Types.SkillBlackWhiteOpInfoOperation.Delete,
            SkillBlackWhiteInfo = new SkillBlackWhiteInfo
            {
                Type = white ? SkillBlackWhiteInfo.Types.SkillBlackWhiteInfoType.White
                    : SkillBlackWhiteInfo.Types.SkillBlackWhiteInfoType.Black,
                SkillTagId = skillTag,
            },
        };
        await viewer.SendGameAsync(SkillSelectionChange, new SkillWhiteListChangeNotify { DirtyInfo = { info } });
    }

    /// <summary>
    /// Called only after a boss actually starts a configured skill cooldown. The boss panel (LC_Skill_BossShow) lists
    /// the boss's skills with their names, icons and cooldowns; its normal attack is never one of them.
    /// </summary>
    public async Task BossCooldownStartedAsync(MonsterUnit boss, int skillId, long endMs)
    {
        if (boss.Dead || endMs <= Now || skillId == boss.Template.NormalSkill || _data.Row("SkillConfig", skillId) == null
            || _data.Row("NPCConfig", boss.Template.NpcId) is not { } npc || GameData.Int(npc, "_iImportantSkill") != 1) return;
        boss.BossSkillCds[skillId] = endMs;
        await _world.ToUnitViewersAsync(boss, BossSkillCd,
            new BossSkillShowCdNotify { SkillId = skillId, EndSkillCdTime = endMs, BossUId = boss.Uid });
    }

    /// <summary>31127 reads the visible live boss's cooldown state, not a guessed static table.</summary>
    public async Task BossCooldownSnapshotAsync(GameSession viewer, BossAllSkillShowCdReq request)
    {
        var response = new BossAllSkillShowCdRes { BossUId = request.BossUId };
        var boss = _world.Map(viewer.WorldScene).Units.OfType<MonsterUnit>()
            .FirstOrDefault(m => m.Uid == request.BossUId && !m.Dead && m.Viewers.Contains(viewer));
        if (viewer.Role == null || boss == null || _data.Row("NPCConfig", boss.Template.NpcId) is not { } npc
            || GameData.Int(npc, "_iImportantSkill") != 1) response.Result = ErrorCodes.SkillTargetInvalid;
        else foreach (var (skillId, end) in boss.BossSkillCds.Where(x => x.Value > Now).OrderBy(x => x.Key))
            response.BossSkillCds.Add(new BossSkillShowCdNotify { SkillId = skillId, EndSkillCdTime = end, BossUId = boss.Uid });
        await viewer.SendGameAsync(BossAllSkillCds, response);
    }

    /// <summary>Clear scene-scoped target and boss presentation when the scene changes. The 31133 pause ends with it:
    /// LC_System_DamageStatistics._onSwitchScene resumes the client's own count without a request, and
    /// UI_Sys_GVE_ExitMenu_PauseWidgetBaseCtrl.OnOpen shows Resume again (inferred: the pause is per scene).</summary>
    public async Task ClearSceneSkillProtocolAsync(GameSession viewer)
    {
        viewer.SkillProtocol.SkillSelection.Clear();
        viewer.SkillProtocol.TrainingPaused = false;
        if (viewer.Role != null) await viewer.SendGameAsync(BossClearSkillCds, new BossClearAllSkillShowCdNotify());
    }

    /// <summary>31129 records an actual resolved result; omit FormulaInfo until real calculation steps exist.</summary>
    public async Task RecordDamageStatisticAsync(GameSession source, Romsg.DamageStatistics entry)
    {
        if (source.Role == null || (entry.DamageSourceId != source.Role.Id
            && !OwnsActiveSkillSource(source, entry.DamageSourceId)) || entry.SkillId == 0 && entry.BuffId == 0) return;
        var notification = new DamageStatisticsNotify { Damages = { entry } };
        foreach (var viewer in _world.Viewers(source).Prepend(source).Distinct())
            if (viewer.Role != null && !viewer.SkillProtocol.TrainingPaused)
                await viewer.SendGameAsync(DamageStatistics, notification);
    }

    /// <summary>31133 toggles whether 31129 reaches this viewer; numeric 2 is Lua Resume/proto CancelPause.</summary>
    public async Task TrainingOperationAsync(GameSession session, BattleTrainingGroundOpReq request)
    {
        int op = (int)request.OpType;
        int error = session.Role == null || session.SceneLoading || op is not (1 or 2) ? ErrorCodes.Parameter : 0;
        if (error == 0) session.SkillProtocol.TrainingPaused = op == 1;
        await session.SendGameAsync(TrainingOperation, new BattleTrainingGroundOpResp { Result = error, OpType = request.OpType });
    }
}
