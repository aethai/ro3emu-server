using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Ro3Emu:PartyExpShare (an operator option, not original): a kill's Base EXP shared with the killer's party members on the
/// same map line, evenly (Split) or in full (Full). Off, the killer alone gets it, as on the original server.
/// </summary>
public sealed class PartyExpShareTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const long KillExp = 4; // below the fixture's first level-up

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private static GameSession Session(SyntheticHost host, BotClient bot) =>
        host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)bot.Self!.Uuid);

    private static async Task PartyAsync(BotClient leader, BotClient member)
    {
        await leader.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five });
        var created = await Next(leader, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        await leader.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)member.Self!.Uuid });
        Assert.Equal(0, (await Next(leader, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        await Next(member, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await member.SendGameAsync(TeamService.TeamInviteeResp,
            new InviteeResp { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)leader.Self!.Uuid });
        Assert.Equal(0, (await Next(member, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
    }

    private static MonsterUnit Monster(GameSession killer) => new()
    {
        Uid = 77, SceneEntry = killer.SceneEntry, RuntimeMap = killer.RuntimeMap,
        Template = new MonsterTemplate(10001, 1, 10, 0, 1, 1, 0, 0, 1, 0, 0, 100, 1, 1, 1, 1,
            false, 0, 1000, 5, 10, 0, 1000, .1f, 1000, 1, 500),
        Group = new SpawnGroup(-1, false, 1, 0, [], []), Home = new SpawnPoint(-1, 0, 0, 0, 0, 10001), Hp = 0
    };

    /// <summary>Kills a monster for the leader and returns the Base EXP the leader and the member gained.</summary>
    private static async Task<(long Killer, long Member)> KillAsync(PartyExpShareOptions share)
    {
        await using var host = await SyntheticHost.StartAsync(o => o.PartyExpShare = share);
        await using var alice = await host.InWorldAsync("Leader");
        await using var bob = await host.InWorldAsync("Member");
        await PartyAsync(alice, bob);
        var loop = host.Service<GameLoop>();
        var rewards = host.Service<RewardService>();
        var killer = Session(host, alice);
        var member = Session(host, bob);
        long killerBefore = killer.Progress!.Exp, memberBefore = member.Progress!.Exp;
        await loop.InvokeAsync(async () =>
        {
            rewards.ResolveKillForTest = (_, _) => ([], KillExp, KillExp);
            await host.Service<CombatService>().Killed.Single(h => h.Target == rewards)(killer, Monster(killer));
        });
        if (share.Enabled)
            await bob.Inbox.WaitAsync(e => e.IsGame(CharacterService.GetRewardNotify), Wait); // the member's own grant
        await loop.InvokeAsync(() => Task.CompletedTask); // the queued grant has run
        return (killer.Progress!.Exp - killerBefore, member.Progress!.Exp - memberBefore);
    }

    [Fact]
    public async Task OffByDefaultTheKillerAloneGetsTheKillsExp() =>
        Assert.Equal((KillExp, 0L), await KillAsync(new PartyExpShareOptions()));

    [Fact]
    public async Task SplitDividesTheExpEvenlyBetweenTheKillerAndTheMembersOnTheMap() =>
        Assert.Equal((KillExp / 2, KillExp / 2), await KillAsync(new PartyExpShareOptions { Enabled = true }));

    [Fact]
    public async Task FullGivesEachMemberTheKillersWholeExp() =>
        Assert.Equal((KillExp, KillExp), await KillAsync(new PartyExpShareOptions { Enabled = true, Mode = PartyExpShareMode.Full }));
}
