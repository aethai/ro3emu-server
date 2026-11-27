using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The behaviours of held Soul Echo resonance rows (EmblemService.ResonanceEffects) that are not a skill mode or tuple:
/// cast counters, buffs a cast grants, kills that cut a cooldown and followers that join a cast. Each rule quotes the
/// row's description; its ${n} values are the row's _kDescData and its @{n} values the numbers its _iDescription lists
/// (SkillConfig.DescriptionArgs). Which event counts ("after casting", "HIT") is read from the text (inferred).
/// </summary>
public sealed partial class ResonancePassives
{
    // SkillConfig._iSkillID of the skills the descriptions name.
    private const int PhantomThrust = 11308, ShadowPierce = 15326, WargHuntMelee = 13329, WargHuntRanged = 13330,
        DestructiveFlame = 12311, IceFog = 12307, ChainLightning = 12313, PetrifyCurse = 12309, TetraVortex = 12316,
        VulcanArm = 16313, ArmCannon = 16312, Judex = 14309, HolyStigma = 14314;
    private const int WolfFollowerCharge = 113312; // SkillConfig "Wolf Follower Charge" 11331201, PATK * 50% Neutral melee
    private const int TargetMark = 1133601; // BuffConfig "Target Mark": Phantom Thrust's mode-1 _kBuff2, 10000 ms
    private const int Delusion = 11533201; // BuffConfig "Delusion"
    internal const int ElementalAttunement = 1234001; // BuffConfig "Elemental Attunement" (its _iBuffID 12340)
    private const int AttunementGroup = 12340, MelodyBubbleGroup = 16316;

    private readonly GameData _data;
    private readonly CombatService _combat;
    private readonly BuffService _buffs;
    private readonly GameLoop _loop;
    private readonly CharacterService _character;
    private readonly SessionRegistry _sessions;

    private sealed class State
    {
        public int EnsembleCasts;
        public long HuntReadyMs;
    }

    private readonly ConditionalWeakTable<GameSession, State> _states = [];
    /// <summary>Phantom Thrust's marks on a monster: the casters and until when (the mark's 10000 ms).</summary>
    private readonly ConditionalWeakTable<MonsterUnit, List<(GameSession Caster, long UntilMs)>> _marks = [];

    public ResonancePassives(GameData data, CombatService combat, BuffService buffs, GameLoop loop, CharacterService character,
        SessionRegistry sessions)
    {
        _data = data;
        _combat = combat;
        _buffs = buffs;
        _loop = loop;
        _character = character;
        _sessions = sessions;
        combat.CastResolved.Subscribe(OnCastResolvedAsync);
        combat.Killed.Subscribe(OnKilledAsync);
        combat.PlayerKilled.Subscribe(OnPlayerKilledAsync);
        combat.DamageAdjusters.Subscribe(MelodyVulcanDamage);
        combat.MonsterDamageApplied.Subscribe(BubblesTakeDamageAsync);
    }

    private static bool Holds(GameSession s, int row) => s.ResonanceSkills.Length > 0 && Array.IndexOf(s.ResonanceSkills, row) >= 0;

    /// <summary>The monsters the cast struck: in its hit list with damage (a Miss is in the processed list too).</summary>
    private static List<MonsterUnit> Struck(IReadOnlyList<MonsterUnit> processed, SkillHitInfo hit) =>
        [.. processed.Where(m => hit.Dsts.Any(d => d.DstID == m.Uid && d.HitResult != DamageHitResult.Miss && d.Damage + d.SheildDamage > 0))];

    /// <summary>Work for a caster after a delay or from another's hit: in the caster's own work, while the same role
    /// is in the same scene.</summary>
    private void Later(GameSession s, TimeSpan delay, Func<Task> work, string origin)
    {
        if (s.Role is not { } role) return;
        long scene = s.SceneGeneration;
        Task Guarded() => ReferenceEquals(s.Role, role) && s.SceneGeneration == scene && !s.SceneLoading && !s.Dead ? work() : Task.CompletedTask;
        if (delay <= TimeSpan.Zero) _loop.PostOwned(s, Guarded, origin);
        else _loop.ScheduleRuntime(delay, () => { _loop.PostOwned(s, Guarded, origin); return Task.CompletedTask; }, origin);
    }

    private async Task OnCastResolvedAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed, SkillHitInfo hit)
    {
        if (s.Role == null) return;
        var cast = new SkillConfig(action.Row);
        await AttunedCastAsync(s, cast, processed);
        if (s.ResonanceSkills.Length == 0) return;
        switch ((int)cast.SkillId)
        {
            case PhantomThrust when Holds(s, EmblemService.PixelThrust):
                await MarkAsync(s, Struck(processed, hit));
                break;
            case ShadowPierce when Holds(s, EmblemService.Encore):
                // "[Shadow Pierce] also grants you a stack of [Delusion] for @{1} sec."
                await _buffs.AddAsync(s, Delusion, (int)(Arg(EmblemService.Encore, 0) * 1000), s.Role.Id);
                break;
            case WargHuntMelee or WargHuntRanged when Holds(s, EmblemService.CoordinatedHunt):
                await CoordinatedHuntAsync(s, action, processed);
                break;
            case DestructiveFlame or IceFog or ChainLightning or PetrifyCurse when Holds(s, EmblemService.ElementalEnsemble):
                await EnsembleAsync(s);
                break;
            case TetraVortex when Holds(s, EmblemService.MixedAccompaniment) && cast.Mode != 0:
                await AccompanimentAsync(s);
                break;
            case Judex when Holds(s, EmblemService.Baptism):
                await PhrasesAsync(s, action, processed);
                break;
            case HolyStigma when Holds(s, EmblemService.Organum):
                OrganumAfterStigma(s, action);
                break;
            case ArmCannon when Holds(s, EmblemService.MelodyCannon) && cast.ScriptLogic.Contains("S1BD", StringComparison.Ordinal):
                await ApplyBubblesAsync(s, action.Row, Struck(processed, hit));
                break;
        }
    }

    /// <summary>
    /// Pixel Thrust: "If Player is dead within ${2} seconds of being HIT, [Phantom Thrust]'s cooldown is immediately
    /// reset." Read as the target Phantom Thrust hit (the mode-1 row puts Target Mark on it for that long). A target the
    /// cast itself killed died within the time at once.
    /// </summary>
    private async Task MarkAsync(GameSession s, IReadOnlyList<MonsterUnit> processed)
    {
        long until = _loop.NowMs + (long)(Desc(EmblemService.PixelThrust, 1) * 1000);
        bool killed = false;
        foreach (var monster in processed)
        {
            if (monster.Hp <= 0) { killed = true; continue; }
            var marks = _marks.GetOrCreateValue(monster);
            marks.RemoveAll(m => m.Caster == s || m.UntilMs < _loop.NowMs);
            marks.Add((s, until));
        }
        if (killed) await CombatService.ResetCooldownAsync(s, PhantomThrust);
    }

    private async Task OnKilledAsync(GameSession killer, MonsterUnit monster)
    {
        if (_marks.TryGetValue(monster, out var marks))
        {
            _marks.Remove(monster);
            foreach (var (caster, until) in marks)
                if (until >= _loop.NowMs && Holds(caster, EmblemService.PixelThrust)) await ResetAsync(killer, caster, PhantomThrust);
        }
        await DelusionKillAsync(killer);
    }

    /// <summary>A player carries the mark as Target Mark (Phantom Thrust's _kBuff2), with its caster as the source.</summary>
    private async Task OnPlayerKilledAsync(GameSession killer, GameSession victim)
    {
        long now = _loop.NowMs;
        foreach (var mark in victim.Buffs.Where(b => b.ConfigId == TargetMark && b.ExpireMs > now).ToArray())
            if (_sessions.ByRole(mark.SourceId) is { } caster && Holds(caster, EmblemService.PixelThrust))
                await ResetAsync(killer, caster, PhantomThrust);
        await DelusionKillAsync(killer);
    }

    /// <summary>A cooldown of another caster than the one in hand is changed in that caster's own work.</summary>
    private async Task ResetAsync(GameSession current, GameSession caster, int group)
    {
        if (ReferenceEquals(current, caster)) await CombatService.ResetCooldownAsync(caster, group);
        else Later(caster, TimeSpan.Zero, () => CombatService.ResetCooldownAsync(caster, group), "phantom thrust reset");
    }

    /// <summary>
    /// Encore: "[Delusion]: Kill an enemy while this effect is active to immediately reduce [Shadow Pierce]'s cooldown by
    /// @{2}%." Of the cooldown the cast starts (its row's _iCD after Cooldown Reduction), once per Delusion, which the
    /// kill spends (inferred).
    /// </summary>
    private async Task DelusionKillAsync(GameSession killer)
    {
        if (!Holds(killer, EmblemService.Encore) || killer.Progress == null) return;
        var delusion = killer.Buffs.FirstOrDefault(b => b.ConfigId == Delusion && b.ExpireMs > _loop.NowMs);
        if (delusion == null) return;
        await _buffs.RemoveAsync(killer, delusion);
        int level = (int)Math.Max(1, killer.Progress.Skills.GetValueOrDefault(ShadowPierce));
        long cooldown = _combat.ModeRow(killer, ShadowPierce, level) is { } row
            ? CombatService.ReducedCooldown(new SkillConfig(SkillParameterRules.Effective(killer, _data, row)).Cd,
                _character.Stats(killer)[AttributeId.PerCd]) : 0;
        long cut = (long)(cooldown * Arg(EmblemService.Encore, 1) / 100);
        if (cut > 0) await CombatService.CutCooldownAsync(killer, ShadowPierce, cut);
    }

    /// <summary>
    /// Coordinated Hunt: "When using [Wolf Hunt] against an enemy more than @{1} meters away, summon an Extra @{2} wolf
    /// followers to charge the target, dealing Neutral melee Physical DMG equal to Physical ATK * ${1}%. Cooldown: @{3}
    /// sec. When used against Boss monsters, [Wolf Hunt] ignores this Distance requirement." The charge is Wolf Follower
    /// Charge (113312, PATK * 50%). Distance is from where the cast began: Warg Hunt (Ranged) has dashed to the target.
    /// </summary>
    private async Task CoordinatedHuntAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed)
    {
        var target = processed.FirstOrDefault(m => m.Uid == action.Request.TargetId) ?? (processed.Count > 0 ? processed[0] : null);
        if (target is not { Hp: > 0 }) return;
        float distance = MathF.Sqrt((action.Origin.X - target.Pos.X) * (action.Origin.X - target.Pos.X)
            + (action.Origin.Z - target.Pos.Z) * (action.Origin.Z - target.Pos.Z));
        if (!target.Template.IsBoss && distance <= Arg(EmblemService.CoordinatedHunt, 0)) return;
        var state = _states.GetOrCreateValue(s);
        if (state.HuntReadyMs > _loop.NowMs) return;
        state.HuntReadyMs = _loop.NowMs + (long)(Arg(EmblemService.CoordinatedHunt, 2) * 1000);
        if (_combat.ModeRow(s, WolfFollowerCharge, 1) is not { } charge) return;
        for (int i = 0; i < (int)Arg(EmblemService.CoordinatedHunt, 1) && target.Hp > 0; i++)
            await _combat.CastExtraAsync(s, charge, [target]);
    }

    /// <summary>
    /// Elemental Ensemble: "After casting [Destructive Flame], [Ice Fog], [Chain Lightning], or [Petrify Curse] ${1} times,
    /// You gain @{1} stack of Elemental Attunement, up to ${2} stacks." The stacks last until spent (the buff has no time).
    /// </summary>
    private async Task EnsembleAsync(GameSession s)
    {
        var state = _states.GetOrCreateValue(s);
        if (++state.EnsembleCasts < Math.Max(1, (int)Desc(EmblemService.ElementalEnsemble, 0))) return;
        state.EnsembleCasts = 0;
        int most = (int)Desc(EmblemService.ElementalEnsemble, 1);
        int piles = Math.Min(most, AttunementPiles(s) + (int)Math.Max(1, Arg(EmblemService.ElementalEnsemble, 0)));
        if (s.Role is { } role) await _buffs.AddUntilSpentAsync(s, ElementalAttunement, role.Id, piles);
    }

    /// <summary>
    /// Mixed Accompaniment: "After casting it, You gain ${2} stacks of Elemental Attunement." Tetra Vortex's mode-1 row
    /// lists the buff in _kBuff1 with no time (-1); this grants it instead (SkillCastingService.ComesFromALoop), so a
    /// cancelled channel grants nothing and the stacks stay until spent.
    /// </summary>
    private async Task AccompanimentAsync(GameSession s)
    {
        int most = (int)Math.Max(1, BuffConfig.Get(_data, ElementalAttunement)?.MaxStack ?? 1);
        int piles = Math.Min(most, AttunementPiles(s) + (int)Desc(EmblemService.MixedAccompaniment, 1));
        if (piles > 0 && s.Role is { } role) await _buffs.AddUntilSpentAsync(s, ElementalAttunement, role.Id, piles);
    }

    private int AttunementPiles(GameSession s)
    {
        long now = _loop.NowMs;
        return s.Buffs.Where(b => b.ExpireMs > now && b.ConfigId / 100 == AttunementGroup).Sum(b => b.PileCount);
    }

    /// <summary>
    /// Elemental Attunement (BuffConfig 1234001): "Enhance the next [Crimson Rock], [Jack Frost], [Thunderclap], or [Earth
    /// Strain], adding Ranged Magic damage equal to MATK * ${1}% of the corresponding Element. The Chance to trigger Meteor,
    /// Frozen, Petrify, or Stun increases to ${2}%." Its tuples switch those skills to their mode-1 rows, whose
    /// _kDamageParam1 [base, 9000] carries the added hit and whose _kBuff2 the full chance (Crimson Rock's Meteorite:
    /// TalentProcs.RowProcs); the cast deals the added hit to the monsters it hit and spends one stack.
    /// </summary>
    private async Task AttunedCastAsync(GameSession s, SkillConfig cast, IReadOnlyList<MonsterUnit> processed)
    {
        if (cast.Mode == 0 || AttunementPiles(s) == 0 || cast.DamageParam1 is not [_, var added, ..] || !IsAttuned(_data, cast)) return;
        if (!await _buffs.ConsumeBuffPilesAsync(s, AttunementGroup, 1)) return;
        var targets = processed.Where(m => m.Hp > 0).ToList();
        if (targets.Count > 0) await _combat.CastExtraAsync(s, cast.AsDescribedHit(added, 0, cast.EleType), targets);
    }

    /// <summary>A row Elemental Attunement's tuples switch to (its type 15 groups at their mode).</summary>
    internal static bool IsAttuned(GameData data, SkillConfig cast)
    {
        if (cast.Mode == 0) return false;
        foreach (var mod in SkillParameterRules.BuffModifiers(data, ElementalAttunement))
            if (mod.Type == 15 && mod.Group == cast.SkillId && mod.Value == cast.Mode) return true;
        return false;
    }

    /// <summary>"The Chance to trigger Meteor ... increases to ${2}%", in basis points.</summary>
    internal static long AttunedChance(GameData data) =>
        (long)(EmblemService.DescribedValue(BuffConfig.Get(data, ElementalAttunement)?.DescData ?? [], 1) * 100);

    /// <summary>Melody Vulcan: "When attacking a target with [Melody Bubble], DMG Bonus @{2}%."</summary>
    private long MelodyVulcanDamage(GameSession s, MonsterUnit target, JsonElement row, DamageHitResult result, long damage)
    {
        if (damage <= 0 || s.ResonanceSkills.Length == 0 || new SkillConfig(row).SkillId != VulcanArm
            || !Holds(s, EmblemService.MelodyVulcan) || _buffs.UnitPiles(target, MelodyBubbleGroup) <= 0) return damage;
        return (long)Math.Round(damage * (1 + Arg(EmblemService.MelodyVulcan, 1) / 100));
    }

    private double Desc(int row, int index) => EmblemService.DescribedValue(SkillConfig.Get(_data, row)?.DescData ?? [], index);

    private double Arg(int row, int index) => SkillConfig.Get(_data, row)?.DescriptionArgs() is { } args && index < args.Length ? args[index] : 0;
}
