using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class GmCommands
{
    /// <summary>GmOpEnum values only the GMCombinationConfig presets use (GMConfig buttons 1030-1032, 3008-3011).</summary>
    internal const int OpSkillQueue = 38, OpJobProfessSkill = 170, OpJobProfessAttr = 171, OpSetSkillWheel = 172, OpEquipWeapon = 173,
        OpCreateLvEmblem = 174, OpEquipMount = 175, OpEquipPendant = 176, OpWarRobe = 251, OpSeasonTalent = 261, OpPlayerAsset = 999;

    private const int ItemTypeEquip = 5; // ItemConfig._iType

    /// <summary>GM panel preset ops that only need GmCommands's core dependencies; feature-specific preset ops register via RegisterOp.</summary>
    private async Task<string?> PresetOpAsync(GameSession s, int op, string[] args) => op switch
    {
        // ["GmPlayerAsset","openallfunc"]: the only subcommand the client data names.
        OpPlayerAsset => await PlayerAssetAsync(s, args),
        // ["GmjobProfessAttr","101"]: a JobProfessConfig build (school) id.
        OpJobProfessAttr => await character.SetProfessionStatsAsync(s, Profession(args[0])),
        OpEquipWeapon => await EquipAsync(s, I(args[0])),
        _ => null,
    };

    /// <summary>
    /// A JobProfessConfig id as the client reads it: its _iProfessionID keys the recommended stats and skills
    /// (Lua_CFG_JobProfessConfig.GetProfessionId before JobProfessPointConfig / JobProfessSkillConfig).
    /// </summary>
    internal long Profession(string arg) =>
        JobProfessConfig.Get(data, L(arg)) is { } build ? build.ProfessionId : throw new ArgumentException($"no JobProfessConfig {arg}");

    /// <summary>
    /// "openallfunc": every FunctionConfig function counts as open (FunctionService.IsOpen), then 5056 and the other
    /// function-gated datasets go out as after a level change. It is kept with the role (role column gm_state; inferred:
    /// the original server keeps a role's opened functions): a preset-built role has not delivered the quests that open Skills
    /// (function 10, quest 10101170) and the rest, and a login without the flag closed them again, so the skill bar
    /// showed its lock and refused drags (UI_Sys_Main_JoyStickSkillWidget_PCCtrl._refreshFunctionUIState,
    /// UI_Sys_Main_JoyStickSkillItemWidget_PCCtrl.RefreshCanDrag check SkillGrowth).
    /// </summary>
    private async Task<string> PlayerAssetAsync(GameSession s, string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("openallfunc", StringComparison.OrdinalIgnoreCase))
            return $"GmPlayerAsset {string.Join(',', args)}: only openallfunc is known (the presets' one subcommand).";
        character.OpenAllFunctions(s);
        foreach (var handler in character.ProgressHandlers.Handlers) await handler(s);
        return "Every function is open for this role.";
    }

    /// <summary>The role's GM panel grants kept across logins (role column gm_state, JSON).</summary>
    public sealed class RoleGmState
    {
        /// <summary>openallfunc: every client function counts as open (<see cref="GameSession.GmAllFunctionsOpen"/>).</summary>
        public bool AllFunctionsOpen { get; set; }
    }

    /// <summary>
    /// GMEquipWeapon 173: wear an instance of the item from the bag (one is granted when the role has none), through
    /// 31400's own path, so 5009, 5007, the reply and the model change go out as for the client's equip; the grid is the
    /// item's own (EquipPosType). (inferred) A second step for the same item (the dual-dagger presets 5402, 6511 add two)
    /// wears the second copy; where 31400 gives the item one grid it replaces the first, and the reply says so. 31400's
    /// wear rules apply: two client presets (5100's 90000731, 1310's 90000046) name a weapon their level or job may not
    /// wear, and a forced one would come off at the next login anyway.
    /// </summary>
    private async Task<string> EquipAsync(GameSession s, int entry)
    {
        if (ItemConfig.Get(data, entry) is not { Type: ItemTypeEquip }) return $"{entry} is not equipment.";
        string name = $"{data.ItemName(entry)} ({entry})";
        int worn = s.Items.Count(i => i.Entry == entry && i.Grid > 0);
        if (!s.Items.Any(i => i.Entry == entry && i.Grid == 0))
        {
            if (worn > 0) return $"{name} is already worn.";
            await character.AddItemAsync(s, entry, 1);
        }
        if (s.Items.Where(i => i.Entry == entry && i.Grid == 0).MaxBy(i => i.Guid) is not { } item) return $"{name} could not be granted.";
        await character.EquipAsync(s, new EquipItemOprReq { ItemGUID = item.Guid, Opr = EquipItemOprType.EquipOprTypeEquip });
        if (s.Items.FirstOrDefault(i => i.Guid == item.Guid) is not { Grid: > 0 } equipped)
            return $"{name} could not be equipped (not appraised, no grid for it, or the role's level or job may not wear it).";
        return $"Equipped {name} in grid {equipped.Grid}"
            + (worn > 0 && s.Items.Count(i => i.Entry == entry && i.Grid > 0) == worn ? ", replacing the copy worn there." : ".");
    }
}
