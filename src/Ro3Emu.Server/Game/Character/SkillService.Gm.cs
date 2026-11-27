using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The GM panel ops that build a class preset's skills (GMCombinationConfig): a profession's skill build, the bar and the
/// queue. They change the current plan as 31151 / 31153 / 31159 would and publish 5025, without those replies.
/// </summary>
public sealed partial class SkillService
{
    /// <summary>
    /// GMJobProfessSkill 170: the current plan takes a profession's recommended skills at the role's job rank and level,
    /// JobProfessSkillConfig[profession * 1000 + rank * 100 + job level]._kDescData, the row the client's own
    /// recommendation reads (LC_SkillGrowthHelper: GetAddPointOneConfigByProfession with jobAdvanceLevel and jobLevel).
    /// (inferred) Without that row the nearest one below it; the plan's other skills go back to their free level as a
    /// reset would, and a skill outside the career's tree is left out.
    /// </summary>
    internal async Task<string> GmProfessionSkillsAsync(GameSession session, long profession)
    {
        string reply;
        bool learned = false;
        using (await CharacterService.Lock(session))
        {
            var plans = Build(session);
            var candidate = CharacterService.GenreCloneProgress(session.Progress!);
            var staged = StageProfessionBuild(session, plans, candidate, profession, fitPoints: false);
            if (staged.Row is not { } row) return $"Profession {profession} has no skill build up to job rank {Rank(session.Progress!.Career)}.";
            if (staged.Code != 0) reply = $"Skill build {row.Id} refused (error {staged.Code}).";
            else
            {
                PruneLoads(plans.SkillAssignPlans[plans.CurrentPlanIndex], candidate);
                await CommitAsync(session, plans, candidate, "cd13c/skill-gm-profession");
                await SyncAsync(session);
                learned = true;
                reply = $"Skill build {row.Id}: {row.DescData.Length - staged.Outside.Count} skills"
                    + (staged.Outside.Count > 0 ? $", not in this career's tree: {string.Join(' ', staged.Outside)}." : ".");
            }
        }
        if (learned) foreach (var handler in Learned.Handlers) await handler(session);
        return reply;
    }

    /// <summary>A profession's recommended row staged into the current plan: the row (null without one), the row ids
    /// outside the career's tree, the levels <see cref="FitToPoints"/> took off and StageSkillPlan's result.</summary>
    private readonly record struct BuildStage(JobProfessSkillConfig? Row, List<long> Outside, int Cut, int Code);

    /// <summary>
    /// The current plan takes the recommended row at the role's job rank and level (or the nearest below, see
    /// <see cref="GmProfessionSkillsAsync"/>): its other skills go back to their free level, as the client's
    /// LC_SkillGrowthHelper.RecommendAddPoint first calls ResetAllChanged, then each listed group takes its row's level.
    /// <paramref name="candidate"/> becomes the plan's progress when the result is 0.
    /// </summary>
    private BuildStage StageProfessionBuild(GameSession session, DBSkillData plans, RoleProgress candidate, long profession, bool fitPoints)
    {
        var p = session.Progress!;
        int rank = Rank(p.Career);
        var build = JobProfessSkillConfig.All(_data)
            .Where(r => r.ProfessionId == profession && (r.JobRank < rank || r.JobRank == rank && r.JobLv <= p.JobLevel))
            .OrderByDescending(r => r.JobRank).ThenByDescending(r => r.JobLv).Cast<JobProfessSkillConfig?>().FirstOrDefault();
        if (build is not { } row) return new(null, [], 0, ErrorCodes.Parameter);
        var selected = plans.SkillAssignPlans[plans.CurrentPlanIndex];
        foreach (var (group, assigned) in selected.SkillAssigns)
            assigned.SkillLevel = IsNormalGroup(p, group) ? 1 : Initial(p, group);
        var tree = Tree(p.Career).Select(t => t.Group).ToHashSet();
        var outside = new List<long>();
        var listed = new List<int>();
        foreach (long id in row.DescData)
        {
            if (SkillConfig.Get(_data, id) is not { } skill || !tree.Contains((int)skill.SkillId)) { outside.Add(id); continue; }
            int group = (int)skill.SkillId;
            selected.SkillAssigns[group] = new SkillData { SkillId = group, SkillLevel = Math.Max((int)skill.Level, Initial(p, group)) };
            if (!listed.Contains(group)) listed.Add(group);
        }
        int cut = fitPoints ? FitToPoints(p, selected, listed) : 0;
        return new(row, outside, cut, StageSkillPlan(session, plans, candidate, plans.CurrentPlanIndex));
    }

    /// <summary>
    /// (inferred) A recommended row can cost more points than the job level has earned: the export has rows up to five
    /// points over the rank's JobLvConfig._iSkillPoint total, and the client applies a row as it is (RecommendAddPoint checks
    /// no points). The original's handling is not known. The row is not refused whole: while a rank is overspent, its last
    /// listed skill whose lower level keeps every prerequisite of the plan met (PrerequisitesMet) loses a level. Returns
    /// the levels taken off.
    /// </summary>
    private int FitToPoints(RoleProgress p, SkillPlan plan, List<int> listed)
    {
        var preview = PlanPreview(p, plan);
        var rankOf = Tree(p.Career).ToDictionary(t => t.Group, t => Rank(t.Career));
        int cut = 0;
        for (var left = SkillPoints(preview); left.FirstOrDefault(kv => kv.Value < 0) is { Value: < 0 } over; left = SkillPoints(preview))
        {
            int lowered = Enumerable.Reverse(listed).FirstOrDefault(g => rankOf.GetValueOrDefault(g) == over.Key && TryLowerOne(preview, g));
            if (lowered == 0) break;
            plan.SkillAssigns[lowered].SkillLevel = Math.Max(preview.Skills.GetValueOrDefault(lowered), Initial(preview, lowered));
            cut++;
        }
        return cut;
    }

    /// <summary>One level off a paid group of <paramref name="preview"/>, kept only when the plan's prerequisites still hold.</summary>
    private bool TryLowerOne(RoleProgress preview, int group)
    {
        int level = preview.Skills.GetValueOrDefault(group);
        if (level <= Initial(preview, group)) return false;
        if (level - 1 > Initial(preview, group)) preview.Skills[group] = level - 1;
        else preview.Skills.Remove(group);
        if (PrerequisitesMet(preview)) return true;
        preview.Skills[group] = level;
        return false;
    }

    /// <summary>
    /// GMSetSkillWheel 172: the bar from SkillConfig row ids, slot n = the n-th id (ManualLoadSkill, as 31153 fills it),
    /// 0 an empty slot. A normal attack row (the presets name JobAttackConfig's, 1001001 for a Swordman) is the career's
    /// own, as a saved bar shows it (BarGroup). (inferred) A skill the role has not learned, a passive or a repeat leaves
    /// its slot empty; a level above the learned one is lowered to it.
    /// </summary>
    internal async Task<string> GmSkillBarAsync(GameSession session, IReadOnlyList<long> rows)
    {
        using var held = await CharacterService.Lock(session);
        var p = session.Progress!;
        var plans = Build(session);
        var selected = plans.SkillAssignPlans[plans.CurrentPlanIndex];
        selected.ManualLoadSkill.Clear();
        var empty = new List<long>();
        foreach (long id in rows.Take(BarSlots))
        {
            var skill = SkillConfig.Get(_data, id);
            int group = skill is not { } s ? 0 : s.IsNormalAtk == 1 ? CareerNormalGroup(p.Career) : (int)s.SkillId;
            int learned = group > 0 ? Level(p, group) : 0;
            bool fits = learned > 0 && IsActive(group) && selected.ManualLoadSkill.All(load => load.SkillId != group);
            if (!fits && id != 0) empty.Add(id);
            selected.ManualLoadSkill.Add(fits ? new SkillData { SkillId = group, SkillLevel = Math.Min(learned, (int)skill!.Value.Level) } : new SkillData());
        }
        if (selected.ManualLoadSkill.Count == 0) selected.ManualLoadSkill.Add(new SkillData());
        var candidate = CharacterService.GenreCloneProgress(p);
        int code = StageSkillPlan(session, plans, candidate, plans.CurrentPlanIndex);
        if (code != 0) return $"Skill bar refused (error {code}).";
        await CommitAsync(session, plans, candidate, "cd13c/skill-gm-bar");
        await SyncAsync(session);
        empty.AddRange(rows.Skip(BarSlots).Where(id => id != 0));
        int placed = selected.ManualLoadSkill.Count(load => load.SkillId > 0);
        return $"Skill bar: {placed} skills" + (empty.Count > 0 ? $", left out (not learned, passive or no slot): {string.Join(' ', empty)}." : ".");
    }

    /// <summary>
    /// GMSkillQueue 38: the current plan's queue in the given order (skill group ids, queue index 0 first) at the learned
    /// levels, as 31159 fills it one index at a time. (inferred) It replaces the whole queue; a skill not learned or not
    /// queueable (SkillConfig._iQueue, or the combat override) is left out.
    /// </summary>
    internal async Task<string> GmSkillQueueAsync(GameSession session, IReadOnlyList<int> groups)
    {
        using var held = await CharacterService.Lock(session);
        var p = session.Progress!;
        var plans = Build(session);
        var queue = plans.SkillAssignPlans[plans.CurrentPlanIndex].SkillQueue;
        queue.Clear();
        int capacity = (int)(Data.Config.PublicParams.Of(_data).QueueNum ?? 12);
        var skipped = new List<int>();
        foreach (int group in groups)
        {
            int learned = Level(p, group);
            bool queueable = SkillConfig.Get(_data, RealId(group, 1)) is { } row && (QueueEligibility?.Invoke(session, group) ?? row.Queue == 1);
            if (learned <= 0 || !queueable || queue.Count >= capacity || queue.Values.Any(q => q.SkillId == group)) { skipped.Add(group); continue; }
            queue[queue.Count] = new SkillData { SkillId = group, SkillLevel = learned };
        }
        if (queue.Count == 0) queue[0] = new SkillData(); // the client pairs() it unguarded
        await CommitAsync(session, plans, kind: "cd13c/skill-gm-queue");
        await SyncAsync(session);
        return $"Skill queue: {groups.Count - skipped.Count} skills"
            + (skipped.Count > 0 ? $", left out (not learned or not queueable): {string.Join(' ', skipped)}." : ".");
    }
}
