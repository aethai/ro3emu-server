using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.LoadTest;

/// <summary>
/// S8: S7 in parties of four. Each party forms through the client's own team requests (42001 create, invite, accept)
/// and fights the monster nearest its leader, so most kills have several damage contributors: their kill credit takes
/// every contributor's role reservation at once (TaskObjectives.GetKillTaskParticipantKeys), the party-wide locking a
/// live realm reported as its slowest gameplay path.
/// </summary>
public sealed partial class ScenarioRunner
{
    private const int PartySize = 4;
    private readonly Dictionary<BotActor, BotActor> _leaders = [];

    private BotActor PartyLeader(BotActor actor) => _leaders.GetValueOrDefault(actor, actor);

    private async Task PreparePartiesAsync(CancellationToken ct)
    {
        var ordered = _actors.OrderBy(a => a.Account).ToArray();
        var parties = ordered.Chunk(PartySize).Where(p => p.Length > 1).ToArray();
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(parties.Select(async (party, index) =>
        {
            await gate.WaitAsync(ct);
            try { await FormPartyAsync(party, ct); }
            catch (Exception e) { _failures[600000 + index] = $"party: {e.GetType().Name}: {e.Message}"; }
            finally { gate.Release(); }
        }));
        foreach (var party in parties)
            foreach (var member in party) _leaders[member] = party[0];
    }

    private static async Task FormPartyAsync(BotActor[] party, CancellationToken ct)
    {
        var leader = party[0];
        var wait = TimeSpan.FromSeconds(15);
        using var createdReply = leader.Bot.Inbox.Expect(e => e.IsGame(TeamService.TeamCreate));
        await leader.Bot.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = TeamCreateResp.Parser.ParseFrom((await createdReply.WaitAsync(wait, ct)).Body);
        if (created.Result != 0) throw new InvalidOperationException("Team create refused: " + created.Result);
        foreach (var member in party.Skip(1))
        {
            using var invited = member.Bot.Inbox.Expect(e => e.IsGame(TeamService.TeamNotifyInvitee));
            using var inviteReply = leader.Bot.Inbox.Expect(e => e.IsGame(TeamService.TeamInvite));
            await leader.Bot.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = member.RoleId });
            await inviteReply.WaitAsync(wait, ct);
            await invited.WaitAsync(wait, ct);
            using var accepted = member.Bot.Inbox.Expect(e => e.IsGame(TeamService.TeamInviteeResp));
            await member.Bot.SendGameAsync(TeamService.TeamInviteeResp,
                new InviteeResp { TeamId = created.TeamId, IsAgree = true, InviteGuid = leader.RoleId });
            if (InviteeRespResp.Parser.ParseFrom((await accepted.WaitAsync(wait, ct)).Body).Result != 0)
                throw new InvalidOperationException("Team invite refused.");
        }
    }
}
