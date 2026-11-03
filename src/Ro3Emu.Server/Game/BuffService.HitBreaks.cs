using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    /// <summary>A Freeze/Petrify/Sleep from any source breaks on a hit, as its BuffConfig description says.</summary>
    private static bool BreaksOnDamage(ActiveBuff buff) => buff.ConfigId / 100 is FreezeGroup or PetrifyGroup or SleepGroup
        || !buff.IsGuardianOwned && buff.StateOrdinal is 11 or 12 or 19; // the same states from another row (Petrify 4340301)

    /// <summary>BuffConfig Freeze/Petrify/Sleep descriptions remove control on a hit. Freeze/Petrify explicitly
    /// link their follow-up slow and duration in _kLogicData; the follow-up keeps the broken control's owner, a guardian's
    /// or an ordinary source's (inferred). (inferred) Only positive committed HP damage breaks it.</summary>
    internal async Task BreakHitControlsAsync(GameSession player)
    {
        foreach (var buff in player.Buffs.Where(BreaksOnDamage).ToArray())
        {
            await RemoveAsync(player, buff);
            if (_data.Row("BuffConfig", buff.ConfigId) is not { } row) continue;
            var next = GuardianPetRules.Numbers(row, "_kLogicData");
            if (next.Length != 2 || next[1] <= 0) continue;
            if (buff.IsGuardianOwned) await AddGuardianAsync(player, (int)next[0], (int)next[1], buff.SourceId);
            else await AddAsync(player, (int)next[0], (int)next[1], buff.SourceId);
        }
    }

    internal async Task BreakHitControlsAsync(WorldUnit unit)
    {
        if (!_unitBuffs.TryGetValue(unit.Uid, out var active)) return;
        foreach (var buff in active.Where(BreaksOnDamage).ToArray())
        {
            await RemoveUnitBuffAsync(unit, buff);
            if (_data.Row("BuffConfig", buff.ConfigId) is not { } row) continue;
            var next = GuardianPetRules.Numbers(row, "_kLogicData");
            if (next.Length != 2 || next[1] <= 0) continue;
            if (buff.IsGuardianOwned) await AddGuardianAsync(unit, (int)next[0], (int)next[1], buff.SourceId);
            else await AddSupportAsync(unit, (int)next[0], (int)next[1], buff.SourceId);
        }
    }
}
