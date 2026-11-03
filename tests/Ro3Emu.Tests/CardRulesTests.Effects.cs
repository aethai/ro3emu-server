using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Card special effects played on the real cards, socketed through 31406 and driven by real casts.</summary>
public sealed partial class CardRulesTests
{
    /// <summary>A card goes in the grid of its _iCardPart; the gear that opens that grid's sockets.</summary>
    private const int Pauldrons = 2600120, Sandals = 2650120;

    private async Task<(BotClient Bot, GameSession Session)> SocketedOn(int card, int grid, int gear)
    {
        var alice = await InWorld("Alice");
        var session = Session("Alice");
        await Gm("alice", "!lvl 80");
        await Gm("alice", $"!item {card} 1");
        await Gm("alice", $"!item {gear} 1");
        await Eventually(() => session.Items.Any(i => i.Entry == gear), "the gear");
        await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)grid, ItemGUID = session.Items.Single(i => i.Entry == gear).Guid });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        Assert.Equal(0, await Insert(alice, grid, 1, session.Items.Single(i => i.Entry == card).Guid));
        return (alice, session);
    }

    /// <summary>A socketed copy at a star (CardConfig._iTier), as a star-up leaves it.</summary>
    private Task AtStar(GameSession session, int card, int star) => Service<GameLoop>().InvokeAsync(async () =>
    {
        (session.Items.Single(i => i.Entry == card).Attrs ??= [])[502] = star;
        await Service<CardEffects>().RefreshAsync(session);
        await Service<CharacterService>().RefreshStatsAsync(session);
    });

    /// <summary>The attributes as the owner's stat window reads them (31008).</summary>
    private static async Task<Dictionary<long, long>> Sheet31008(BotClient bot, Func<Dictionary<long, long>, bool> match)
    {
        Dictionary<long, long> Values(OnePropertyChange change) => change.Property.Propertys.ToDictionary(p => (long)p.PropertyId, p => (long)p.Vd64);
        return Values(await Next(bot, CharacterService.OnePropertyChange, OnePropertyChange.Parser, m => match(Values(m))));
    }

    // Shadow Card: Eddga, part 5, T2 [[31, 290]]. AttributeConversionConfig 31: "Converted to other crowd-control debuff
    // attributes", 702, 704, 706, 708, 714, 716, 720, 724, 726, 728 at 1 each. The sheet is what the tag rules read
    // (CombatService.TagAttribute).
    [ClientDataFact]
    public async Task ShadowEddgasCrowdControlResistanceReachesEveryControlsOwnResistance()
    {
        const int card = 1450500;
        var (alice, session) = await SocketedOn(card, 5, Sandals);
        await using (alice)
        {
            long[] members = [702, 704, 706, 708, 714, 716, 720, 724, 726, 728];
            var socketed = await Sheet31008(alice, values => values.GetValueOrDefault(31) == 200);
            Assert.All(members, id => Assert.Equal(200, socketed[id]));

            await AtStar(session, card, 2);
            var starred = await Sheet31008(alice, values => values.GetValueOrDefault(31) == 290);
            Assert.All(members, id => Assert.Equal(290, starred[id]));
            Assert.Equal(0, starred.GetValueOrDefault(710)); // not a member
        }
    }

    // Shadow Card: Deviling, part 4, T1 [[311, 800], [30, -200]]: Neutral resist 311 keeps its own line, every other element
    // (AttributeConversionConfig 30: 312-320) comes out 2% worse.
    [ClientDataFact]
    public async Task ShadowDevilingsOtherElementPenaltyCountsOnEveryOtherElementOnly()
    {
        const int card = 1450400;
        var (alice, session) = await SocketedOn(card, 4, Pauldrons);
        await using (alice)
        {
            await AtStar(session, card, 1);
            var sheet = await Sheet31008(alice, values => values.GetValueOrDefault(311) == 800);
            Assert.Equal(-200, sheet[30]);
            foreach (long element in Enumerable.Range(312, 9).Select(id => (long)id)) Assert.Equal(-200, sheet[element]);
        }
    }

    /// <summary>Every roll fixed: hits land, DamageFloat at exactly 1, a crit by choice.</summary>
    private sealed class FixedRolls(bool crit) : CombatRolls
    {
        public override double Hit() => 0;
        public override double DamageFloat() => 0.5;
        public override double Crit() => crit ? -1 : 1;
    }

    /// <summary>Monsters only of this test's, out of battle and hard to kill, 1 m from the player.</summary>
    private async Task<MonsterUnit[]> Dummies(GameSession session, params int[] npcs)
    {
        var monsters = Service<MonsterService>();
        var units = new MonsterUnit[npcs.Length];
        for (int i = 0; i < npcs.Length; i++)
        {
            int npc = npcs[i];
            var template = monsters.Template(npc)! with { RoamDistance = 0, SurroundDistance = 0 };
            units[i] = await Service<GameLoop>().InvokeAsync(async () => (await monsters.SpawnSingleAsync(session.SceneEntry, npc,
                new MapPoint(session.Position.X, session.Position.Y, session.Position.Z, 0), session.InstanceId, customTemplate: template))!);
        }
        await Service<GameLoop>().InvokeAsync(() =>
        {
            foreach (var other in Service<World>().Map(session.WorldScene).Units.OfType<MonsterUnit>().Where(m => !units.Contains(m)))
            { other.X += 200; other.Z += 200; }
            foreach (var unit in units)
            {
                unit.StaysOutOfBattle = true;
                unit.Hp = 100_000_000;
                unit.X = session.Position.X + 1;
                unit.Z = session.Position.Z;
            }
            Service<CombatService>().Rolls = new FixedRolls(crit: false);
            return Task.CompletedTask;
        });
        return units;
    }

    private async Task<long> SwingDamage(BotClient bot, GameSession session, MonsterUnit target) =>
        Assert.Single((await Swing(bot, session, target)).Dsts).Damage;

    private static long Raised(long damage, long basisPoints) => damage + (long)Math.Floor(damage * basisPoints / 10000d);

    // Card: Orc Lady, no stat line at any star, so the card's whole effect is the buff: 2015001 "Damage against all Orcs +1.8%
    // (excluding Orc Lord and Orc Hero)", T2 2015201 +4.6%. Orc Lady 5000099 holds _kNpcTag 1000; Orc Hero 5020004 holds no tag.
    [ClientDataFact]
    public async Task OrcLadyCardRaisesDamageOnOrcsByItsStarAndLeavesOrcHeroAlone()
    {
        const int card = 1430108;
        var (alice, session) = await Socketed(card);
        await using (alice)
        {
            var units = await Dummies(session, 5000099, 5020004, 5000002);
            var (orc, hero, other) = (units[0], units[1], units[2]);
            await Eventually(() => session.Buffs.Any(b => b.IsPassive && b.ConfigId == 2015001), "the card's passive");
            long orcT0 = await SwingDamage(alice, session, orc), heroWith = await SwingDamage(alice, session, hero), otherWith = await SwingDamage(alice, session, other);

            await AtStar(session, card, 2);
            Assert.Contains(session.Buffs, b => b.IsPassive && b.ConfigId == 2015201);
            long orcT2 = await SwingDamage(alice, session, orc);

            await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)1, SlotIndex = 1 });
            Assert.Equal(0, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
            await Eventually(() => !session.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned), "the passive gone");
            long orcPlain = await SwingDamage(alice, session, orc), heroPlain = await SwingDamage(alice, session, hero), otherPlain = await SwingDamage(alice, session, other);

            Assert.True(orcPlain >= 56, "too little damage to see 1.8% in: " + orcPlain);
            Assert.Equal(Raised(orcPlain, 180), orcT0);
            Assert.Equal(Raised(orcPlain, 460), orcT2);
            Assert.Equal(heroPlain, heroWith);
            Assert.Equal(otherPlain, otherWith);
        }
    }

    // Every family row says what it adds, and the tag it counts on sits on a monster the text names.
    [ClientDataTheory]
    [InlineData(2015001, 180, 5000099)] // "Damage against all Orcs +1.8%": Orc Lady
    [InlineData(2015301, 600, 5000139)] // +6%: Orc Archer
    [InlineData(2022001, 360, 5000160)] // "Damage against all Kobolds +3.6%": Mace Kobold
    [InlineData(2022301, 900, 5000165)] // +9%: Kobold Archer
    [InlineData(2027201, 460, 5000141)] // "Damage against Kara Crabs +4.6%": Crab
    [InlineData(2028101, 320, 5000144)] // "Damage against Shellfish +3.2%"
    [InlineData(2029301, 600, 5000145)] // "Damage against Aster +6%"
    [InlineData(2053201, 500, 5000092)] // "Damage dealt to Goblins +5%": Goblin (Axe)
    [InlineData(2053301, 1000, 5020007)] // +10%: Goblin Leader
    public void EveryFamilyRowCountsItsNumberOnItsMonsters(int id, long basisPoints, int npc)
    {
        var data = Service<GameData>();
        var buff = Ro3Emu.Server.Data.Config.BuffConfig.Get(data, id)!.Value;
        Assert.Equal([basisPoints], buff.LogicData);
        Assert.Equal(basisPoints, (long)Math.Round(double.Parse(buff.DescData[0], System.Globalization.CultureInfo.InvariantCulture) * 100));
        Assert.Contains(CardEffects.FamilyTag(buff.BuffId), Ro3Emu.Server.Data.Config.NPCConfig.Get(data, npc)!.Value.NpcTag);
    }
}
