using System.Runtime.CompilerServices;
using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Shield Block (Royal Guard, group 11638): "Raise your Shield to block for ${1} seconds ... Tap this skill again while
/// blocking or wait until the block ends to use [Shield Auto Counter]. If you are attacked while blocking, [Shield Auto
/// Counter] is Enhanced into [Powerful Auto Counter]." The block (script Protection, modes 0 and 3) grants a Parry buff whose
/// type 15 tuple switches the group to its counter mode (1164201: 4, Absolute Defense's 11165501: 6), so the bar shows the
/// counter and a second tap sends its row (a plain 31102: the client lets nothing interrupt _iInterruptedType 3, so it
/// sends no 31124). What no row carries is done here (no client code reads these; inferred from the text):
/// <list type="bullet">
/// <item>while the block lives its counter is ready: the block's cooldown, shared by the group's modes (_iCdModeGroup -1),
/// neither refuses the tap nor shows on the counter's key, and the running block gives way to it;</item>
/// <item>a hit while blocking, from a monster's attack or skill or a player, swaps the Parry for the next Parry row
/// (1164301: mode 7, 11165601: mode 9, Powerful Auto Counter) for the rest of the block;</item>
/// <item>a counter, tapped or not, ends the block as it starts: its Parry goes, so the skill shows the block again;</item>
/// <item>a block that runs out with no tap counters by itself with the row of the Parry it ended on, at the monsters in
/// that row's area (_kRange) in front of the holder, and at the players there it may fight, as a cast of the holder's own
/// (timeline, hit, _kBuff1). A Parry taken
/// away before its time (a dispel) ends the block with no counter.</item>
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
    /// <summary>How late the block-end work may come and still count the Parry as run out rather than taken away.</summary>
    private const long BlockEndSlackMs = 100;

    private sealed class Block
    {
        public long Token;
        public bool Ended;
        /// <summary>The Parry row the block holds now (the Powerful one after a hit) and when it runs out.</summary>
        public int Parry;
        public long ParryEndsMs;
    }

    private readonly ConditionalWeakTable<GameSession, Block> _blocks = [];

    private ActiveBuff? BlockParry(GameSession s)
    {
        long now = Now;
        foreach (var buff in s.Buffs)
            if (buff.ExpireMs > now && Array.IndexOf(BlockParryGroups, buff.ConfigId / 100) >= 0) return buff;
        return null;
    }

    private Task ShieldBlockOnCastAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed, Romsg.SkillHitInfo hit)
    {
        var cast = new SkillConfig(action.Row);
        if (cast.SkillId != ShieldBlockGroup || cast.ScriptLogic != BlockScript || s.Role is not { } role || BlockParry(s) is not { } started)
            return Task.CompletedTask;
        var block = _blocks.GetOrCreateValue(s);
        long token = ++block.Token;
        block.Ended = false;
        block.Parry = started.ConfigId;
        block.ParryEndsMs = started.ExpireMs;
        long generation = s.SceneGeneration;
        int level = (int)Math.Max(1, cast.Level);
        _loop.ScheduleRuntime(TimeSpan.FromMilliseconds(Math.Max(0, started.ExpireMs - Now)), () =>
        {
            _loop.PostOwned(s, () => ReferenceEquals(s.Role, role) ? BlockEndedAsync(s, generation, block, token, level) : Task.CompletedTask,
                "shield block");
            return Task.CompletedTask;
        }, "shield block");
        return Task.CompletedTask;
    }

    /// <summary>The block ran out with no tap: the counter of the Parry it ended on, then the Parry goes.</summary>
    private async Task BlockEndedAsync(GameSession s, long generation, Block block, long token, int level)
    {
        if (s.Dead || s.SceneLoading || s.SceneGeneration != generation || block.Token != token || block.Ended) return;
        block.Ended = true;
        var parry = BlockParry(s);
        // Gone before its time: taken away, not run out (the work itself may come a little late).
        if (parry == null && Now < block.ParryEndsMs - BlockEndSlackMs) return;
        if (parry != null) await _buffs.RemoveAsync(s, parry);
        int mode = BuffModes(block.Parry);
        if (mode == 0 || RowInMode(ShieldBlockGroup, level, mode) is not { } plain) return;
        var row = SkillParameterRules.Effective(s, _data, plain);
        var counter = new SkillConfig(row);
        float reach = Math.Max(counter.DistanceMax, counter.Range is [var range, ..] ? range : 0) / 100f;
        var targets = MonstersNear(s, s.Position, reach)
            .Where(m => SkillAreaContains(row, s.Position, s.Position, s.Face, m.Pos)).Take((int)Math.Max(1, counter.TargetMax)).ToList();
        // Players the holder may fight, in the same area, up to the row's count with the monsters.
        var enemies = AlliesNear(s, s.Position, reach)
            .Where(p => p != s && CanPlayerHit(s, p) && SkillAreaContains(row, s.Position, s.Position, s.Face, p.Position))
            .Take(Math.Max(0, (int)Math.Max(1, counter.TargetMax) - targets.Count)).ToList();
        // The counter lands under a runtime intent: a hit that kills commits under every contributor's role (GameLoop.PostRuntimeFor).
        _loop.PostRuntimeFor(s, async () =>
        {
            if (s.Dead || s.SceneLoading || s.SceneGeneration != generation) return;
            await CastForPlayerAsync(s, row, targets);
            foreach (var enemy in enemies) await CastExtraOnPlayerAsync(s, counter, enemy);
        }, "shield block counter");
    }

    /// <summary>A counter tapped while the block lives: the block gives way to it and its shared cooldown does not hold it back.</summary>
    internal bool CountersBlock(GameSession s, JsonElement row)
    {
        var cast = new SkillConfig(row);
        return cast.SkillId == ShieldBlockGroup && cast.ScriptLogic.StartsWith(CounterScript, StringComparison.Ordinal) && BlockParry(s) != null;
    }

    /// <summary>The mode the live block's Parry selects (its counter), whose key shows ready on the bar.</summary>
    internal bool BlockCounterMode(GameSession s, int group, int mode) =>
        group == ShieldBlockGroup && mode != 0 && BlockParry(s) is { } parry && BuffModes(parry.ConfigId) == mode;

    /// <summary>A tapped counter has been accepted: the block ends now, with no counter of its own.</summary>
    internal async Task EndBlockAsync(GameSession s)
    {
        if (_blocks.TryGetValue(s, out var block)) block.Ended = true;
        if (BlockParry(s) is { } parry) await _buffs.RemoveAsync(s, parry);
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
        if (_blocks.TryGetValue(p, out var block)) block.Parry = powerful;
    }
}
