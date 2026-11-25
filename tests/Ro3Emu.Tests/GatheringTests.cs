using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Real RefreshInfo miner-node acquisition and authoritative life gathering.</summary>
public sealed class GatheringTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TestServer.DefaultWait;
    private readonly FakeTimeProvider _clock = TestServer.CreateFakeClock();
    private TestServer _server = null!;
    private const long Scene = 10522;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartClientAsync(o =>
        {
            o.Ro1AggressiveMonsters = false; // field monsters near the nodes would interrupt the gathering casts
        }, _clock);
    }

    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private Task<BotClient> InWorld(string name, bool create = true) => _server.InWorldAsync(name, create);

    private static Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T> => TestServer.NextAsync(bot, msgId, parser, match);

    private Task Gm(string account, string command) => _server.GmAsync(account, command);

    private static int Count(BotClient bot, uint id) => bot.Inbox.Snapshot().Count(e => e.IsGame(id));

    private Task<T> OnLoop<T>(string account, Func<GameSession, T> read) =>
        _server.OnLoopAsync(() => Task.FromResult(read(_server.Session(account))));

    private async Task<EntityBase> EnterMinerField(BotClient bot, string account)
    {
        await Gm(account, "!item 1291000 1"); // learning Miner consumes the workshop certificate
        await Gm(account, "!warp 10522 245.93 220.27");
        var change = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(Scene, change.SceneEntry);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = Scene });
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        var view = await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityTemplateID == 7010001 && b.CollectionInfo != null));
        var node = view.Bases.Where(b => b.EntityTemplateID == 7010001 && b.CollectionInfo != null)
            .OrderBy(b => Distance(b.MovementInfo.Pos, 245.93f, 220.27f)).First();
        Assert.Equal(2, node.EntityType);
        Assert.True(node.BornTimeMs > 0);
        Assert.NotEmpty(node.CollectionInfo.CollectCd);
        Assert.NotEmpty(node.CollectionInfo.CollectCountByPlayer);
        var points = _server.Service<WorldData>().Map(Scene)!.GatheringPoints;
        Assert.Contains(points, p => p.Id == node.CollectionInfo.PointConfigId && p.GroupId == 1371 && p.RefreshId == 10522201);
        await Gm(account, FormattableString.Invariant($"!jump {node.MovementInfo.Pos.X:0.00} {node.MovementInfo.Pos.Z:0.00}"));
        await Next(bot, GameAoiSink.MovementChange, MovementChangeNtf.Parser, m => m.Uid == (long)bot.Self!.Uuid);
        return node;
    }

    private static float Distance(Vec3 pos, float x, float z) => MathF.Sqrt((pos.X - x) * (pos.X - x) + (pos.Z - z) * (pos.Z - z));

    private static NpcInteractionReq Request(EntityBase node, int interaction = 70100001) => new()
    {
        NpcUid = (long)node.Uuid, InteractionId = interaction, SceneConfigId = Scene, Pos = node.MovementInfo.Pos,
    };

    private IReadOnlyList<string> GatherSources()
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(_server.DataDir, "ro3emu.db")};Mode=ReadOnly;Pooling=False");
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT source_key FROM operation_receipt WHERE command_kind='gather-yield' ORDER BY commit_seq";
        using var rows = query.ExecuteReader();
        var sources = new List<string>();
        while (rows.Read()) sources.Add(rows.GetString(0));
        return sources;
    }

    private static async Task LearnMiner(BotClient bot)
    {
        await bot.SendGameAsync(LifeJobService.SwitchLife,
            new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
        Assert.Equal(0, (await Next(bot, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);
    }

    private async Task Cast(BotClient bot, EntityBase node, int advanceSeconds = 5)
    {
        await bot.SendGameAsync(GatheringService.Interaction, Request(node));
        Assert.Equal(0, (await Next(bot, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        await Next(bot, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Collection);
        await _server.AdvanceAsync(TimeSpan.FromSeconds(advanceSeconds));
        await Next(bot, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Idle && !n.Interrupt);
    }

    [ClientDataFact]
    public async Task SuspendedGatherRetainsQuotaAndNodeUntilCommitAndDoesNotReserveAnotherRole()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var node = await EnterMinerField(alice, "alice");
        await LearnMiner(alice);
        var accounts = _server.Service<AccountStore>();
        var loop = _server.Loop;
        var owner = _server.Session("alice");
        var other = _server.Session("bob");
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gmDeferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owners = _server.Service<Ro3Emu.Server.Game.Ownership.OwnerRegistry>();
        byte[] before = Google.Protobuf.MessageExtensions.ToByteArray(owner.LifeJob!);
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/gather-yield") return Task.CompletedTask;
            staged.TrySetResult(); return release.Task;
        };
        owners.DeferredForTest = origin => { if (origin == "GM") gmDeferred.TrySetResult(); };
        Task? gm = null;
        await alice.SendGameAsync(GatheringService.Interaction, Request(node));
        Assert.Equal(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Collection);
        try
        {
            await _server.AdvanceAsync(TimeSpan.FromSeconds(5));
            await staged.Task.WaitAsync(Wait);
            Assert.Empty(GatherSources());
            Assert.Equal(before, Google.Protobuf.MessageExtensions.ToByteArray(owner.LifeJob!));
            Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(GatheringService.StateChange)
                && InteractChangeNtf.Parser.ParseFrom(e.Body) is { Interrupt: false, DataInfo.EType: InteractInfo.Types.EState.Idle });
            gm = Gm("alice", "!speed 701");
            await gmDeferred.Task.WaitAsync(Wait);
            await loop.InvokeAsync(() => _server.Service<DungeonService>().SetGuideAsync(other,
                new MultiDungeonSetJobPositionGuideFlagReq { GuideFlag = true })).WaitAsync(Wait);
            Assert.True(other.Dungeons!.FirstSetted);
            Assert.False(gm.IsCompleted);
        }
        finally
        {
            release.TrySetResult(); accounts.BeforeOwnedCommitForTest = null; owners.DeferredForTest = null;
        }
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Idle && !n.Interrupt);
        await gm!.WaitAsync(Wait);
        Assert.Single(GatherSources());
        Assert.Equal(1, owner.LifeJob!.GeneralCollectionTimesByMap[Scene].Times[node.CollectionInfo.PointConfigId]);
        var items = _server.Service<ItemStore>().Load(owner.Role!.Id);
        Assert.True(items.Where(i => i.Entry is 1291010 or 1291020 or 1291021).Sum(i => i.Stack) > 0);
    }

    [ClientDataFact]
    public async Task LostAcknowledgementDoesNotRemintButRefreshedGenerationHasNewSource()
    {
        await using var alice = await InWorld("Alice");
        var node = await EnterMinerField(alice, "alice");
        await LearnMiner(alice);
        _server.Service<AccountStore>().FailNextEconomyAcknowledgementForTest();
        await Cast(alice, node);
        var first = GatherSources();
        Assert.Single(first);
        Assert.StartsWith("gather/", first[0]);
        var initial = await OnLoop("alice", s =>
            (Materials: s.Items.Where(i => i.Entry is 1291010 or 1291020 or 1291021).Sum(i => i.Stack),
             Count: s.LifeJob!.GeneralCollectionTimesByMap[Scene].Times[node.CollectionInfo.PointConfigId]));
        Assert.Equal(1, initial.Count);
        await alice.SendGameAsync(GatheringService.Interaction, Request(node));
        Assert.NotEqual(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        Assert.Equal(first, GatherSources());

        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(node.Uuid));
        await _server.AdvanceAsync(TimeSpan.FromSeconds(3));
        var refresh = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.CollectionInfo?.PointConfigId == node.CollectionInfo.PointConfigId && b.Uuid != node.Uuid));
        var next = refresh.Bases.Single(b => b.CollectionInfo?.PointConfigId == node.CollectionInfo.PointConfigId);
        await Cast(alice, next);
        var sources = GatherSources();
        Assert.Equal(2, sources.Count);
        Assert.NotEqual(sources[0], sources[1]);
        var after = await OnLoop("alice", s =>
            (Materials: s.Items.Where(i => i.Entry is 1291010 or 1291020 or 1291021).Sum(i => i.Stack),
             Count: s.LifeJob!.GeneralCollectionTimesByMap[Scene].Times[node.CollectionInfo.PointConfigId]));
        Assert.True(after.Materials > initial.Materials);
        Assert.Equal(2, after.Count);
    }

    [ClientDataFact]
    public async Task PrecommitFailureRetriesFrozenInteractionOnce()
    {
        await using var alice = await InWorld("Alice");
        var node = await EnterMinerField(alice, "alice");
        await LearnMiner(alice);
        var accounts = _server.Service<AccountStore>();
        int attempts = 0;
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label.Contains("gather-yield", StringComparison.Ordinal) && ++attempts == 1)
                throw new IOException("test gather precommit failure");
            return Task.CompletedTask;
        };
        try { await Cast(alice, node, advanceSeconds: 7); }
        finally { accounts.BeforeOwnedCommitForTest = null; }
        Assert.Equal(2, attempts);
        Assert.Single(GatherSources());
        Assert.Equal(1, await OnLoop("alice", s =>
            s.LifeJob!.GeneralCollectionTimesByMap[Scene].Times[node.CollectionInfo.PointConfigId]));
    }

    [ClientDataFact]
    public async Task PostcommitNoticeFailureDoesNotRepeatQuotaOrSource()
    {
        await using var alice = await InWorld("Alice");
        var node = await EnterMinerField(alice, "alice");
        await LearnMiner(alice);
        var character = _server.Service<CharacterService>();
        int faults = 0;
        Action<GameSession> fail = _ => { faults++; throw new IOException("test gather postcommit notice failure"); };
        using (character.StateChanged.Subscribe(fail))
            await Cast(alice, node, advanceSeconds: 7);
        Assert.Equal(1, faults);
        Assert.Single(GatherSources());
        Assert.Equal(1, await OnLoop("alice", s =>
            s.LifeJob!.GeneralCollectionTimesByMap[Scene].Times[node.CollectionInfo.PointConfigId]));
        await alice.SendGameAsync(GatheringService.Interaction, Request(node));
        Assert.NotEqual(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        Assert.Single(GatherSources());
    }

    [ClientDataFact]
    public async Task CommittedApplyFailureReconcilesResourceAndReloadsOnceAfterRestart()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var node = await EnterMinerField(alice, "alice");
        var bobNode = await EnterMinerField(bob, "bob"); // keep the resource runtime alive after Alice detaches
        Assert.Equal(node.Uuid, bobNode.Uuid);
        await LearnMiner(alice);
        await LearnMiner(bob);
        long aliceRole = await OnLoop("alice", s => s.Role!.Id);
        var accounts = _server.Service<AccountStore>();
        var failedApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accounts.BeforeOwnedApplyForTest = label =>
        {
            if (label.Contains("gather-yield", StringComparison.Ordinal))
            {
                failedApply.TrySetResult();
                throw new IOException("gather durable commit before live apply");
            }
            return Task.CompletedTask;
        };
        try
        {
            await alice.SendGameAsync(GatheringService.Interaction, Request(node));
            Assert.Equal(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
            await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
                n => n.DataInfo.EType == InteractInfo.Types.EState.Collection);
            await _server.AdvanceAsync(TimeSpan.FromSeconds(5));
            await failedApply.Task.WaitAsync(Wait);
        }
        finally { accounts.BeforeOwnedApplyForTest = null; }
        Assert.Single(GatherSources());
        await _server.AdvanceAsync(TimeSpan.FromSeconds(6));
        await Next(bob, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(node.Uuid));
        // C/D quarantine deliberately requires process recovery; no stale snapshot is allowed to reconnect/save. The
        // server aborts Alice's connection, so the owner is read by role: her session may already be gone.
        var loop = _server.Loop;
        Assert.Equal(Ro3Emu.Server.Game.Ownership.RoleOwnerState.RecoveryRequired,
            await loop.InvokeAsync(() => Task.FromResult(loop.Owners!.Cached(aliceRole)!.State)));
        await alice.DisposeAsync();
        await bob.DisposeAsync();
        await _server.RestartAsync();
        await using var recovered = await InWorld("Alice", create: false);
        Assert.Equal(1, await OnLoop("alice", s =>
            s.LifeJob!.GeneralCollectionTimesByMap[Scene].Times[node.CollectionInfo.PointConfigId]));
        Assert.Single(GatherSources());
        await using var nextBob = await InWorld("Bob", create: false);
        var next = await EnterMinerField(nextBob, "bob");
        Assert.Equal(0, next.CollectionInfo.TotalCollectCount);
        await Cast(nextBob, next);
        Assert.Equal(2, GatherSources().Distinct().Count());
    }

    [ClientDataFact]
    public async Task ResolvedGatherVirtualExpUsesProgressAndOneSourceReceipt()
    {
        await using var alice = await InWorld("Alice");
        var node = await EnterMinerField(alice, "alice");
        await LearnMiner(alice);
        var loop = _server.Loop;
        var life = _server.Service<LifeJobService>();
        var result = await loop.InvokeAsync(async () =>
        {
            var session = _server.Session("alice");
            using var held = await CharacterService.Lock(session);
            var (error, original) = life.ResolveGather(session, 100100, 0, Scene,
                node.CollectionInfo.PointConfigId);
            Assert.Equal(0, error);
            Assert.NotNull(original);
            var resolved = new LifeJobService.GatherResolution
            {
                RoleId = original.RoleId, BeforeLife = original.BeforeLife,
                BeforeRestrict = original.BeforeRestrict, BeforeRuntime = original.BeforeRuntime,
                BeforeCurrencyVigor = original.BeforeCurrencyVigor, CurrencyVigorCost = original.CurrencyVigorCost,
                Life = original.Life, Restrict = original.Restrict, Runtime = original.Runtime,
                Grants = [.. original.Grants, new AwardGrant(100, 1), new AwardGrant(101, 1)],
            };
            long baseBefore = session.Progress!.Exp;
            Assert.Equal(1, session.Progress.JobLevel);
            Assert.Equal(0, session.Progress.JobExp);
            var settlement = new RewardSettlement("gather/test/" + Guid.NewGuid().ToString("N"), "gather-yield");
            int code = await life.CompleteGatherAsync(session, resolved, settlement, () => { });
            return (code, settlement.Committed, baseBefore,
                session.Progress!.Exp, session.Progress.JobExp,
                session.Progress.JobLevel,
                session.Items.Any(i => i.Entry is 100 or 101));
        });
        Assert.Equal(0, result.code);
        Assert.True(result.Committed);
        Assert.Equal(result.baseBefore + 1, result.Exp);
        Assert.Equal(2, result.JobLevel); // JobLvConfig[101] consumes the one awarded job EXP
        Assert.Equal(0, result.JobExp);
        Assert.False(result.Item7);
        Assert.Single(GatherSources());
    }

    [ClientDataFact]
    public async Task RefreshNodeRequiresActiveMinerAndPaysOnlyAfterCastThenRespawns()
    {
        await using var alice = await InWorld("Alice");
        var node = await EnterMinerField(alice, "alice");
        var req = Request(node);

        await alice.SendGameAsync(GatheringService.Interaction, req);
        Assert.NotEqual(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);

        await alice.SendGameAsync(LifeJobService.SwitchLife, new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
        Assert.Equal(0, (await Next(alice, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);
        await alice.SendGameAsync(GatheringService.Interaction, req);
        var start = await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser);
        Assert.Equal(0, start.Result);
        Assert.Equal(req, start.Req);
        var state = await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Collection);
        Assert.Equal((long)alice.Self!.Uuid, state.Uid);
        Assert.Equal((long)node.Uuid, state.DataInfo.ActiveObjId);
        Assert.Equal(3100101, state.DataInfo.CollectionInfo.PlayInfo.PlayConfigId);
        Assert.True(state.DataInfo.CollectionInfo.PlayInfo.PlayEndTimeStamp > _clock.GetUtcNow().ToUnixTimeMilliseconds());
        int itemsBefore = Count(alice, CharacterService.ItemsSync);
        int lifeBefore = Count(alice, LifeJobService.LifeSync);
        int vigorBefore = Count(alice, LifeJobService.RestrictSync);

        await alice.SendGameAsync(GatheringService.Interaction, req);
        Assert.NotEqual(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        await _server.AdvanceAsync(TimeSpan.FromSeconds(5));
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Idle && !n.Interrupt);
        Assert.True(Count(alice, CharacterService.ItemsSync) > itemsBefore);
        Assert.True(Count(alice, LifeJobService.LifeSync) > lifeBefore);
        Assert.True(Count(alice, LifeJobService.RestrictSync) > vigorBefore);
        var (material, pointCount) = await OnLoop("alice", s =>
            (s.Items.Where(i => i.Entry is 1291010 or 1291020 or 1291021).Sum(i => i.Stack),
             s.LifeJob!.GeneralCollectionTimesByMap[Scene].Times[node.CollectionInfo.PointConfigId]));
        Assert.True(material >= 4);
        Assert.Equal(1, pointCount);
        var removed = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(node.Uuid));
        Assert.Contains(node.Uuid, removed.LeaveAoi);

        await alice.SendGameAsync(GatheringService.Interaction, req);
        Assert.NotEqual(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        int after = Count(alice, CharacterService.ItemsSync);
        await _server.AdvanceAsync(TimeSpan.FromMilliseconds(2500)); // RefreshInfo._iRefreshTime=2, interpreted as seconds (inferred).
        Assert.Equal(after, Count(alice, CharacterService.ItemsSync));
        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityTemplateID == 7010001 && b.CollectionInfo?.PointConfigId == node.CollectionInfo.PointConfigId));
    }

    [ClientDataFact]
    public async Task MovementWrongRangeAndUnselectedMiniGameCannotPay()
    {
        await using var alice = await InWorld("Alice");
        var node = await EnterMinerField(alice, "alice");
        await alice.SendGameAsync(LifeJobService.SwitchLife, new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
        Assert.Equal(0, (await Next(alice, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);

        await alice.SendGameAsync(GatheringService.MiniGame,
            new FinishLifeJobGameReq { GameId = 1, NpcGuid = (long)node.Uuid, Rating = 5 });
        Assert.NotEqual(0, (await Next(alice, GatheringService.MiniGame, FinishLifeJobGameResp.Parser)).Result);
        await alice.SendGameAsync(GatheringService.Interaction, Request(node, interaction: 8));
        Assert.NotEqual(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        var wrongScene = Request(node);
        wrongScene.SceneConfigId = 10111;
        await alice.SendGameAsync(GatheringService.Interaction, wrongScene);
        Assert.NotEqual(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);

        await alice.SendGameAsync(GatheringService.Interaction, Request(node));
        Assert.Equal(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Collection);
        int before = Count(alice, CharacterService.ItemsSync);
        await alice.SendGameAsync(GatheringService.MiniGame,
            new FinishLifeJobGameReq { GameId = 1, NpcGuid = (long)node.Uuid, Rating = 5 });
        Assert.NotEqual(0, (await Next(alice, GatheringService.MiniGame, FinishLifeJobGameResp.Parser)).Result);
        Assert.Equal(before, Count(alice, CharacterService.ItemsSync));
        var pos = node.MovementInfo.Pos;
        await alice.WalkAsync(pos.X + 0.3f, pos.Y, pos.Z, stop: true);
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Idle && n.Interrupt);
        await _server.AdvanceAsync(TimeSpan.FromMilliseconds(4700));
        Assert.Equal(before, Count(alice, CharacterService.ItemsSync));

        await Gm("alice", "!jump 200 200");
        await alice.SendGameAsync(GatheringService.Interaction, Request(node));
        Assert.NotEqual(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        await Gm("alice", "!warp 10111");
        var change = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = change.SceneEntry });
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        await alice.SendGameAsync(GatheringService.Interaction, Request(node));
        Assert.NotEqual(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task TwoMinersContestingOneUseNodeReceiveOnlyOneReward()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var node = await EnterMinerField(alice, "alice");
        var bobNode = await EnterMinerField(bob, "bob");
        Assert.Equal(node.Uuid, bobNode.Uuid);
        foreach (var (client, slot) in new[] { (alice, 1), (bob, 1) })
        {
            await client.SendGameAsync(LifeJobService.SwitchLife,
                new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = slot });
            Assert.Equal(0, (await Next(client, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);
        }

        await alice.SendGameAsync(GatheringService.Interaction, Request(node));
        await bob.SendGameAsync(GatheringService.Interaction, Request(node));
        Assert.Equal(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        Assert.Equal(0, (await Next(bob, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Collection);
        await Next(bob, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Collection);
        int aliceItems = Count(alice, CharacterService.ItemsSync);
        int bobItems = Count(bob, CharacterService.ItemsSync);
        await _server.AdvanceAsync(TimeSpan.FromSeconds(5));
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        await Next(bob, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        int rewards = (Count(alice, CharacterService.ItemsSync) > aliceItems ? 1 : 0)
            + (Count(bob, CharacterService.ItemsSync) > bobItems ? 1 : 0);
        Assert.Equal(1, rewards);
        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(node.Uuid));
        await Next(bob, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(node.Uuid));
    }

    [ClientDataFact]
    public async Task LeavingSceneDuringCastCannotGrantReward()
    {
        await using var alice = await InWorld("Alice");
        var node = await EnterMinerField(alice, "alice");
        await alice.SendGameAsync(LifeJobService.SwitchLife,
            new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
        Assert.Equal(0, (await Next(alice, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);
        await alice.SendGameAsync(GatheringService.Interaction, Request(node));
        Assert.Equal(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Collection);
        int items = Count(alice, CharacterService.ItemsSync);
        await Gm("alice", "!warp 10111");
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Idle && n.Interrupt);
        var change = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = change.SceneEntry });
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        await _server.AdvanceAsync(TimeSpan.FromMilliseconds(4700));
        Assert.Equal(items, Count(alice, CharacterService.ItemsSync));
    }

    [ClientDataFact]
    public async Task DisconnectDuringCastCannotPersistReward()
    {
        var alice = await InWorld("Alice");
        var node = await EnterMinerField(alice, "alice");
        await alice.SendGameAsync(LifeJobService.SwitchLife,
            new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
        Assert.Equal(0, (await Next(alice, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);
        await alice.SendGameAsync(GatheringService.Interaction, Request(node));
        Assert.Equal(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Collection);
        await alice.DisposeAsync();
        await _server.AdvanceAsync(TimeSpan.FromMilliseconds(4700));

        await using var again = await InWorld("Alice", create: false);
        var (material, counted) = await OnLoop("alice", s =>
            (s.Items.Where(i => i.Entry is 1291010 or 1291020 or 1291021).Sum(i => i.Stack),
             s.LifeJob!.GeneralCollectionTimesByMap.TryGetValue(Scene, out var map)
                 && map.Times.ContainsKey(node.CollectionInfo.PointConfigId)));
        Assert.Equal(0, material);
        Assert.False(counted);
    }

    [ClientDataFact]
    public async Task RefusedSkillStillInterruptsGatherCastWithoutChargingVigor()
    {
        await using var alice = await InWorld("Alice");
        var node = await EnterMinerField(alice, "alice");
        await alice.SendGameAsync(LifeJobService.SwitchLife,
            new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
        Assert.Equal(0, (await Next(alice, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);
        await alice.SendGameAsync(GatheringService.Interaction, Request(node));
        Assert.Equal(0, (await Next(alice, GatheringService.Interaction, NpcInteractionRsp.Parser)).Result);
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Collection);
        int items = Count(alice, CharacterService.ItemsSync);
        int vigor = Count(alice, LifeJobService.RestrictSync);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 99999999 });
        await Next(alice, GatheringService.StateChange, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Idle && n.Interrupt);
        Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await _server.AdvanceAsync(TimeSpan.FromMilliseconds(4700));
        Assert.Equal(items, Count(alice, CharacterService.ItemsSync));
        Assert.Equal(vigor, Count(alice, LifeJobService.RestrictSync));
    }
}
