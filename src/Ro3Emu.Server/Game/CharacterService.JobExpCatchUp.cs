using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// One Job EXP grant for roles older than <see cref="JobExpFromBase"/>. (Not original: it repairs this server's earlier
/// rule.) Their quests paid no Job EXP, so a role can stand at Base 30 far below the Job 40 that Job Advance asks with it,
/// and from now on kills bring only their share. At its first login since the rule every role gets what its total Base
/// EXP should have brought, as far as its current rank has room; nobody loses Job EXP it already has.
/// </summary>
public sealed partial class CharacterService
{
    /// <summary>Role blob "job_exp_catchup" (JSON): present once the role's one grant is decided, whatever its amount.</summary>
    public sealed class JobExpCatchUpState
    {
        public long Granted { get; set; }
    }

    internal async Task CatchUpJobExpAsync(GameSession session)
    {
        long role = session.Role!.Id;
        if (roles.LoadJobExpCatchUp(role) is { Length: > 0 }) return;
        using var held = await LockAsync(session);
        long owed = JobExpOwed(session.Progress!);
        if (owed > 0)
        {
            // The natural source allows one grant per role, whatever happens to the marker written after it.
            var source = new OwnedAwardSource("cd13e/grant", $"job-exp-catchup/{role}", new { Version = 1, Granted = owed });
            try
            {
                if (!await ApplyAwardAsync(session, new AwardPlan(0, 0, [new AwardGrant(JobExpItem, checked((int)owed))]),
                    stateLockHeld: true, ownedAward: source)) return;
            }
            catch (EconomyOperationConflictException)
            {
                LogJobExpCaughtUp(log, role);
                owed = 0;
            }
        }
        roles.SaveJobExpCatchUp(role, JsonSerializer.SerializeToUtf8Bytes(new JobExpCatchUpState { Granted = owed }));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Role {Role} already had its Job EXP catch-up")]
    private static partial void LogJobExpCaughtUp(ILogger logger, long role);

    /// <summary>Base EXP's share of Job EXP less the Job EXP earned, through the ranks up to the current one, held to the
    /// room left in that rank as ApplyExp would hold it.</summary>
    private long JobExpOwed(RoleProgress p)
    {
        long baseExp = p.Exp;
        for (int level = 1; level < p.Level; level++) baseExp += BaseExpNeed(level);
        long earned = p.JobExp;
        for (int rank = 1; rank < JobRank(p.Career); rank++)
            for (int level = 1; JobLvConfig.Get(data, rank * 100L + level) is { } row && level < row.MaxJobLv; level++)
                earned += row.JobeExp;
        for (int level = 1; level < p.JobLevel; level++) earned += JobExpNeed(p.Career, level);
        long room = -p.JobExp;
        for (int level = p.JobLevel; level < MaxJobLevel(p.Career); level++) room += JobExpNeed(p.Career, level);
        return Math.Max(0, Math.Min(JobExpFromBase(baseExp) - earned, room));
    }
}
