using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Card effects around a hit on a player: socketed through 31406, read in the 31101 the hit sends.</summary>
public sealed partial class CardRulesTests
{
    /// <summary>A role with the card in its part's grid, through the real equip and socket messages. Off-hand cards need a career
    /// whose form has an off hand. A new role already carries the armor and the dagger.</summary>
    private async Task<(BotClient Bot, GameSession Session)> Holder(string name, int card, int grid)
    {
        var bot = await InWorld(name);
        var session = Session(name);
        string account = name.ToLowerInvariant();
        await Gm(account, "!lvl 80");
        if (grid == 2) await Gm(account, "!job 1100");
        await Gm(account, $"!item {card} 1");
        await Eventually(() => session.Items.Any(i => i.Entry == card), "the card");
        int gear = grid switch { 1 => Dagger, 2 => OffHand, 3 => Armor, 4 => Pauldrons, 5 => Sandals, 6 => Ring, _ => 0 };
        if (gear != 0)
        {
            if (!session.Items.Any(i => i.Entry == gear)) await Gm(account, $"!item {gear} 1");
            await Eventually(() => session.Items.Any(i => i.Entry == gear), "the gear");
            await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)grid, ItemGUID = session.Items.First(i => i.Entry == gear).Guid });
            Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        }
        Assert.Equal(0, await Insert(bot, grid, 1, session.Items.Single(i => i.Entry == card).Guid));
        return (bot, session);
    }

    /// <summary>Monsters only of this test's, one of each element, 1 m from the player, who has no recovery tick between two hits.</summary>
    private async Task<MonsterUnit[]> Strikers(GameSession session, params int[] elements)
    {
        var monsters = Service<MonsterService>();
        var units = new MonsterUnit[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            var template = monsters.Template(5000002)! with { RoamDistance = 0, SurroundDistance = 0, Element = elements[i], Atk = 400, Matk = 400 };
            units[i] = await Service<GameLoop>().InvokeAsync(async () => (await monsters.SpawnSingleAsync(session.SceneEntry, 5000002,
                new MapPoint(session.Position.X, session.Position.Y, session.Position.Z, 0), session.InstanceId, customTemplate: template))!);
        }
        await Service<GameLoop>().InvokeAsync(() =>
        {
            foreach (var other in Service<World>().Map(session.WorldScene).Units.OfType<MonsterUnit>().Where(m => !units.Contains(m)))
            { other.X += 200; other.Z += 200; }
            foreach (var unit in units)
            {
                unit.StaysOutOfBattle = true;
                unit.X = session.Position.X + 1;
                unit.Z = session.Position.Z;
            }
            session.NoRegenUntilMs = long.MaxValue;
            Service<CombatService>().Rolls = new FixedRolls(crit: false);
            return Task.CompletedTask;
        });
        return units;
    }

    private async Task<DamageInfo> HitOn(BotClient bot, GameSession session, long source)
    {
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == source && h.Dsts.Any(d => d.DstID == session.Role!.Id));
        return hit.Dsts.Single(d => d.DstID == session.Role!.Id);
    }

    /// <summary>A monster's real swing on the player, at full HP; what its 31101 says of the player.</summary>
    private async Task<DamageInfo> SwungAt(BotClient bot, GameSession session, MonsterUnit monster)
    {
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            await Service<CharacterService>().SetHpSpAsync(session, long.MaxValue, null);
            await Service<MonsterService>().Attack!(monster, session);
        });
        return await HitOn(bot, session, monster.Uid);
    }

    /// <summary>A monster's skill row landing on the player, at full HP.</summary>
    private async Task<DamageInfo> SkillAt(BotClient bot, GameSession session, MonsterUnit monster, int row, int maxHpBasisPoints = 0)
    {
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            await Service<CharacterService>().SetHpSpAsync(session, long.MaxValue, null);
            await Service<CombatService>().MonsterSkillHitAsync(monster, Service<GameData>().Row("SkillConfig", row)!.Value, [session], monster.Uid, maxHpBasisPoints: maxHpBasisPoints);
        });
        return await HitOn(bot, session, monster.Uid);
    }

    /// <summary>Two players in one room, hostile both ways, 2 m apart, at full HP with no recovery tick and the rolls fixed.</summary>
    private async Task Duel(BotClient alice, GameSession a, BotClient bob, GameSession b)
    {
        var instances = Service<InstanceService>();
        var room = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(instances.Create(a.SceneEntry, [a, b])));
        foreach (var (bot, session) in new[] { (alice, a), (bob, b) })
        {
            Assert.True(await Service<GameLoop>().InvokeAsync(() => instances.EnterAsync(room, session, reason: 3)));
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        }
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            Service<CombatService>().SetEventPvPHandlers((source, target) => source != target, (_, _) => Task.CompletedTask);
            Service<CombatService>().Rolls = new FixedRolls(crit: false);
            await Service<SceneService>().TeleportInMapAsync(b, new MapPoint(a.Position.X + 2, a.Position.Y, a.Position.Z, b.Face));
            foreach (var p in new[] { a, b })
            {
                p.NoRegenUntilMs = long.MaxValue;
                await Service<CharacterService>().SetHpSpAsync(p, long.MaxValue, long.MaxValue);
            }
        });
    }

    /// <summary>A real cast at a player on the wire, its cooldowns cleared; the hit it lands on them.</summary>
    private async Task<DamageInfo> CastAt(BotClient bot, GameSession caster, int skill, GameSession target, bool refillCaster = true)
    {
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            caster.SkillCds.Clear(); caster.SkillModeCds.Clear(); caster.SwingLockUntilMs = 0; caster.SkillCast = null;
            await Service<CharacterService>().SetHpSpAsync(target, long.MaxValue, null);
            if (refillCaster) await Service<CharacterService>().SetHpSpAsync(caster, long.MaxValue, long.MaxValue);
        });
        for (int attempt = 0; ; attempt++)
        {
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Role!.Id });
            var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
            if (accepted.Result == 0)
            {
                var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == target.Role!.Id));
                await OnLoop(() => { caster.Attack = null; return 0; });
                return hit.Dsts.Single(d => d.DstID == target.Role!.Id);
            }
            Assert.True(attempt < 40, $"skill {skill} was refused {accepted.Result}");
            await Task.Delay(100);
        }
    }

    private const int ShadowGoldenThiefBug = 1450200, GoldenThiefBug = 1460200, FireBolt = 1200401;

    // Card: Golden Thief Bug T2: 2051103 "When hit by a magic attack, has a 28% Chance to nullify its damage. Internal Cooldown: 5
    // seconds." The 31101 of the nullified hit says 0; inside the 5 s the certain roll nullifies nothing.
    [ClientDataFact]
    public async Task GoldenThiefBugNullifiesAMagicHitAtItsStarsChanceAndThenWaitsItsCooldown()
    {
        var (alice, session) = await Holder("Alice", GoldenThiefBug, 2);
        await using (alice)
        {
            await AtStar(session, GoldenThiefBug, 2);
            var striker = (await Strikers(session, 1))[0];
            var effects = Service<CardEffects>();
            effects.Roll = _ => 2800;
            Assert.True((await SkillAt(alice, session, striker, FireBolt)).Damage > 0);

            effects.Roll = _ => 2799;
            var nullified = await SkillAt(alice, session, striker, FireBolt);
            var since = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(0, nullified.Damage);
            Assert.Equal(await OnLoop(() => Service<CharacterService>().Stats(session)[101]), await OnLoop(() => Service<CharacterService>().Stats(session)[2000]));

            effects.Roll = _ => 0;
            var inCooldown = await SkillAt(alice, session, striker, FireBolt);
            if (since.ElapsedMilliseconds < 4800) Assert.True(inCooldown.Damage > 0);

            await Task.Delay(Math.Max(0, 5100 - (int)since.ElapsedMilliseconds));
            Assert.Equal(0, (await SkillAt(alice, session, striker, FireBolt)).Damage);
        }
    }

    // Shadow Card: Golden Thief Bug T3: its stat lines rise, 2051001 stays 10% and 5 s.
    [ClientDataFact]
    public async Task ShadowGoldenThiefBugKeepsItsChanceAtEveryStar()
    {
        var (alice, session) = await Holder("Alice", ShadowGoldenThiefBug, 2);
        await using (alice)
        {
            await AtStar(session, ShadowGoldenThiefBug, 3);
            var striker = (await Strikers(session, 1))[0];
            var effects = Service<CardEffects>();
            effects.Roll = _ => 1000;
            Assert.True((await SkillAt(alice, session, striker, FireBolt)).Damage > 0);
            effects.Roll = _ => 999;
            Assert.Equal(0, (await SkillAt(alice, session, striker, FireBolt)).Damage);
        }
    }

    // "a magic attack": a swing of _iDmgType 1 lands whatever the roll. "(Boss instant-kill skills can bypass this card's effect.)":
    // a raid mechanic's share of maximum HP lands too, and spends no cooldown. Nor does a tick count as an attack.
    [ClientDataFact]
    public async Task GoldenThiefBugLeavesAPhysicalHitAMechanicAndATickAlone()
    {
        var (alice, session) = await Holder("Alice", GoldenThiefBug, 2);
        await using (alice)
        {
            var striker = (await Strikers(session, 1))[0];
            var effects = Service<CardEffects>();
            effects.Roll = _ => 0;
            Assert.True((await SwungAt(alice, session, striker)).Damage > 0);
            Assert.True((await SkillAt(alice, session, striker, FireBolt, maxHpBasisPoints: 1000)).Damage > 0);
            var magic = Service<GameData>().Row("SkillConfig", FireBolt)!.Value;
            Assert.Equal(1000, await OnLoop(() => effects.AdjustDamageTaken(session, magic, directHit: false, 1000)));
            Assert.Equal(0, (await SkillAt(alice, session, striker, FireBolt)).Damage);
        }
    }

    // "When hit by a magic attack" holds for a player's Fire Bolt on the wire too.
    [ClientDataFact]
    public async Task GoldenThiefBugNullifiesAPlayersMagicHit()
    {
        var (bob, b) = await Holder("Bob", GoldenThiefBug, 2);
        await using (bob)
        {
            await using var alice = await InWorld("Alice");
            var a = Session("Alice");
            await Gm("alice", "!lvl 80");
            await Gm("alice", "!job 2100");
            await Learn(a, 12004);
            await Duel(alice, a, bob, b);
            Service<CardEffects>().Roll = _ => 0;
            Assert.Equal(0, (await CastAt(alice, a, FireBolt, b)).Damage);
        }
    }

    private const int Ladybug = 1430702, Silence = 902301;

    // Card: Ladybug T2: 2026001 "Damage taken while Silenced -3%." [300]; its star raises only HP, and the damage only has to be
    // read against a hit at the same star. A real swing and a Fire Bolt, silenced and not.
    [ClientDataFact]
    public async Task LadybugTakesThreePercentOffWhileSilencedOnly()
    {
        var (alice, session) = await Holder("Alice", Ladybug, 8);
        await using (alice)
        {
            await AtStar(session, Ladybug, 2);
            var striker = (await Strikers(session, 1))[0];
            long swing = (await SwungAt(alice, session, striker)).Damage;
            long skill = (await SkillAt(alice, session, striker, FireBolt)).Damage;
            Assert.True(swing >= 34, "too little damage to see 3% in: " + swing);

            await Service<GameLoop>().InvokeAsync(() => Service<BuffService>().AddAsync(session, Silence, 60_000, striker.Uid));
            Assert.Equal(swing - (long)Math.Floor(swing * 0.03), (await SwungAt(alice, session, striker)).Damage);
            Assert.Equal(skill - (long)Math.Floor(skill * 0.03), (await SkillAt(alice, session, striker, FireBolt)).Damage);

            await Service<GameLoop>().InvokeAsync(() => Service<BuffService>().RemoveAsync(session, session.Buffs.Single(b => b.ConfigId == Silence)));
            Assert.Equal(swing, (await SwungAt(alice, session, striker)).Damage);
        }
    }

    private const int OrcLord = 1460301, ShadowOrcLord = 1450301, Maya = 1460201, ShadowMaya = 1450201, SwordSmash = 1109901, MagnumBreak = 1100201;

    /// <summary>Alice the attacker of a career, with the skill learned, and Bob holding the card, in one duel.</summary>
    private async Task<(BotClient Alice, GameSession A, BotClient Bob, GameSession B)> ReflectDuel(int card, int grid, int star, int job, params int[] groups)
    {
        var (bob, b) = await Holder("Bob", card, grid);
        await AtStar(b, card, star);
        var alice = await InWorld("Alice");
        var a = Session("Alice");
        await Gm("alice", "!lvl 80");
        await Gm("alice", $"!job {job}");
        foreach (int group in groups) await Learn(a, group);
        await Duel(alice, a, bob, b);
        return (alice, a, bob, b);
    }

    private async Task<long> Attribute(GameSession session, int id) => await OnLoop(() => Service<CharacterService>().Stats(session)[id]);

    // Card: Orc Lord T2: 2051703 "When hit by a player's ST Physical skill, reflects 48% of the damage taken, up to 35% of the attacker's
    // Max Life. For every 1 VIT, reflects an Extra 0.1% of the damage taken, up to 60%. Internal Cooldown: 3 seconds." A second 31101 goes
    // to the attacker, as row 2051703, from Bob. Sword Smash is ST and Physical.
    [ClientDataFact]
    public async Task OrcLordReflectsAPlayersSingleTargetPhysicalSkillAtItsStarAndThenWaitsItsCooldown()
    {
        var (alice, a, bob, b) = await ReflectDuel(OrcLord, 3, 2, 1100, 11099);
        await using (alice)
        await using (bob)
        {
            const int row = 2051703;
            long vit = await Attribute(b, 3), maxHp = await Attribute(a, 101);
            var taken = await CastAt(alice, a, SwordSmash, b);
            var since = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(taken.Damage >= 100, "too little damage to read a share of: " + taken.Damage);
            var reflected = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == row && h.SrcID == b.Role!.Id);
            long share = Math.Min(4800 + 10 * vit, 6000);
            Assert.Equal(Math.Min((long)Math.Floor(taken.Damage * share / 10000d), maxHp * 3500 / 10000), Assert.Single(reflected.Dsts).Damage);
            Assert.Equal(a.Role!.Id, reflected.Dsts[0].DstID);
            await Task.Delay(100);
            Assert.Equal(maxHp - reflected.Dsts[0].Damage, await Attribute(a, 2000));

            await CastAt(alice, a, SwordSmash, b);
            await Task.Delay(200);
            if (since.ElapsedMilliseconds < 2800) Assert.Single(SplashHits(alice, row));

            await Task.Delay(Math.Max(0, 3100 - (int)since.ElapsedMilliseconds));
            await CastAt(alice, a, SwordSmash, b);
            await Eventually(() => SplashHits(alice, row).Count == 2, "the second reflect");
        }
    }

    // Shadow Card: Maya T3: 2051204 "When hit by a player's ST magic skill, reflects 50% ... up to 25% of the attacker's Max Life."
    // Fire Bolt is ST and magic. The Shadow card has no VIT line.
    [ClientDataFact]
    public async Task ShadowMayaReflectsAPlayersSingleTargetMagicSkillWithNoVitLine()
    {
        var (alice, a, bob, b) = await ReflectDuel(ShadowMaya, 2, 3, 2100, 12004);
        await using (alice)
        await using (bob)
        {
            long maxHp = await Attribute(a, 101);
            var taken = await CastAt(alice, a, FireBolt, b);
            var reflected = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2051204 && h.SrcID == b.Role!.Id);
            Assert.Equal(Math.Min(taken.Damage / 2, maxHp * 2500 / 10000), Assert.Single(reflected.Dsts).Damage);
        }
    }

    // Each kind answers its own: Orc Lord leaves a magic hit alone, Maya a Physical one.
    [ClientDataTheory]
    [InlineData(1460301, 3, 2100, 12004, 1200401)] // Orc Lord, Fire Bolt
    [InlineData(1450301, 3, 2100, 12004, 1200401)] // Shadow Orc Lord, Fire Bolt
    [InlineData(1460201, 2, 1100, 11099, 1109901)] // Maya, Sword Smash
    [InlineData(1450201, 2, 1100, 11099, 1109901)] // Shadow Maya, Sword Smash
    [InlineData(1460301, 3, 1100, 11002, 1100201)] // Orc Lord, Magnum Break: an AoE of the same kind
    public async Task AReflectAnswersOnlyASingleTargetSkillOfItsKind(int card, int grid, int job, int group, int skill)
    {
        var (alice, a, bob, b) = await ReflectDuel(card, grid, 0, job, group);
        await using (alice)
        await using (bob)
        {
            Assert.True((await CastAt(alice, a, skill, b)).Damage > 0);
            await Task.Delay(300);
            Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)), h => h.SrcID == b.Role!.Id);
        }
    }

    // "a player's ST Physical skill": a Basic Attack is no skill (inferred), and a monster's hit is no player's.
    [ClientDataFact]
    public async Task OrcLordLeavesABasicAttackAndAMonstersHitAlone()
    {
        var (alice, a, bob, b) = await ReflectDuel(OrcLord, 3, 0, 1100);
        await using (alice)
        await using (bob)
        {
            int attack = await OnLoop(() => Service<SkillService>().CurrentAttack(a));
            Assert.True((await CastAt(alice, a, attack, b)).Damage > 0);
            await OnLoop(() => { Service<CombatService>().SetEventPvPHandlers((_, _) => false, (_, _) => Task.CompletedTask); return 0; });
            await Task.Delay(300);
            Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)), h => h.SrcID == b.Role!.Id);

            var striker = (await Strikers(b, 1))[0];
            await SwungAt(bob, b, striker);
            await SkillAt(bob, b, striker, SwordSmash);
            await Task.Delay(300);
            Assert.DoesNotContain(bob.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)), h => h.SkillId == 2051701);
        }
    }

    // Two holders: Alice's Sword Smash on Bob comes back as a reflect, and Alice's own Orc Lord does not answer that, even off cooldown.
    [ClientDataFact]
    public async Task AReflectIsNotReflected()
    {
        var (bob, b) = await Holder("Bob", OrcLord, 3);
        await using (bob)
        {
            var (alice, a) = await Holder("Alice", ShadowOrcLord, 3);
            await using (alice)
            {
                await Gm("alice", "!job 1100");
                await Learn(a, 11099);
                await Duel(alice, a, bob, b);
                await CastAt(alice, a, SwordSmash, b);
                await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2051701);
                await Task.Delay(300);
                Assert.Empty(SplashHits(bob, 2051601));
            }
        }
    }

    /// <summary>Alice with the card, a Swordman, Sword Smash learned, against a plain Bob.</summary>
    private async Task<(BotClient Alice, GameSession A, BotClient Bob, GameSession B)> AttackerDuel(int card, int grid, int star)
    {
        var (alice, a) = await Holder("Alice", card, grid);
        await AtStar(a, card, star);
        await Gm("alice", "!job 1100");
        await Learn(a, 11099);
        var bob = await InWorld("Bob");
        var b = Session("Bob");
        await Gm("bob", "!lvl 80");
        await Duel(alice, a, bob, b);
        return (alice, a, bob, b);
    }

    private const int Stun = 901601;

    private static List<BuffInfo> StatusShown(BotClient bot, int buff) =>
    [
        .. bot.Inbox.Snapshot().Where(e => e.IsGame(BuffService.BuffUpdate)).SelectMany(e => BuffUpdateNotify.Parser.ParseFrom(e.Body).Buffs).Where(b => b.ConfigId == buff),
    ];

    // Card: Savage Babe T3: 2000201 "When dealing damage, has a 3% Chance to inflict Stun on the enemy for 0.5 sec. Internal Cooldown: 15
    // sec." A player's Sword Smash on a player: Stun shows on Bob, 500 ms, once in the 15 s.
    [ClientDataFact]
    public async Task SavageBabeStunsAPlayerAtItsStarsChanceAndThenWaitsItsCooldown()
    {
        var (alice, a, bob, b) = await AttackerDuel(1420101, 1, 3);
        await using (alice)
        await using (bob)
        {
            var effects = Service<CardEffects>();
            effects.Roll = _ => 300;
            await CastAt(alice, a, SwordSmash, b);
            await Task.Delay(200);
            Assert.Empty(StatusShown(bob, Stun));

            effects.Roll = _ => 299;
            await CastAt(alice, a, SwordSmash, b);
            var shown = await Next(bob, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(x => x.ConfigId == Stun));
            Assert.Equal(500, shown.Buffs.Single(x => x.ConfigId == Stun).Duration);

            effects.Roll = _ => 0;
            await CastAt(alice, a, SwordSmash, b);
            await Task.Delay(200);
            Assert.Single(StatusShown(bob, Stun));
        }
    }

    // A hit that kills leaves no one to hold the status, and no proc of the attacker runs on a fixed hit, which a reflect is: Bob's Orc
    // Lord answers Alice, and Bob's Savage Babe does not take that for his own damage.
    [ClientDataFact]
    public async Task AReflectSetsOffNoneOfTheReflectersOnHitProcs()
    {
        var (bob, b) = await Holder("Bob", OrcLord, 3);
        await using (bob)
        {
            await Gm("bob", "!item 1420101 1");
            await Eventually(() => b.Items.Any(i => i.Entry == 1420101), "the card");
            await bob.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = b.Items.First(i => i.Entry == Dagger).Guid });
            Assert.Equal(0, (await Next(bob, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
            Assert.Equal(0, await Insert(bob, 1, 1, b.Items.Single(i => i.Entry == 1420101).Guid));
            await using var alice = await InWorld("Alice");
            var a = Session("Alice");
            await Gm("alice", "!lvl 80");
            await Gm("alice", "!job 1100");
            await Learn(a, 11099);
            await Duel(alice, a, bob, b);
            Service<CardEffects>().Roll = _ => 0;
            await CastAt(alice, a, SwordSmash, b);
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2051701);
            await Task.Delay(200);
            Assert.Empty(StatusShown(alice, Stun));
        }
    }

    // Card: Hunter Fly T3: 2017201 "has a 4% Chance to restore Life equal to 10% of the damage dealt to the enemy, up to 2000. Internal
    // Cooldown: 2 sec."
    [ClientDataFact]
    public async Task HunterFlyStealsLifeFromAPlayerAtItsStarsChance()
    {
        var (alice, a, bob, b) = await AttackerDuel(1430118, 1, 3);
        await using (alice)
        await using (bob)
        {
            await Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().SetHpSpAsync(a, 100, long.MaxValue));
            var effects = Service<CardEffects>();
            effects.Roll = _ => 400;
            await CastAt(alice, a, SwordSmash, b, refillCaster: false);
            await Task.Delay(100);
            Assert.Equal(100, await Attribute(a, 2000));

            effects.Roll = _ => 399;
            long dealt = (await CastAt(alice, a, SwordSmash, b, refillCaster: false)).Damage;
            await Eventually(() => Service<CharacterService>().Stats(a)[2000] != 100, "the Life restored");
            Assert.Equal(100 + dealt / 10, await Attribute(a, 2000));
        }
    }

    // Card: Dracula T2: 2050401 "has a 10% Chance to absorb 5% of the damage dealt to the enemy as SP."
    [ClientDataFact]
    public async Task DraculaAbsorbsSpFromAPlayerAtItsChance()
    {
        var (alice, a, bob, b) = await AttackerDuel(1460102, 1, 2);
        await using (alice)
        await using (bob)
        {
            async Task<(long Sp, long Dealt)> After(int roll)
            {
                Service<CardEffects>().Roll = _ => roll;
                await Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().SetHpSpAsync(a, long.MaxValue, 500));
                long dealt = (await CastAt(alice, a, SwordSmash, b, refillCaster: false)).Damage;
                await Task.Delay(100);
                return (await Attribute(a, 2001), dealt);
            }
            var (spent, _) = await After(1000);
            var (absorbed, dealt) = await After(999);
            Assert.True(dealt >= 20, "too little damage to read 5% of: " + dealt);
            Assert.Equal(spent + dealt / 20, absorbed);
        }
    }

    // Card: Familiar T3: 2001201, 3%, Blind 902501 for 1 s. "Reduces field of view": a Blinded monster no longer sees a player 6 m off,
    // and still does at 4 m (BuffService.BlindRadiusCm), as with a guardian's Blind.
    [ClientDataFact]
    public async Task FamiliarsBlindLimitsAMonstersSight()
    {
        const int card = 1420104, blind = 902501;
        var (alice, session) = await Socketed(card);
        await using (alice)
        {
            await AtStar(session, card, 3);
            var target = (await Dummies(session, 5000002))[0];
            var effects = Service<CardEffects>();
            var monsters = Service<MonsterService>();
            bool SeesAt(float metres)
            {
                target.X = session.Position.X + metres;
                return monsters.TargetVisible!(target, session);
            }
            effects.Roll = _ => 300;
            await SwingOnce(alice, session, target);
            await Task.Delay(100);
            Assert.True(await OnLoop(() => SeesAt(6)));
            await OnLoop(() => SeesAt(1));

            effects.Roll = _ => 299;
            await SwingOnce(alice, session, target);
            await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(x => x.ConfigId == blind));
            Assert.False(await OnLoop(() => SeesAt(6)));
            Assert.True(await OnLoop(() => SeesAt(4)));
        }
    }

    // Card: Bongun T2: 2019301, 4%, 2 s: "When making a Physical attack" holds for a hit on a player, and Bash lands on him.
    [ClientDataFact]
    public async Task BongunAutocastsBashOnAPlayer()
    {
        var (alice, a, bob, b) = await AttackerDuel(Bongun, 6, 2);
        await using (alice)
        await using (bob)
        {
            var effects = Service<CardEffects>();
            effects.Roll = _ => 400;
            await CastAt(alice, a, SwordSmash, b);
            await NoHitOf(alice, CardBash);
            effects.Roll = _ => 399;
            await CastAt(alice, a, SwordSmash, b);
            var bash = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == CardBash);
            Assert.Equal(b.Role!.Id, Assert.Single(bash.Dsts).DstID);
            Assert.True(bash.Dsts[0].Damage > 0);
        }
    }

    // Card: Side Winder T1: 2021201, 8%: a Basic Attack on a player.
    [ClientDataFact]
    public async Task SideWinderDoubleAttacksAPlayer()
    {
        const int doubleAttack = 2003001;
        var (alice, a, bob, b) = await AttackerDuel(1440107, 1, 1);
        await using (alice)
        await using (bob)
        {
            int attack = await OnLoop(() => Service<SkillService>().CurrentAttack(a));
            Service<CardEffects>().Roll = _ => 799;
            await CastAt(alice, a, attack, b);
            var extra = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == doubleAttack);
            Assert.Equal(b.Role!.Id, Assert.Single(extra.Dsts).DstID);
        }
    }

    // Card: Doppelganger T2: 2050701 "your next instance of damage is increased by 5%", on a player too.
    [ClientDataFact]
    public async Task DoppelgangerRaisesTheNextHitOnAPlayer()
    {
        var (alice, a, bob, b) = await AttackerDuel(Doppelganger, 1, 2);
        await using (alice)
        await using (bob)
        {
            long plain = (await CastAt(alice, a, SwordSmash, b)).Damage;
            await SetCrit(true);
            var crit = await CastAt(alice, a, SwordSmash, b);
            Assert.Equal(DamageHitResult.Critical, crit.HitResult);
            await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(x => x.ConfigId == NextHitBuff));
            await SetCrit(false);
            Assert.Equal(Raised(plain, 500), (await CastAt(alice, a, SwordSmash, b)).Damage);
            Assert.Equal(plain, (await CastAt(alice, a, SwordSmash, b)).Damage);
        }
    }

    // Card: Dark Priest T3: 2023201 "When dealing DMG, there is a 12% Chance to reduce the target's current SP by 20%, up to 400.
    // Internal Cooldown: 15 sec." Row 2003101 [20, 400]. Bob's SP on the wire (his 31008 and the attribute).
    [ClientDataFact]
    public async Task DarkPriestBurnsAPlayersSpAtItsStarsChanceAndThenWaitsItsCooldown()
    {
        var (alice, a, bob, b) = await AttackerDuel(1440112, 1, 3);
        await using (alice)
        await using (bob)
        {
            long full = await Attribute(b, 2001);
            var effects = Service<CardEffects>();
            effects.Roll = _ => 1200;
            await CastAt(alice, a, SwordSmash, b);
            await Task.Delay(100);
            Assert.Equal(full, await Attribute(b, 2001));

            effects.Roll = _ => 1199;
            await CastAt(alice, a, SwordSmash, b);
            await Eventually(() => Service<CharacterService>().Stats(b)[2001] != full, "the SP burned");
            long burned = full - await Attribute(b, 2001);
            Assert.Equal(Math.Min(400, full * 20 / 100), burned);

            effects.Roll = _ => 0;
            await CastAt(alice, a, SwordSmash, b);
            await Task.Delay(100);
            Assert.Equal(full - burned, await Attribute(b, 2001));
        }
    }

    // "A monster has no current SP": a hit on one rolls nothing.
    [ClientDataFact]
    public async Task DarkPriestRollsNothingOnAMonster()
    {
        const int card = 1440112;
        var (alice, session) = await Socketed(card);
        await using (alice)
        {
            var target = (await Dummies(session, 5000002))[0];
            List<int> rolled = [];
            Service<CardEffects>().Roll = bound => { rolled.Add(bound); return 0; };
            await SwingOnce(alice, session, target);
            await Task.Delay(100);
            Assert.Empty(rolled);
        }
    }
}
