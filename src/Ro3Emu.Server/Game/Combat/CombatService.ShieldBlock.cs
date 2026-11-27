using System.Runtime.CompilerServices;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Shield Block (Royal Guard, group 11638): "Raise your Shield to block for ${1} seconds ... Tap this skill again while
/// blocking or wait until the block ends to use [Shield Auto Counter]. If you are attacked while blocking, [Shield Auto
/// Counter] is Enhanced into [Powerful Auto Counter]." The block (script Protection, modes 0 and 3) grants a Parry buff whose
/// type 15 tuple switches the group to its counter mode (1164201: 4, Absolute Defense's 11165501: 6), so a second tap
/// casts the counter row (ProtectionAttack) the ordinary way. What no row carries is done here (no client code reads
/// these; inferred from the text):
/// <list type="bullet">
/// <item>a hit while blocking swaps the Parry for the next Parry row (1164301: mode 7, 11165601: mode 9, Powerful Auto
/// Counter, ProtectionAttack2) for the rest of the block;</item>
/// <item>a counter, tapped or not, ends the block: its Parry goes, so the skill shows the block again;</item>
/// <item>a block that ends with no tap counters by itself with the row of the Parry it ended on, at the monsters in its
/// area in front of the holder.</item>
/// </list>
/// </summary>
public sealed partial class CombatService
{
    private const int ShieldBlockGroup = 11638;
    private const string BlockScript = "Protection", CounterScript = "ProtectionAttack";
    /// <summary>The Parry rows of a block (BuffConfig._iBuffID): the plain and Powerful ones, and Absolute Defense's.</summary>
    private static readonly int[] BlockParryGroups = [11642, 11643, 111655, 111656];
    /// <summary>A plain Parry row's Powerful one is the next row: 1164201 -> 1164301, 11165501 -> 11165601.</summary>
    private const int PowerfulParryStep = 100;

    private sealed class Block
    {
        public long Token;
        public bool Countered;
    }

    private readonly ConditionalWeakTable<GameSession, Block> _blocks = [];

    private ActiveBuff? BlockParry(GameSession s)
    {
        long now = Now;
        foreach (var buff in s.Buffs)
            if (buff.ExpireMs > now && Array.IndexOf(BlockParryGroups, buff.ConfigId / 100) >= 0) return buff;
        return null;
    }

    private async Task ShieldBlockOnCastAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed, Romsg.SkillHitInfo hit)
    {
        var cast = new SkillConfig(action.Row);
        if (cast.SkillId != ShieldBlockGroup || s.Role is not { } role) return;
        var block = _blocks.GetOrCreateValue(s);
        if (cast.ScriptLogic.StartsWith(CounterScript, StringComparison.Ordinal))
        {
            block.Countered = true;
            if (BlockParry(s) is { } parry) await _buffs.RemoveAsync(s, parry);
            return;
        }
        if (cast.ScriptLogic != BlockScript || BlockParry(s) is not { } started) return;
        long token = ++block.Token;
        block.Countered = false;
        long generation = s.SceneGeneration;
        int level = (int)Math.Max(1, cast.Level);
        // A little before the Parry expires, so the buff it ended on still says which counter is due.
        long dueMs = Math.Max(0, started.ExpireMs - Now - 50);
        _loop.ScheduleRuntime(TimeSpan.FromMilliseconds(dueMs), () =>
        {
            _loop.PostOwned(s, () => ReferenceEquals(s.Role, role) ? BlockEndedAsync(s, generation, block, token, level) : Task.CompletedTask,
                "shield block");
            return Task.CompletedTask;
        }, "shield block");
    }

    /// <summary>The block ran out with no tap: the counter of the Parry it ended on, then the Parry goes.</summary>
    private async Task BlockEndedAsync(GameSession s, long generation, Block block, long token, int level)
    {
        if (s.Dead || s.SceneLoading || s.SceneGeneration != generation
            || block.Token != token || block.Countered || BlockParry(s) is not { } parry) return;
        block.Countered = true;
        int mode = BuffModes(parry.ConfigId);
        await _buffs.RemoveAsync(s, parry);
        if (mode == 0 || RowInMode(ShieldBlockGroup, level, mode) is not { } row) return;
        var counter = new SkillConfig(row);
        var targets = MonstersNear(s, s.Position, counter.DistanceMax / 100f)
            .Where(m => SkillAreaContains(row, s.Position, s.Position, s.Face, m.Pos)).Take((int)Math.Max(1, counter.TargetMax)).ToList();
        if (targets.Count > 0) await CastExtraAsync(s, row, targets);
    }

    /// <summary>
    /// A counter tapped while the block lives: the block (its _iInterruptedType 3, which the client's
    /// SkillCanInterrupted treats as never interruptible) gives way to it, and the block's own cooldown, shared by the
    /// group's modes (_iCdModeGroup -1), does not hold it back. Once it is cast the Parry is gone (inferred).
    /// </summary>
    internal bool CountersBlock(GameSession s, System.Text.Json.JsonElement row)
    {
        var cast = new SkillConfig(row);
        return cast.SkillId == ShieldBlockGroup && cast.ScriptLogic.StartsWith(CounterScript, StringComparison.Ordinal) && BlockParry(s) != null;
    }

    /// <summary>The mode a Parry row's type 15 tuple sets on Shield Block.</summary>
    private int BuffModes(int buffConfigId)
    {
        foreach (var mod in SkillParameterRules.BuffModifiers(_data, buffConfigId))
            if (mod.Type == 15 && mod.Group == ShieldBlockGroup) return (int)mod.Value;
        return 0;
    }

    /// <summary>A hit while blocking: the plain Parry becomes the Powerful one for the rest of the block.</summary>
    private async Task ShieldBlockStruckAsync(GameSession p)
    {
        if (BlockParry(p) is not { } parry || p.Role is not { } role) return;
        int powerful = parry.ConfigId + PowerfulParryStep;
        if (Array.IndexOf(BlockParryGroups, parry.ConfigId / 100) % 2 != 0 || BuffConfig.Get(_data, powerful) is null) return;
        int left = (int)Math.Max(1, parry.ExpireMs - Now);
        await _buffs.RemoveAsync(p, parry);
        await _buffs.AddAsync(p, powerful, left, role.Id);
    }
}
