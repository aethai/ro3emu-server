using System.Collections.Immutable;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>DI-bound <see cref="ICombatRule"/> evaluation and territory/escort authorization for the shared combat path.</summary>
public sealed partial class CombatService
{
    private readonly Lock _rulesGate = new();
    private ImmutableArray<ICombatRule> _rules = [];

    /// <summary>Snapshot of the DI-registered combat rules currently bound to this service.</summary>
    public ImmutableArray<ICombatRule> Rules => _rules;

    /// <summary>Binds the DI-registered <see cref="ICombatRule"/> implementations once during host composition.</summary>
    public void BindRules(IEnumerable<ICombatRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var bound = rules.ToImmutableArray();
        if (bound.Any(static r => r == null))
            throw new ArgumentException("Combat rules must not contain null entries.", nameof(rules));
        lock (_rulesGate) _rules = bound;
    }

    internal IDisposable OverrideRulesForTest(params ICombatRule[] rules)
    {
        ImmutableArray<ICombatRule> previous;
        lock (_rulesGate)
        {
            previous = _rules;
            _rules = [.. rules];
        }
        return new RuleRestore(this, previous);
    }

    internal IDisposable AddRuleForTest(ICombatRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ImmutableArray<ICombatRule> previous;
        lock (_rulesGate)
        {
            previous = _rules;
            _rules = _rules.Add(rule);
        }
        return new RuleRestore(this, previous);
    }

    internal bool IsSameParty(GameSession a, GameSession b)
    {
        foreach (var rule in _rules)
            if (rule.IsSameParty(a, b)) return true;
        return false;
    }

    internal bool IsPlayerHostile(GameSession attacker, GameSession defender)
    {
        foreach (var rule in _rules)
            if (rule.IsPlayerHostile(attacker, defender)) return true;
        return false;
    }

    internal bool IsMonsterTargetAllowed(GameSession attacker, MonsterUnit target)
    {
        foreach (var rule in _rules)
            if (!rule.CanHitMonster(attacker, target)) return false;
        return true;
    }

    internal bool CanEventFight(GameSession attacker, GameSession defender)
    {
        foreach (var rule in _rules)
            if (rule.CanEventFight(attacker, defender)) return true;
        return false;
    }

    internal async Task NotifyEventPlayerKilledAsync(GameSession killer, GameSession victim)
    {
        foreach (var rule in _rules)
            await rule.OnEventPlayerKilledAsync(killer, victim);
    }

    internal bool? EvaluateMatchSkillRule(GameSession session, int skillId)
    {
        foreach (var rule in _rules)
            if (rule.AllowMatchSkill(session, skillId) is { } decision) return decision;
        return null;
    }

    internal bool IsSkillLineBlocked(GameSession caster, MonsterUnit? target, Vec3 origin, Vec3 destination)
    {
        foreach (var rule in _rules)
            if (rule.IsSkillLineBlocked(caster, target, origin, destination)) return true;
        return false;
    }

    internal (ICombatRule Rule, int Result)? TryAdmitOwnSkill(GameSession session, int skillId)
    {
        foreach (var rule in _rules)
            if (rule.AdmitOwnSkill(session, skillId) is { } result) return (rule, result);
        return null;
    }

    private sealed class RuleRestore(CombatService owner, ImmutableArray<ICombatRule> previous) : IDisposable
    {
        private CombatService? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } combat) return;
            lock (combat._rulesGate) combat._rules = previous;
        }
    }

    private bool CanTerritoryHit(GameSession source, GameSession target) => !ReferenceEquals(source, target)
        && source.Role != null && source.Progress != null && !source.Dead && !source.SceneLoading
        && target.Role != null && target.Progress != null && !target.Dead && !target.SceneLoading
        && source.SceneEntry != 0 && source.WorldScene == target.WorldScene
        && _sessions.Contains(source) && _sessions.Contains(target)
        && _world.PlayersIn(source.WorldScene).Contains(source) && _world.PlayersIn(source.WorldScene).Contains(target)
        && IsPlayerHostile(source, target);

    private bool CanHitMonster(GameSession source, MonsterUnit target) =>
        !target.Dead && target.WorldScene == source.WorldScene
        && IsMonsterTargetAllowed(source, target);

    private bool CurrentPlayerHit(GameSession source, GameSession target, Role attackerRole, Role targetRole, long sceneId) =>
        ReferenceEquals(source.Role, attackerRole) && ReferenceEquals(target.Role, targetRole)
        && !source.SceneLoading && !target.SceneLoading && source.WorldScene == sceneId && target.WorldScene == sceneId
        && _sessions.Contains(source) && _sessions.Contains(target);
}
