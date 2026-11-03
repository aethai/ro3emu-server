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
    private async Task<DamageInfo> CastAt(BotClient bot, GameSession caster, int skill, GameSession target)
    {
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            caster.SkillCds.Clear(); caster.SkillModeCds.Clear(); caster.SwingLockUntilMs = 0; caster.SkillCast = null;
            await Service<CharacterService>().SetHpSpAsync(target, long.MaxValue, null);
            await Service<CharacterService>().SetHpSpAsync(caster, long.MaxValue, long.MaxValue);
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
}
