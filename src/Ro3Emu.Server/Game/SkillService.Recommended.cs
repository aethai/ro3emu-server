using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The recommended build a career's empty skill-plan slot takes. The client shows five slots ("Switch Build") whose
/// levels it renders from this server, and a new role has four of them empty: switching did nothing a new player could
/// see, because an empty slot is a reset to the free levels. Filling it from the same table the client's own
/// recommendation reads (LC_SkillGrowthHelper.GetAddPointOneConfigByProfession: JobConfig._kProfessionID selects a build,
/// whose JobProfessConfig._iProfessionID keys JobProfessSkillConfig by job rank and job level) means the new player's
/// first switch hands them a build they can play, and the desktop and Android clients alike get it without a patch.
/// </summary>
public sealed partial class SkillService
{
    private const string RecommendedBuildKind = "cd13c/skill-recommended-build";

    /// <summary>The professions the role's career recommends, the career's own builds first and its parents' after.</summary>
    private List<long> RecommendedProfessions(RoleProgress p)
    {
        var professions = new List<long>();
        var builds = new List<long>();
        foreach (int career in Enumerable.Reverse(CareerChain(p.Career)))
        {
            if (_data.Row("JobConfig", career) is not { } row
                || !row.TryGetProperty("_kProfessionID", out var ids) || ids.ValueKind != JsonValueKind.Array) continue;
            foreach (var value in ids.EnumerateArray())
                if (GameData.Number(value) is { } number && (long)number is var build && build > 0 && !builds.Contains(build))
                    builds.Add(build);
        }
        foreach (long build in builds)
            if (JobProfessConfig.Get(_data, build) is { } config && !professions.Contains(config.ProfessionId))
                professions.Add(config.ProfessionId);
        return professions;
    }

    /// <summary>True when a slot holds no level the player paid for: the state BuildFresh and a reset leave it in.</summary>
    private bool EmptyPlan(RoleProgress p, SkillPlan plan) => plan.SkillAssigns.All(assigned => assigned.Value.SkillLevel <= Initial(p, assigned.Key));

    /// <summary>
    /// The row the client's recommendation reads for the role: the profession's highest build at or below its job rank
    /// and level. GMJobProfessSkill 170 picks the same row for an explicit profession.
    /// </summary>
    private JobProfessSkillConfig? RecommendedRow(RoleProgress p, long profession)
    {
        int rank = Rank(p.Career);
        return JobProfessSkillConfig.All(_data)
            .Where(r => r.ProfessionId == profession && (r.JobRank < rank || r.JobRank == rank && r.JobLv <= p.JobLevel))
            .OrderByDescending(r => r.JobRank).ThenByDescending(r => r.JobLv).Cast<JobProfessSkillConfig?>().FirstOrDefault();
    }

    /// <summary>
    /// Writes the recommended build into a slot: its paid levels go back to the free ones, then the recommendation's
    /// skills take their levels, as GMJobProfessSkill does. Skills outside the career's tree are left out. The caller
    /// stages the slot afterwards, so a build the role cannot afford or reach is refused there and nothing is committed.
    /// </summary>
    private long FillRecommendedBuild(RoleProgress p, SkillPlan plan)
    {
        var tree = Tree(p.Career).Select(t => t.Group).ToHashSet();
        foreach (var row in RecommendedProfessions(p))
        {
            if (RecommendedRow(p, row) is not { } build) continue;
            foreach (var (group, assigned) in plan.SkillAssigns)
                assigned.SkillLevel = IsNormalGroup(p, group) ? 1 : Initial(p, group);
            foreach (long id in build.DescData)
            {
                if (SkillConfig.Get(_data, id) is not { } skill || !tree.Contains((int)skill.SkillId)) continue;
                int group = (int)skill.SkillId;
                plan.SkillAssigns[group] = new SkillData { SkillId = group, SkillLevel = Math.Max((int)skill.Level, Initial(p, group)) };
            }
            return row;
        }
        return 0;
    }

    /// <summary>
    /// A slot the player has not filled takes the career's recommended build, so "Switch Build" gives a new player a
    /// working build instead of an empty page. Only a role that has not spent a skill point yet is filled: once the
    /// player has allocated anything, an empty slot stays the blank canvas a reset leaves, as the client's own
    /// recommended-build reset expects (LC_SkillGrowthHelper.ResetAllChanged).
    /// </summary>
    internal long RecommendedBuildFor(GameSession session, DBSkillData plans, RoleProgress progress, int index)
    {
        if (index is < 0 or >= PlanCount || index >= plans.SkillAssignPlans.Count) return 0;
        if (SpentPoints(progress) != 0) return 0;
        var plan = plans.SkillAssignPlans[index];
        if (!EmptyPlan(progress, plan)) return 0;
        long profession = FillRecommendedBuild(progress, plan);
        if (profession == 0) return 0;
        _log.LogInformation("{Name} switches to empty build {Plan}: recommended build for profession {Profession} fills it",
            session.Role?.Name, index, profession);
        return profession;
    }
}
