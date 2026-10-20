using System.Runtime.CompilerServices;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private readonly PetService pets;
    private sealed class GuardianPoll { public long Next; public bool Pending; }
    private readonly ConditionalWeakTable<GameSession, GuardianPoll> guardianPolls = new();

    private void TickGuardianPets(GameSession s, long now)
    {
        if (s.Pets == null || s.Dead || s.SceneLoading || s.SceneRecoveryRequired || s.TowerAttempt != null
            || (s.StatusBits & FightBit) == 0) return;
        var poll = guardianPolls.GetOrCreateValue(s);
        if (poll.Pending || now < poll.Next) return;
        poll.Next = now + 250; // inferred: bounded ordinary guardian AI polling, separate from player auto attack
        poll.Pending = true;
        loop.TrackRuntimeDetached("ordinary guardian skills", async () =>
        {
            try
            {
                using var held = await CharacterService.Lock(s);
                if (s.Role == null || s.Dead || s.SceneLoading || (s.StatusBits & FightBit) == 0) return;
                var target = s.Attack?.Target;
                if (target == null || !CanHitMonster(s, target))
                    target = world.Map(s.WorldScene).Units.OfType<MonsterUnit>()
                        .Where(m => ReferenceEquals(m.Target, s) && CanHitMonster(s, m))
                        .OrderBy(m => Distance(s.Position, m.Pos)).FirstOrDefault();
                foreach (var skill in pets.Guardians(s).ToArray())
                {
                    if (GuardianPetRules.Numbers(skill.Active, "_kCost").Length > 0) continue;
                    if (GameData.Int(skill.Active, "_iAutoUse") != 1) continue;
                    if (target == null && skill.Kind is GuardianPetRules.Kind.Damage or GuardianPetRules.Kind.Debuff) continue;
                    if (skill.Kind == GuardianPetRules.Kind.Heal && !GuardianAllies(s, skill)
                        .Any(ally => character.Stats(ally) is var stats && stats[2000] < stats[101])) continue;
                    await BeginGuardianAsync(s, skill, new CastSkillReq
                    {
                        SkillId = skill.Id, TargetId = target?.Uid ?? s.Role!.Id,
                        TargetPos = target?.Pos.Clone() ?? s.Position.Clone(), Face = s.Face, IsAutoCast = true,
                    }, reply: false);
                }
            }
            finally { poll.Pending = false; }
        });
    }

    private async Task CastGuardianRequestAsync(GameSession s, CastSkillReq req)
    {
        var skill = pets.Guardians(s).FirstOrDefault(g => g.Id == req.SkillId);
        if (skill == null)
        {
            await RefuseCastAsync(s, req, ErrorCodes.SkillNotLearned, 0);
            return;
        }
        await BeginGuardianAsync(s, skill, req.Clone(), reply: true);
    }

    private async Task BeginGuardianAsync(GameSession s, GuardianPetRules.Skill skill, CastSkillReq req, bool reply)
    {
        int group = (int)GameData.Int(skill.Active, "_iSkillID"), mode = (int)GameData.Int(skill.Active, "_iMode");
        int error = s.Role == null || s.Progress == null || s.Dead || s.SceneLoading || s.SceneRecoveryRequired
            || !sessions.All().Contains(s) || s.Attachment?.Cancelled == true ? ErrorCodes.SkillBadState
            : !pets.GuardianEquipped(s, skill) ? ErrorCodes.SkillNotLearned
            : req.OpUnitId != 0 && req.OpUnitId != s.Role.Id ? ErrorCodes.SkillRequirements
            : !float.IsFinite(req.Face) || req.TargetPos is { } pos && !Finite(pos) ? ErrorCodes.Parameter
            : buffs.HasForbidden(s, 4) || MatchSkillRule?.Invoke(s, skill.Id) == false ? ErrorCodes.SkillBadState
            : Math.Max(s.SkillCds.GetValueOrDefault(group), s.SkillModeCds.GetValueOrDefault((group, mode))) > Now
                ? ErrorCodes.SkillCooldown : 0;
        // No unhandled item/energy/cast-cost path may be bypassed by an extra skill.
        if (error == 0 && (Tuples(skill.Active, "_kCost").Any()
            || Tuples(skill.Active, "_kEnergyInfo").Any())) error = ErrorCodes.SkillRequirements;
        if (error == 0 && skill.Kind is GuardianPetRules.Kind.Damage or GuardianPetRules.Kind.Debuff)
            error = ValidateTarget(s, skill.Effect, req, s.Position, 0);
        if (error != 0)
        {
            if (reply) await RefuseCastAsync(s, req, error, 0);
            return;
        }

        long id = ++_sessionIds;
        long started = Now;
        var origin = s.Position.Clone();
        int duration = (int)Math.Max(1, GameData.Int(skill.Effect, "_iActionTime"));
        var template = data.SkillTemplate(GameData.Int(skill.Effect, "_iScriptShow"));
        // A pet's extra cast never replaces the player's SkillCast, animation, resources or attack loop.
        // LC_Scene_Sync._useExtraSkill needs the CHILD (GuardianSkill=41) plus petConfigId.
        ActiveSkillCast Action(int component) => new()
        {
            Id = id, OwnerId = s.Role!.Id, WorldScene = s.WorldScene, SceneGeneration = s.SceneGeneration,
            Attachment = s.Attachment, Row = GuardianPetRules.DamageRow(skill, character.Stats(s), component),
            Request = new CastSkillReq { SkillId = skill.EffectId, TargetId = req.TargetId,
                TargetPos = req.TargetPos?.Clone() ?? origin.Clone(), Face = req.Face, IsExtraSkill = true },
            Origin = origin.Clone(), StartMs = started, ChantMs = 0, ActionMs = duration,
            SourceEligible = () => pets.GuardianEquipped(s, skill),
        };
        var actions = Enumerable.Range(0, GuardianPetRules.Components(skill)).Select(Action).ToArray();
        await StartCooldownAsync(s, skill.Active); // reserves before any suspension can admit a duplicate
        if (!CurrentCastMap(s, actions[0])) return;
        if (reply) await s.SendGameAsync(UseSkill, new CastSkillRsp { SessionId = id, IsAutoCast = req.IsAutoCast });
        if (!CurrentCastMap(s, actions[0])) return;
        await SendCastTimelineAsync(s, new UseInSkillInfo
        {
            SrcID = actions[0].OwnerId, SkillID = skill.EffectId, PetConfigId = skill.PetId,
            TargetId = req.TargetId, TargetPos = req.TargetPos ?? origin, Face = req.Face,
            SessionId = id, SkillMs = duration, StartTime = started,
        }, extra: true);
        double point = template is { } t ? Numbers(t, "m_kAtkPoints").FirstOrDefault(0.4) : 0.4;
        int first = ImpactOffset(template, point, 0, duration);
        for (int i = 0; i < actions.Length; i++)
        {
            var action = actions[i];
            bool support = i == 0;
            // (inferred) distribute explicitly documented multi-hit components over the action tail.
            // Presentation attack-point count is not used as a damage multiplier.
            int delay = first + (duration - first) * i / actions.Length;
            ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(delay), async () =>
            {
                if (!CurrentCastMap(s, action)) return;
                if (skill.Kind is GuardianPetRules.Kind.Damage or GuardianPetRules.Kind.Debuff)
                    await ApplyEffectsAsync(s, action, action.Request.TargetPos ?? action.Origin);
                if (support && CurrentCastMap(s, action)) await GuardianSupportAsync(s, skill, action);
            });
        }
    }

    private GameSession[] GuardianAllies(GameSession s, GuardianPetRules.Skill skill)
    {
        if (skill.Kind is not (GuardianPetRules.Kind.Heal or GuardianPetRules.Kind.AlliedBuff)) return [s];
        float radius = (float)(GuardianPetRules.Numbers(skill.Effect, "_kRange").FirstOrDefault() / 100);
        int maximum = (int)Math.Max(1, GameData.Int(skill.Effect, "_iTargetMax"));
        return world.PlayersIn(s.WorldScene).Where(p => p.Role != null && !p.Dead && !p.SceneLoading && !p.SceneRecoveryRequired
                && PlayerHostile?.Invoke(s, p) != true && PlayerHostile?.Invoke(p, s) != true
                && Distance(s.Position, p.Position) <= radius)
            .OrderBy(p => p == s ? 0 : 1).ThenBy(p => Distance(s.Position, p.Position))
            .ThenBy(p => p.Role!.Id).Take(maximum).ToArray();
    }

    private async Task GuardianSupportAsync(GameSession s, GuardianPetRules.Skill skill, ActiveSkillCast action)
    {
        var ownerStats = character.Stats(s);
        var hit = new SkillHitInfo { SrcID = action.OwnerId, SkillId = skill.EffectId,
            SessionId = action.Id, RootCtxGuid = action.Id, ShowPoint = HitEffect(GameData.Int(skill.Effect, "_iScriptShow")) };
        foreach (var recipient in GuardianAllies(s, skill))
        {
            if (!CurrentCastMap(s, action)) break;
            var recipientRole = recipient.Role;
            var recipientAttachment = recipient.Attachment;
            var recipientGeneration = recipient.SceneGeneration;
            bool RecipientCurrent() => recipientRole != null && ReferenceEquals(recipient.Role, recipientRole)
                && ReferenceEquals(recipient.Attachment, recipientAttachment) && recipientAttachment?.Cancelled != true
                && recipient.SceneGeneration == recipientGeneration && recipient.WorldScene == action.WorldScene
                && !recipient.Dead && !recipient.SceneLoading && !recipient.SceneRecoveryRequired;
            if (!RecipientCurrent()) continue;
            foreach (var tuple in Tuples(skill.Effect, "_kBuff1"))
            {
                if (!CurrentCastMap(s, action) || !RecipientCurrent()) break;
                if (RollBuff(tuple, out int buffId, out int ms) && data.Row("BuffConfig", buffId) is { } buff)
                    await buffs.AddSupportAsync(recipient, buffId, ms, action.OwnerId,
                        GuardianPetRules.Shield(skill, buff, ownerStats, character.Stats(recipient)));
            }
            if (!CurrentCastMap(s, action) || !RecipientCurrent() || skill.Kind != GuardianPetRules.Kind.Heal) continue;
            var stats = character.Stats(recipient);
            long amount = (long)Math.Max(0, Math.Floor(Math.Max(ownerStats[103], ownerStats[104])
                * GuardianPetRules.Numbers(skill.Effect, "_kDamageParam1").FirstOrDefault() / 10000));
            foreach (var modifier in ExtraHeal) amount = modifier(s, amount);
            amount = Math.Max(0, amount);
            long actual = Math.Min(amount, Math.Max(0, stats[101] - stats[2000]));
            await character.SetHpSpAsync(recipient, stats[2000] + actual, null);
            hit.Dsts.Add(new DamageInfo { DstID = recipientRole!.Id, Damage = actual, DamageType = DamageType.Heal, DstElement = 1 });
            foreach (var observer in Healed) observer(s, actual);
            foreach (var observer in HealedAsync) await observer(s, actual);
        }
        if (hit.Dsts.Count > 0 && CurrentCastMap(s, action)) await ToPlayerAndViewersAsync(s, SkillHit, hit);
    }
}
