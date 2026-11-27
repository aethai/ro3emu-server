using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class SeasonTalentService
{
    // enum_SeasonTalentType: SkillPoint (the free gate of a profession choice), Profession_L, Profession_M.
    private const int ChoiceNodeType = 5, ProfessionBranchType = 6, ProfessionOptionType = 7;

    // The selection panel sends the chosen Profession_M, not separate upgrades of its
    // SkillPoint and Profession_L parents (UI_Sys_Skill_SeasonTalent_SelectPanelCtrl._onClickUpgrade).
    // Resolve the same job-filtered graph as Lua_CFG_SeasonTalentConfig.GetTreeDataByTreeID.
    private (long Gate, long Branch)? ChoicePath(long id, int career)
    {
        Nodes();
        if (SeasonTalentConfig.Get(_data, id) is not { } node) return null;
        long tree = node.TalentTreeId, branch = id, type = node.Type;
        if (type is not (ProfessionBranchType or ProfessionOptionType)
            || Effect(node.LevelGroupId, 1, career) == null) return null;
        if (type == ProfessionOptionType)
        {
            var parents = _parents!.GetValueOrDefault((id, tree), [])
                .Where(p => SeasonTalentConfig.Get(_data, p.Parent) is { Type: ProfessionBranchType }).ToArray();
            if (parents.Length != 1) return null;
            branch = parents[0].Parent;
        }
        var branchRow = SeasonTalentConfig.Get(_data, branch)!.Value;
        if (Effect(branchRow.LevelGroupId, 1, career) == null) return null;
        // A branch with selectable children must name one; otherwise the choice is incomplete.
        if (type == ProfessionBranchType && branchRow.Afterids.Any(child =>
            SeasonTalentConfig.Get(_data, child) is { Type: ProfessionOptionType } r
            && Effect(r.LevelGroupId, 1, career) != null)) return null;
        var gates = _parents!.GetValueOrDefault((branch, tree), [])
            .Where(p => SeasonTalentConfig.Get(_data, p.Parent) is { Type: ChoiceNodeType }).ToArray();
        return gates.Length == 1 ? (gates[0].Parent, branch) : null;
    }

    private IEnumerable<long> ChoiceMembers(long gate)
    {
        if (SeasonTalentConfig.Get(_data, gate) is not { } row) yield break;
        foreach (long branch in row.Afterids)
        {
            if (SeasonTalentConfig.Get(_data, branch) is not { Type: ProfessionBranchType } b) continue;
            yield return branch;
            foreach (long option in b.Afterids)
                if (SeasonTalentConfig.Get(_data, option) is { Type: ProfessionOptionType }) yield return option;
        }
    }

    private async Task SelectProfessionAsync(GameSession s, SeasonTalentUpgradeReq req)
    {
        int result = 0;
        using (await CharacterService.Lock(s))
        {
            if (s.Season.SeasonTalent == null || s.Progress == null || !Available(s)) result = ErrorCodes.FeatureUnavailable;
            else
            {
                BuildUnlocks(s);
                s.StatsMemo = null; // the unlocks changed in place (also when the choice is then refused)
                var path = ChoicePath(req.TalentId, s.Progress.Career);
                if (path == null) result = ErrorCodes.TalentInvalid;
                else if (!s.Season.SeasonTalent.UnlockedNode.GetValueOrDefault(path.Value.Gate)) result = ErrorCodes.TalentLocked;
                else
                {
                    var next = s.Season.SeasonTalent.Clone();
                    var plan = next.Plan[next.CurPlanId];
                    // (inferred) choosing again replaces this one choice, within this plan only.
                    foreach (long member in ChoiceMembers(path.Value.Gate)) plan.TalentNodes[member].CurrentLevel = 0;
                    plan.TalentNodes[path.Value.Gate].CurrentLevel = 1;
                    plan.TalentNodes[path.Value.Branch].CurrentLevel = 1;
                    plan.TalentNodes[req.TalentId].CurrentLevel = 1;
                    BuildUnlocks(s, next);
                    try
                    {
                        var transaction = _roles.NewTransaction();
                        transaction.RoleBlob(s.Role!.Id, "season_talent", next.ToByteArray());
                        await _roles.CommitAsync(transaction, "season-talent/profession", _ => s.Season.SeasonTalent = next);
                    }
                    catch (Exception error) when (error is SqliteException
                        or Accounts.Persistence.PersistenceException or InvalidOperationException)
                    {
                        _log.LogError(error, "Profession talent selection {Node} failed for {Role}", req.TalentId, s.Role!.Id);
                        result = ErrorCodes.Unknown;
                    }
                    if (result == 0)
                    {
                        await CharacterService.SyncAsync(s, TalentSync, next.Clone());
                        await _character.RefreshStatsAsync(s);
                        await RefreshEffectsAsync(s);
                    }
                }
            }
        }
        await s.SendGameAsync(Upgrade, new SeasonTalentUpgradeResp { Result = result, TalentId = req.TalentId });
    }
}
