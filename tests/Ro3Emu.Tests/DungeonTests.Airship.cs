using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Main quest 10103210 'Journey Through the Skies' ('Take the airship to Morroc') played as a player plays it: its accept
/// action 3000 enters the airship 1000501 (30400), every crew bubble is a walk up to that crew member (NPCInteractConfig
/// mode 2, AutoInteractWhenInRange: the client sends 32700 once the player stands within NPCConfig _iInteractDistance),
/// the cannons are boarded and fired, the boarders and the Evil Boss fought, the captain's dialogue (the tracker's
/// [142, 1000501, 55, 9, 530003802]) ends in its option's [211, 5300038, 5300039], and the delivery's client action
/// [141, 10514, 1040401, ...] (JumpMapArea, 30131) takes the player to the Sograt Desert, where 10103220 goes on.
/// </summary>
public sealed partial class DungeonTests
{
    private const long Airship = 1000501, AirshipTask = 10103210, CrashTask = 10103220;

    [ClientDataFact]
    public async Task TheAirshipToMorrocIsPlayedFromTheQuestToTheDesert()
    {
        Service<IOptions<ServerOptions>>().Value.ScriptedScenePacePercent = 10;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, $"!quest accept {AirshipTask}");
        // Accept action [3000, 1000501, 1, 10103210]: LC_InteractActions.EnterDungeon sends 30400 for the scene.
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = Airship }));
        long instance = await Load(alice, Airship);
        var map = Service<WorldData>().Map(Airship)!;

        // 1 'Have a Chitchat with them' (goal [1], no counter): the captain's bubble ends it; Bobo's, while the crew is
        // still there, is answered too.
        await Stage(alice, 1, quest: 1);
        await ActorsIn(alice, 5);
        await WalkUpTo(alice, 5300032);
        await WalkUpTo(alice, 5300031);

        // 2 the cannons: board one, sink each ship (its HP set to 1 before the sinking shot, as Kill does).
        await Stage(alice, 2, quest: 2);
        var cannon = (await ActorsIn(alice, 3, a => a.NpcId == 5300022))[0];
        var ships = await WaveIn(instance, 3);
        await WalkTo(alice, cannon.X - 1, cannon.Y, cannon.Z);
        Assert.Equal(0, await Interact(alice, cannon, 5300006));
        long role = Session(alice).Role!.Id;
        foreach (var ship in ships)
        {
            await OnLoop(() =>
            {
                Session(alice).SkillCds.Clear(); Session(alice).SkillModeCds.Clear(); Session(alice).SkillCast = null;
                ship.Hp = 1;
                return Task.CompletedTask;
            });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 5353001, OpUnitId = role, TargetsId = { ship.Uid }, TargetPos = ship.Pos });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            for (int i = 0; i < 100 && !ship.Dead; i++) await Task.Delay(20);
            Assert.True(ship.Dead, $"ship {ship.Template.NpcId} still has {ship.Hp} HP");
        }

        // 3 the boarders in three groups, 4 the Evil Boss. The end of stage 2 puts the gunner down; the last shot has
        // long ended for a player who walks over to the first boarder.
        await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser, n => n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        await OnLoop(() => { Session(alice).SkillCast = null; Session(alice).SkillCds.Clear(); return Task.CompletedTask; });
        for (int group = 0; group < 3; group++)
        {
            await Stage(alice, 3 + group, quest: 3);
            foreach (var monster in await WaveIn(instance, 3)) await Kill(alice, monster);
        }
        await Stage(alice, 6, quest: 4);
        foreach (var boss in await WaveIn(instance, 1)) await Kill(alice, boss);

        // 5 'Take a look around' (goal [1], no counter): the captain stands nearest to where the Evil Boss falls, and his
        // bubble ends it; Bobo, Wule, Boya and the chief 36 m away need not be found.
        await Stage(alice, 7, quest: 5);
        await ActorsIn(alice, 5);
        await WalkUpTo(alice, 5300062);

        // 6 'Talk to the captain': the tracker walks to point 55 and opens dialogue 530003802 (action 9 NPCDialogue); its
        // last option runs [211, 5300038, 5300039], which sends 32700 for interaction 5300039 on the captain.
        await Stage(alice, 8, quest: 6);
        var captain = Assert.Single(await ActorsIn(alice, 1, a => a.NpcId == 5300038));
        var point55 = map.Points[55];
        await WalkTo(alice, point55.X - 1, point55.Y, point55.Z);
        using var done = alice.Inbox.Expect(e => e.IsGame(DungeonService.Result));
        Assert.Equal(0, await Interact(alice, captain, 5300039));
        Assert.Equal((DungeonRes)1, DungeonResNotify.Parser.ParseFrom((await done.WaitAsync(Wait)).Body).Res);

        // The clear counts for 101018 [1000501, 1]; the quest delivers itself (_kDeliverTask 1) and its client action
        // [141, 10514, 1040401, 2, 10103210, 0] sends 30131 to the Sograt Desert.
        for (int i = 0; i < 200 && !TaskService.IsDelivered(Session(alice), AirshipTask); i++) await Task.Delay(20);
        Assert.True(TaskService.IsDelivered(Session(alice), AirshipTask), $"{AirshipTask} is not delivered");
        await alice.SendGameAsync(SceneService.CustomJumpMap, new ClientCustomJumpMapReq
        { SceneId = 10514, PointId = 1040401, CustomType = (EClientCustomJumpMapType)2, TypeParam1 = AirshipTask });
        Assert.Equal(0, (await Next(alice, SceneService.CustomJumpMap, ClientCustomJumpMapResp.Parser)).Result);
        var arrived = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry == 10514);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = arrived.SceneEntry });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser, m => m.SceneId == 10514)).Result);
        // Five seconds on, the return after the clear finds the player gone and leaves the desert alone.
        await Task.Delay(TimeSpan.FromSeconds(DungeonService.PlaneExitSeconds + 1));
        Assert.Equal(10514, Session(alice).SceneEntry);
        Assert.Null(Session(alice).DungeonAttempt);
        // 'Crashed Airship' follows (its 101002 level target is met at once at level 99).
        Assert.True(Session(alice).Tasks!.Infos.TryGetValue(CrashTask, out var crash)
            && crash.Status is TaskState.Running or TaskState.Finish or TaskState.Delivery, $"{CrashTask} was not offered");
    }

    /// <summary>
    /// The player walks up to a scene actor until it is within its NPCConfig _iInteractDistance; the client's 0.2 s range
    /// check then sends its auto interaction (mode 2), its first _iInteractID, with the player's position.
    /// </summary>
    private async Task WalkUpTo(BotClient bot, int npc)
    {
        var actor = Assert.Single(await ActorsIn(bot, 1, a => a.NpcId == npc));
        int interaction = (int)Service<DungeonCatalog>().Interactions(npc)[0];
        await WalkTo(bot, actor.X - 1.5f, actor.Y, actor.Z);
        await bot.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        {
            NpcUid = actor.Uid, InteractionId = interaction, SceneConfigId = actor.SceneEntry,
            Pos = Session(bot).Position.Clone(), Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        var reply = await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser,
            r => r.Req.NpcUid == actor.Uid && r.Req.InteractionId == interaction);
        Assert.True(reply.Result == 0, $"{npc}'s interaction {interaction} refused {reply.Result}");
    }
}
