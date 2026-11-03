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
}
