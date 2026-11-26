using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Two rules from a forum report: a rider in mount flight is out of every monster's reach (CombatService.Airborne),
/// and a party fighting on the same map gets the item drop bonus its tip promises (TeamService.DropBonus, invented
/// TeamDropBuffWeights 2000 in the fixture).
/// </summary>
public sealed class FlyingAndPartyDropTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private static GameSession Session(SyntheticHost host, BotClient bot) =>
        host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)bot.Self!.Uuid);

    [Fact]
    public async Task AMonsterNeitherTakesNorKeepsAFlyingRider()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = await host.InWorldAsync("Flyer");
        await host.Service<GameLoop>().InvokeAsync(async () =>
        {
            var s = Session(host, bot);
            var monsters = host.Service<MonsterService>();
            var at = s.Position;
            var m = await monsters.SpawnSingleAsync(s.SceneEntry, 16320001, new MapPoint(at.X + 1, at.Y, at.Z, 0), s.InstanceId);
            Assert.NotNull(m);
            Assert.True(monsters.TargetVisible?.Invoke(m, s));
            s.WorldState.Flying = true;
            Assert.False(monsters.TargetVisible?.Invoke(m, s));
            monsters.Engage(m, s);
            Assert.Null(m.Target); // never taken while it flies
            s.WorldState.Flying = false;
            monsters.Engage(m, s);
            Assert.Same(s, m.Target);
            s.WorldState.Flying = true; // takes off mid-fight: the next fight tick loses it (MonsterService.Fight)
            Assert.False(monsters.TargetVisible?.Invoke(m, s));
        });
    }

    [Fact]
    public async Task APartyOnTheSameMapRaisesTheDropRateByTheWeightPerExtraMember()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Leader");
        await using var bob = await host.InWorldAsync("Member");
        var teams = host.Service<TeamService>();
        var loop = host.Service<GameLoop>();
        Assert.Equal(10000, await loop.InvokeAsync(() => Task.FromResult(teams.DropBonus(Session(host, alice)))));

        await alice.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five });
        var created = await Next(alice, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)bob.Self!.Uuid });
        Assert.Equal(0, (await Next(alice, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        await Next(bob, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await bob.SendGameAsync(TeamService.TeamInviteeResp,
            new InviteeResp { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)alice.Self!.Uuid });
        Assert.Equal(0, (await Next(bob, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);

        Assert.Equal(12000, await loop.InvokeAsync(() => Task.FromResult(teams.DropBonus(Session(host, alice)))));
        Assert.Equal(12000, await loop.InvokeAsync(() => Task.FromResult(teams.DropBonus(Session(host, bob)))));
    }
}
