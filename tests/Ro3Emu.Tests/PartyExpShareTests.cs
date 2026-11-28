using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Ro3Emu:PartyExpShare (inferred, on by default): a kill's EXP pool shared with the killer's party members on the same map
/// line, evenly (Split) or in full (Full); each part then gets its recipient's own level gap. A 40-member team shares only
/// within the killer's group of five slots. Off, the killer alone gets it.
/// </summary>
public sealed class PartyExpShareTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const long KillExp = 4; // below the fixture's first level-up

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private static GameSession Session(SyntheticHost host, BotClient bot) =>
        host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)bot.Self!.Uuid);

    /// <summary>A party of the leader and the members, in this slot order.</summary>
    private static async Task PartyAsync(TeamType type, BotClient leader, params BotClient[] members)
    {
        await leader.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = type });
        var created = await Next(leader, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        foreach (var member in members)
        {
            await leader.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)member.Self!.Uuid });
            Assert.Equal(0, (await Next(leader, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
            await Next(member, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
            await member.SendGameAsync(TeamService.TeamInviteeResp,
                new InviteeResp { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)leader.Self!.Uuid });
            Assert.Equal(0, (await Next(member, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
        }
    }

    private static MonsterUnit Monster(GameSession killer) => new()
    {
        Uid = 77, SceneEntry = killer.SceneEntry, RuntimeMap = killer.RuntimeMap,
        Template = new MonsterTemplate(10001, 1, 10, 0, 1, 1, 0, 0, 1, 0, 0, 100, 1, 1, 1, 1,
            false, 0, 1000, 5, 10, 0, 1000, .1f, 1000, 1, 500),
        Group = new SpawnGroup(-1, false, 1, 0, [], []), Home = new SpawnPoint(-1, 0, 0, 0, 0, 10001), Hp = 0
    };

    /// <summary>
    /// The leader kills a level-1 monster; returns the Base EXP each party member gained, the leader first. With
    /// <paramref name="pool"/> the kill's pool goes through each recipient's own factors; without it the killer's reward is
    /// fixed at <see cref="KillExp"/> and shared as it is.
    /// </summary>
    private static async Task<long[]> KillAsync(PartyExpShareOptions? share, int members = 1, TeamType type = TeamType.Five,
        long? pool = null, int memberLevel = 1, int? granted = null)
    {
        await using var host = await SyntheticHost.StartAsync(o => { if (share != null) o.PartyExpShare = share; });
        var bots = new List<BotClient>();
        try
        {
            for (int i = 0; i <= members; i++) bots.Add(await host.InWorldAsync(i == 0 ? "Leader" : "Member" + i));
            await PartyAsync(type, bots[0], [.. bots.Skip(1)]);
            var loop = host.Service<GameLoop>();
            var rewards = host.Service<RewardService>();
            var sessions = bots.Select(b => Session(host, b)).ToList();
            if (memberLevel != 1)
            {
                using var http = new HttpClient();
                foreach (var bot in bots.Skip(1))
                    (await http.PostAsync($"{host.Http}/ro3emu/gm?account={bot.Account}", Operator.Command($"!lvl {memberLevel}")))
                        .EnsureSuccessStatusCode();
                Assert.All(sessions.Skip(1), s => Assert.Equal(memberLevel, s.Progress!.Level));
            }
            var before = sessions.Select(s => s.Progress!.Exp).ToList();
            await loop.InvokeAsync(async () =>
            {
                if (pool is { } p) rewards.KillPoolForTest = (_, _) => ([], p);
                else rewards.ResolveKillForTest = (_, _) => ([], KillExp, KillExp);
                await host.Service<CombatService>().Killed.Handlers.Single(h => h.Target == rewards)(sessions[0], Monster(sessions[0]));
            });
            // The members' own grants, queued after the kill committed.
            foreach (var bot in bots.Skip(1).Take(granted ?? (share?.Enabled == false ? 0 : members)))
                await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.GetRewardNotify), Wait);
            await loop.InvokeAsync(() => Task.CompletedTask);
            return [.. sessions.Select((s, i) => s.Progress!.Exp - before[i])];
        }
        finally
        {
            foreach (var bot in bots) await bot.DisposeAsync();
        }
    }

    [Fact]
    public async Task TurnedOffTheKillerAloneGetsTheKillsExp()
    {
        var gained = await KillAsync(new PartyExpShareOptions { Enabled = false });
        Assert.Equal([KillExp, 0L], gained);
    }

    [Fact]
    public async Task ByDefaultTheKillIsSplitEvenly()
    {
        var gained = await KillAsync(null);
        Assert.Equal([KillExp / 2, KillExp / 2], gained);
    }

    [Fact]
    public async Task FullGivesEachMemberTheKillersWholeExp()
    {
        var gained = await KillAsync(new PartyExpShareOptions { Mode = PartyExpShareMode.Full });
        Assert.Equal([KillExp, KillExp], gained);
    }

    [Fact]
    public async Task EachPartGetsItsRecipientsOwnLevelGap()
    {
        // Pool 8 split in two; the fixture's correction halves EXP one level above the monster, for the member alone.
        var gained = await KillAsync(null, pool: 8, memberLevel: 2);
        Assert.Equal([4L, 2L], gained);
    }

    [Fact]
    public async Task AFortyMemberTeamSharesOnlyWithinTheKillersGroupOfFive()
    {
        // Slots 0-4 are the leader's group, slot 5 the next one: it gets nothing and does not raise the party bonus.
        var gained = await KillAsync(new PartyExpShareOptions { Mode = PartyExpShareMode.Full }, members: 5, type: TeamType.Forty,
            granted: 4);
        Assert.Equal([KillExp, KillExp, KillExp, KillExp, KillExp, 0L], gained);
    }

    [Fact]
    public async Task ThePartyBonusesCountOnlyLiveMembersOfTheKillersGroup()
    {
        await using var host = await SyntheticHost.StartAsync();
        var bots = new List<BotClient>();
        try
        {
            for (int i = 0; i <= 5; i++) bots.Add(await host.InWorldAsync(i == 0 ? "Leader" : "Member" + i));
            await PartyAsync(TeamType.Forty, bots[0], [.. bots.Skip(1)]);
            var teams = host.Service<TeamService>();
            var sessions = bots.Select(b => Session(host, b)).ToList();
            // Slots 0-4 make five on the map: TeamExpBuffNum's last step, four extra members' drop weight (fixture 2000).
            Assert.Equal((20000, 18000), await host.Service<GameLoop>().InvokeAsync(() => Task.FromResult((teams.ExpBonus(sessions[0]), teams.DropBonus(sessions[0])))));
            sessions[1].Dead = true;
            Assert.Equal((17500, 16000), await host.Service<GameLoop>().InvokeAsync(() => Task.FromResult((teams.ExpBonus(sessions[0]), teams.DropBonus(sessions[0])))));
        }
        finally
        {
            foreach (var bot in bots) await bot.DisposeAsync();
        }
    }
}
