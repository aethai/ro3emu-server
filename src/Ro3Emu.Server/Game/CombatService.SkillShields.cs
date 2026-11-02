using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The shields of player skills: a _kBuff1 buff that <see cref="SkillShieldRules"/> names a shield gets its amount from
/// the caster's (or the recipient's) attributes when it is applied, through the finite shield the pet shields use
/// (property 2003, consumed before HP by <see cref="BuffService.AbsorbAsync(GameSession, long, bool)"/>).
/// </summary>
public sealed partial class CombatService
{
    /// <summary>Put a skill's _kBuff1 buff on <paramref name="recipient"/>, with its shield when it carries one.</summary>
    internal Task ApplySkillBuffAsync(StatSheet casterStats, GameSession recipient, int buffId, int durationMs, long sourceId)
    {
        long shield = BuffConfig.Get(_data, buffId) is { } buff
            ? SkillShieldRules.Amount(buff, casterStats, _character.Stats(recipient)) : 0;
        return shield > 0
            ? _buffs.AddSkillShieldAsync(recipient, buffId, durationMs, sourceId, shield)
            : _buffs.AddAsync(recipient, buffId, durationMs, sourceId);
    }
}
