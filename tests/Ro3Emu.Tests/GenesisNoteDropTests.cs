using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class GenesisNoteDropTests
{
    private sealed class Clock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void Set(long seconds) => Interlocked.Exchange(ref _seconds, seconds);
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))),
            TimeSpan.FromSeconds(10));
        return parser.ParseFrom(packet.Body);
    }

    private static async Task Gm(SyntheticHost host, BotClient bot, string command)
    {
        using var http = new HttpClient();
        using var reply = await http.PostAsync($"{host.Http}/ro3emu/gm?account=alice", Operator.Command(command));
        reply.EnsureSuccessStatusCode();
        if (!command.StartsWith("!warp ", StringComparison.Ordinal)) return;
        var change = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = change.SceneEntry });
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotePillarCompletesWithOrWithoutAPendingSong(bool startSeen)
    {
        var clock = new Clock();
        await using var host = await SyntheticHost.StartAsync(o =>
        {
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.PlayBook = 101;
            o.SeasonStart = "2026-10-05";
        }, clock);
        const int map = 10110, pillar = 8000305, play = 7000104, finish = 7000100;
        var seasons = host.Service<SeasonService>();
        long start = new SeasonClock(host.Service<GameData>(), seasons.Start, 101, seasons.Zone)
            .Build().StageTimeInfo[101002].StartTime + 60;
        clock.Set(start);
        var point = host.Service<WorldData>().Map(map)!.SpawnGroups.SelectMany(g => g.Points).Single(p => p.ConfigId == pillar);
        long uid = NewActivityService.ConchUid(map, point.Id);
        await using var alice = await host.InWorldAsync("Alice");
        await Gm(host, alice, "!lvl 99");
        await Gm(host, alice, "!warp 10110 194.77 203.36");
        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.Uuid == (ulong)uid && b.EntityTemplateID == pillar));
        var session = host.Service<SessionRegistry>().All().Single(s => s.Account == "alice");
        async Task<int> Interact(int action)
        {
            await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
            { SceneConfigId = map, NpcUid = uid, InteractionId = action });
            return (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result;
        }
        long Notes() => session.Items.Where(i => i.Entry is >= 1113001 and <= 1113007).Sum(i => i.Stack);
        long before = Notes();
        await Gm(host, alice, "!jump 206 203.36");
        Assert.Equal(ErrorCodes.NpcTooFar, await Interact(finish));
        async Task WaitForRecovery()
        {
            var release = await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
                n => n.DataInfo.InteractConfigId == finish);
            long end = release.DataInfo.CollectionInfo.PlayInfo.PlayEndTimeStamp;
            Assert.Equal(clock.GetUtcNow().AddSeconds(3).ToUnixTimeMilliseconds(), end);
            clock.Set(end / 1000);
            await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
                n => n.Interrupt && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
            Assert.Equal(before, Notes());
        }
        await WaitForRecovery();
        await Gm(host, alice, "!jump 194.77 203.36");
        if (startSeen)
        {
            Assert.Equal(0, await Interact(play));
            clock.Set(start + 185);
            Assert.Equal(ErrorCodes.NpcParameter, await Interact(finish));
            await WaitForRecovery();
            Assert.Equal(0, await Interact(play));
        }
        var correction = session.Position.Clone();
        correction.X += 0.25f;
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = correction, BStop = true, SpeedRate = 1 });
        Assert.Equal(0, await Interact(finish));
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.ExploreCollection && n.DataInfo.InteractConfigId == finish);
        Assert.Equal(before, Notes());
        Assert.Equal(ErrorCodes.Cooldown, await Interact(play));
        clock.Set(clock.GetUtcNow().AddSeconds(3).ToUnixTimeSeconds());
        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains((ulong)uid));
        Assert.Equal(before + 3, Notes());
        var collected = session.NewActivities!.ActivityInfo[111].ActivityParam.WorldExploration.MapCollect[map];
        Assert.Equal(1, collected.CollectCount[point.Id]);
        Assert.Equal(ErrorCodes.NpcMissing, await Interact(finish));
    }

    [ClientDataFact]
    public async Task SpecialConchAppearsInItsTriggerAndPaysOnceAfterTheCast()
    {
        var clock = new Clock();
        await using var host = await SyntheticHost.StartAsync(o =>
        {
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.PlayBook = 101;
            o.SeasonStart = "2026-10-05";
        }, clock);
        const int map = 10110, point = 491;
        long uid = NewActivityService.ConchUid(map, point);
        var seasons = host.Service<SeasonService>();
        long start = new SeasonClock(host.Service<GameData>(), seasons.Start, 101, seasons.Zone)
            .Build().StageTimeInfo[101002].StartTime + 60;
        clock.Set(start);
        await using var alice = await host.InWorldAsync("Alice");
        await Gm(host, alice, "!lvl 99");
        await Gm(host, alice, "!warp 10110 95.4 241");
        var session = host.Service<SessionRegistry>().All().Single(s => s.Account == "alice");
        async Task<int> Pick(int action)
        {
            await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
            { SceneConfigId = map, NpcUid = uid, InteractionId = action });
            return (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result;
        }
        async Task EnterTrigger()
        {
            await Gm(host, alice, "!jump 89.65 238.27"); // inside region 547, the conch's own one-unit circle
            await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
                v => v.Bases.Any(b => b.Uuid == (ulong)uid && b.EntityTemplateID == 8100004));
        }

        Assert.Equal(ErrorCodes.NpcMissing, await Pick(7000008));
        await EnterTrigger();
        Assert.Equal(ErrorCodes.NpcParameter, await Pick(7000006));
        Assert.Equal(0, await Pick(7000008));
        var cast = await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.ExploreCollection);
        Assert.Equal(7000008, cast.DataInfo.InteractConfigId);
        await Gm(host, alice, "!jump 95.4 241");
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser, n => n.Interrupt);
        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains((ulong)uid));
        Assert.Equal(ErrorCodes.NpcMissing, await Pick(7000008));

        await EnterTrigger();
        long before = session.Progress!.Currency.GetValueOrDefault(CharacterService.ZenyId);
        Assert.Equal(0, await Pick(7000008));
        clock.Set(start + 3);
        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains((ulong)uid));
        // Award 100118002 pays 1,000 Zeny plus one pick from group 1230, which can itself pay Zeny.
        long paid = session.Progress.Currency.GetValueOrDefault(CharacterService.ZenyId) - before;
        Assert.True(paid is 1000 or (>= 2000 and <= 4000) or 6000, $"Unexpected special conch Zeny reward: {paid}");
        var collected = session.NewActivities!.ActivityInfo[111].ActivityParam.WorldExploration.MapCollect[map];
        Assert.Equal(1, collected.CollectCount[point]);
        Assert.Equal(ErrorCodes.NpcMissing, await Pick(7000008));
    }

    [ClientDataFact]
    public async Task GenesisFieldAndGatherNotesShareFivePerDayAcrossReconnect()
    {
        var clock = new Clock();
        await using var host = await SyntheticHost.StartAsync(o =>
        {
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.PlayBook = 101;
            o.SeasonStart = "2026-10-05";
        }, clock);
        var seasons = host.Service<SeasonService>();
        var data = host.Service<GameData>();
        long start = new SeasonClock(data, seasons.Start, 101, seasons.Zone).Build().StageTimeInfo[101002].StartTime + 60;
        clock.Set(start);
        var alice = await host.InWorldAsync("Alice");
        await Gm(host, alice, "!lvl 99");
        await Gm(host, alice, "!item 1291000 1");
        await Gm(host, alice, "!warp 10522 245.93 220.27");
        var view = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityTemplateID == 7010001 && b.CollectionInfo != null));
        var node = view.Bases.First(b => b.EntityTemplateID == 7010001 && b.CollectionInfo != null);
        await Gm(host, alice, FormattableString.Invariant($"!jump {node.MovementInfo.Pos.X:0.00} {node.MovementInfo.Pos.Z:0.00}"));
        await alice.SendGameAsync(LifeJobService.SwitchLife, new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
        Assert.Equal(0, (await Next(alice, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);
        var session = host.Service<SessionRegistry>().All().Single(s => s.Account == "alice");
        var notes = host.Service<NewActivityService>().NoteDrops;
        notes.Draw = _ => 0; // deterministically win and select the first configured note
        static long Count(GameSession s) => s.Items.Where(i => i.Entry is >= 1113001 and <= 1113007).Sum(i => i.Stack);
        long before = Count(session);
        await alice.SendGameAsync(GatheringService.Interaction, new NpcInteractionReq
        { NpcUid = (long)node.Uuid, InteractionId = 70100001, SceneConfigId = 10522, Pos = node.MovementInfo.Pos });
        Assert.Equal(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Idle && !n.Interrupt);
        Assert.Equal(before + 1, Count(session));
        Assert.Equal(4, session.Restrictions!.RestrictCount[1007].Count);

        await Gm(host, alice, "!warp 10112");
        var loop = host.Service<GameLoop>();
        var owners = host.Service<OwnerRegistry>();
        var rewards = host.Service<RewardService>();
        var afk = host.Service<WildAfkService>();
        await loop.InvokeAsync(() => owners.RunSessionAsync("exploration field rewards", [ReservationKey.Realm], session, async () =>
        {
            session.AutoBattle = true;
            var monsters = host.Service<World>().UnitsIn(session.WorldScene).OfType<MonsterUnit>()
                .Where(m => afk.IsExplorationHunt(session, m)).Take(7).ToArray();
            Assert.Equal(7, monsters.Length);
            session.AutoBattle = false;
            await rewards.PrepareKilledAsync(session, monsters[0]);
            Assert.Equal(before + 1, Count(session));
            session.AutoBattle = true;
            notes.Draw = n => n - 1;
            await rewards.PrepareKilledAsync(session, monsters[1]);
            Assert.Equal(before + 1, Count(session));
            notes.Draw = _ => 0;
            foreach (var monster in monsters.Skip(2))
            {
                await rewards.PrepareKilledAsync(session, monster);
                await rewards.PrepareKilledAsync(session, monster); // a replayed death pays once
            }
            Assert.Equal(before + 5, Count(session));
            Assert.Equal(0, notes.Remaining(session)!.Count);
        }));
        await alice.DisposeAsync();
        await using var relog = await host.InWorldAsync("Alice", create: false);
        await Next(relog, NewActivityService.ActivitySync, DBNewActivityInfoIncrement.Parser);
        var restored = host.Service<SessionRegistry>().All().Single(s => s.Account == "alice");
        Assert.Equal(before + 5, Count(restored));
        Assert.Equal(0, notes.Remaining(restored)!.Count);
        clock.Set(notes.Day().End);
        Assert.Equal(5, notes.Remaining(restored)!.Count);
    }

}
