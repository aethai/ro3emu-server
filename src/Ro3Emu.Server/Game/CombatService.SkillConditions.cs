using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// SkillConfig._iSkillCondition, which LC_Skill_ActionPreCheck asks through LC_Skill_Function.CheckSkillPreCondition before
/// the client casts: a weapon type, a battle mount, a pendant, a buff. Only a modified client reaches the server without it.
/// </summary>
public sealed partial class CombatService
{
    private const long CondBuffLayer = 2000, CondEquip = 2001, CondMountIs = 2003, CondMountIsNot = 2004, CondPendant = 2006;

    private bool SkillConditionMet(GameSession s, JsonElement row) =>
        new SkillConfig(row).SkillCondition is not (var id and not 0) || LCConditionList.Get(_data, id) is not { } node || NodeMet(s, node);

    /// <summary>LC_ConditionDefine.enum_NodeType: 1 AND, 2 OR of the sub conditions, 3 one condition.</summary>
    private bool NodeMet(GameSession s, LCConditionList node)
    {
        bool met = node.Type == 3 ? LeafMet(s, node.Data) : SubNodes(node) is var subs && (node.Type == 2 ? subs.Any(sub => NodeMet(s, sub)) : subs.All(sub => NodeMet(s, sub)));
        return met != node.Inverse;
    }

    private static List<LCConditionList> SubNodes(LCConditionList node) => node.SubConditionList is { ValueKind: JsonValueKind.Array } subs
        ? [.. subs.EnumerateArray().Select(sub => new LCConditionList(sub))] : [];

    /// <summary>LC_InteractCondition.CheckEquipStatue, CheckMountStatue, CheckPendantStatue and LC_BattleCondition.CheckBuffLayer.
    /// (inferred) CheckHaveBuff (1 row) and CheckSceneFlyable (3 rows) are not judged here, so they do not refuse a cast.</summary>
    private bool LeafMet(GameSession s, long[] data) => data switch
    {
        [CondEquip, var type, ..] => s.Items.Any(i => i.Grid != 0 && ItemConfig.Get(_data, i.Entry)?.SubType == type), // all gear, not only the hands: subtype 50 is worn in part 3
        [CondMountIs, var mount, ..] => MountService.Rider(s) == mount,
        [CondMountIsNot, var mount, ..] => MountService.Rider(s) != mount,
        [CondPendant, var type, ..] => s.CareerPendants.Any(pendant => PendantConfig.Get(_data, pendant)?.Type == type),
        [CondBuffLayer, var buff, var most, var least, ..] => s.Buffs.Any(b => b.ConfigId == buff && b.ExpireMs > Now && b.PileCount >= least && b.PileCount <= most),
        _ => true,
    };
}
