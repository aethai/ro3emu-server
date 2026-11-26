using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// DI-registered combat authorization, party relation, skill override, and event PvP rule contributed by a game feature.
/// </summary>
public interface ICombatRule
{
    bool IsSameParty(GameSession a, GameSession b) => false;
    bool IsPlayerHostile(GameSession attacker, GameSession defender) => false;
    bool CanHitMonster(GameSession attacker, MonsterUnit target) => true;
    bool CanEventFight(GameSession attacker, GameSession defender) => false;
    Task OnEventPlayerKilledAsync(GameSession killer, GameSession victim) => Task.CompletedTask;
    bool? AllowMatchSkill(GameSession session, int skillId) => null;
    bool IsSkillLineBlocked(GameSession caster, MonsterUnit? target, Vec3 origin, Vec3 destination) => false;
    int? AdmitOwnSkill(GameSession session, int skillId) => null;
    Task CastOwnSkillAsync(GameSession session, int skillId) => Task.CompletedTask;
    Task LandOwnSkillAsync(GameSession session, JsonElement row) => Task.CompletedTask;
}

/// <summary>
/// Test and composition helper implementing <see cref="ICombatRule"/> from optional delegates.
/// </summary>
public sealed class DelegateCombatRule : ICombatRule
{
    public Func<GameSession, GameSession, bool>? SameParty { get; init; }
    public Func<GameSession, GameSession, bool>? PlayerHostile { get; init; }
    public Func<GameSession, MonsterUnit, bool>? MonsterTargetAllowed { get; init; }
    public Func<GameSession, GameSession, bool>? EventCanFight { get; init; }
    public Func<GameSession, GameSession, Task>? EventPlayerKilled { get; init; }
    public Func<GameSession, int, bool?>? MatchSkillRule { get; init; }
    public Func<GameSession, MonsterUnit?, Vec3, Vec3, bool>? SkillLineBlocked { get; init; }
    public Func<GameSession, int, int?>? OwnSkillAdmit { get; init; }
    public Func<GameSession, int, Task>? OwnSkillCast { get; init; }
    public Func<GameSession, JsonElement, Task>? OwnSkillLanded { get; init; }

    public bool IsSameParty(GameSession a, GameSession b) => SameParty?.Invoke(a, b) == true;
    public bool IsPlayerHostile(GameSession attacker, GameSession defender) => PlayerHostile?.Invoke(attacker, defender) == true;
    public bool CanHitMonster(GameSession attacker, MonsterUnit target) => MonsterTargetAllowed?.Invoke(attacker, target) ?? true;
    public bool CanEventFight(GameSession attacker, GameSession defender) => EventCanFight?.Invoke(attacker, defender) == true;
    public Task OnEventPlayerKilledAsync(GameSession killer, GameSession victim) => EventPlayerKilled?.Invoke(killer, victim) ?? Task.CompletedTask;
    public bool? AllowMatchSkill(GameSession session, int skillId) => MatchSkillRule?.Invoke(session, skillId);
    public bool IsSkillLineBlocked(GameSession caster, MonsterUnit? target, Vec3 origin, Vec3 destination) => SkillLineBlocked?.Invoke(caster, target, origin, destination) == true;
    public int? AdmitOwnSkill(GameSession session, int skillId) => OwnSkillAdmit?.Invoke(session, skillId);
    public Task CastOwnSkillAsync(GameSession session, int skillId) => OwnSkillCast?.Invoke(session, skillId) ?? Task.CompletedTask;
    public Task LandOwnSkillAsync(GameSession session, JsonElement row) => OwnSkillLanded?.Invoke(session, row) ?? Task.CompletedTask;
}
