using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Guild War Technology (32304/32305, GuildTechService) on the client's own GuildWarTechConfig.</summary>
public sealed partial class GuildTests
{
    private const long InstituteBuilding = 3; // GuildBuildingConfig _iType 3, the Institute

    private static async Task<OperateGuildTechRes> BuyTech(BotClient bot, long group, int level)
    {
        await bot.SendGameAsync(GuildTechService.Operate, new OperateGuildTechReq { TechId = group, Level = level });
        return await Next(bot, GuildTechService.Operate, OperateGuildTechRes.Parser);
    }

    /// <summary>A stat group's rows by level, and the attribute and amount of a level (SpecialEffectConfig op 1).</summary>
    private (Dictionary<long, GuildWarTechConfig> Levels, long Attribute) StatGroup()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        var group = GuildWarTechConfig.All(data).GroupBy(r => r.Group).First(g => g.Count() >= 4
            && g.All(r => r.SpecialEffects.All(id => SpecialEffectConfig.Get(data, id)?.SpecialEffect is [[1, _, _]])));
        var first = group.Single(r => r.Level == 1);
        long attribute = SpecialEffectConfig.Get(data, first.SpecialEffects[0])!.Value.SpecialEffect[0][1];
        return (group.ToDictionary(r => r.Level), attribute);
    }

    private long Bonus(GuildWarTechConfig row) =>
        SpecialEffectConfig.Get(_app.Services.GetRequiredService<GameData>(), row.SpecialEffects[0])!.Value.SpecialEffect[0][2];

    [ClientDataFact]
    public async Task GuildTechLevelsCostSuppliesNeedTheInstituteAndResetWithTheSeason()
    {
        await using var alice = await InWorld("Alice");
        await Level("alice");
        long guildId = await CreateGuild(alice, "Lore");
        var store = _app.Services.GetRequiredService<GuildStore>();
        var (levels, _) = StatGroup();
        long group = levels[1].Group;

        await alice.SendGameAsync(GuildTechService.Query, new QueryGuildTechReq());
        var info = await Next(alice, GuildTechService.Query, QueryGuildTechRes.Parser);
        Assert.Equal(0, info.Result);
        Assert.Equal(GuildWarTechConfig.All(_app.Services.GetRequiredService<GameData>()).Select(r => r.Group).Distinct().Count(),
            info.TechInfo.TechLevel.Count);
        Assert.All(info.TechInfo.TechLevel.Values, level => Assert.Equal(0, level));

        // A plain member lacks the Building Level-up right the purchase needs.
        var guild = store.Get(guildId)!;
        int office = guild.Members[(long)alice.Self!.Uuid].Official;
        guild.Members[(long)alice.Self.Uuid].Official = 20;
        guild.Materials = levels[1].CostMoney[1];
        Assert.True(store.Save(guild));
        Assert.Equal(ErrorCodes.GuildPermission, (await BuyTech(alice, group, 1)).Result);
        guild = store.Get(guildId)!;
        guild.Members[(long)alice.Self.Uuid].Official = office;
        guild.Materials = 0;
        Assert.True(store.Save(guild));

        Assert.Equal(ErrorCodes.GuildSupplies, (await BuyTech(alice, group, 1)).Result);
        Assert.Equal(ErrorCodes.Parameter, (await BuyTech(alice, group, 2)).Result);
        guild = store.Get(guildId)!;
        guild.Materials = levels[1].CostMoney[1] + 1;
        Assert.True(store.Save(guild));
        var bought = await BuyTech(alice, group, 1);
        Assert.Equal(0, bought.Result);
        Assert.Equal(1, bought.TechInfo.TechLevel[group]);
        Assert.Equal(1, store.Get(guildId)!.Materials);
        Assert.Equal(1, store.Get(guildId)!.TechLevels[group]);

        // The level whose condition asks for a higher Institute than a new guild's first level.
        var locked = levels.Values.OrderBy(r => r.Level).First(r => r.Level > 1 && !Met(alice, r.Limit));
        guild = store.Get(guildId)!;
        guild.TechLevels = new() { [group] = locked.Level - 1 };
        guild.Materials = locked.CostMoney[1];
        Assert.True(store.Save(guild));
        Assert.Equal(ErrorCodes.GuildTechLocked, (await BuyTech(alice, group, (int)locked.Level)).Result);
        guild = store.Get(guildId)!;
        guild.Buildings[InstituteBuilding] = 3;
        Assert.True(store.Save(guild));
        Assert.Equal(0, (await BuyTech(alice, group, (int)locked.Level)).Result);
        guild = store.Get(guildId)!;
        guild.TechLevels = new() { [group] = levels.Keys.Max() };
        Assert.True(store.Save(guild));
        Assert.Equal(ErrorCodes.GuildTechMax, (await BuyTech(alice, group, (int)levels.Keys.Max() + 1)).Result);

        guild = store.Get(guildId)!;
        guild.TechSeason -= 1; // levels learned in another season
        Assert.True(store.Save(guild));
        await alice.SendGameAsync(GuildTechService.Query, new QueryGuildTechReq());
        Assert.Equal(0, (await Next(alice, GuildTechService.Query, QueryGuildTechRes.Parser)).TechInfo.TechLevel[group]);
    }

    private bool Met(BotClient bot, long condition)
    {
        var session = _app.Services.GetRequiredService<SessionRegistry>().ByRole((long)bot.Self!.Uuid)!;
        return _app.Services.GetRequiredService<GameLoop>()
            .InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<FunctionService>().ConditionMet(session, condition)))
            .GetAwaiter().GetResult();
    }

    [ClientDataFact]
    public async Task GuildTechStatsCountOnlyInTheLevelsPlayingMethods()
    {
        await using var alice = await InWorld("Alice");
        await Level("alice");
        long guildId = await CreateGuild(alice, "Lore");
        var store = _app.Services.GetRequiredService<GuildStore>();
        var tech = _app.Services.GetRequiredService<GuildTechService>();
        var character = _app.Services.GetRequiredService<CharacterService>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var session = _app.Services.GetRequiredService<SessionRegistry>().ByRole((long)alice.Self!.Uuid)!;
        var (levels, attribute) = StatGroup();
        var row = levels[2];
        long method = 0;
        using var hook = tech.PlayingMethods.Subscribe(_ => method);
        long Stat() => loop.InvokeAsync(() => { character.Refresh(session); return Task.FromResult(character.Stats(session)[(int)attribute]); })
            .GetAwaiter().GetResult();

        var guild = store.Get(guildId)!;
        guild.Materials = levels[1].CostMoney[1] + row.CostMoney[1];
        Assert.True(store.Save(guild));
        Assert.Equal(0, (await BuyTech(alice, row.Group, 1)).Result);
        Assert.Equal(0, (await BuyTech(alice, row.Group, 2)).Result);
        long outside = Stat();
        method = row.PlayingMethods[0];
        // A battle starting: the clock's sync republishes the sheet with the bonus, once.
        using (var published = alice.Inbox.Expect(e => e.IsGame(CharacterService.OnePropertyChange)))
        {
            await loop.InvokeAsync(() => tech.SyncAsync(session));
            await published.WaitAsync(Wait);
        }
        // Nothing changed since: no second refresh.
        Assert.True(await loop.InvokeAsync(() => Task.FromResult(ReferenceEquals(tech.SyncAsync(session), Task.CompletedTask))));
        Assert.Equal(outside + Bonus(row), Stat()); // the level's whole bonus, not level 1's plus level 2's
        method = GuildWarTechConfig.All(_app.Services.GetRequiredService<GameData>()).SelectMany(r => r.PlayingMethods)
            .Distinct().Except(row.PlayingMethods).DefaultIfEmpty(-1).First();
        Assert.Equal(outside, Stat());

        // A hot path's memoized sheet: the battle starting (no refresh in between) is a new key, not a stale reuse.
        long HotRead() => loop.InvokeAsync(() =>
        {
            character.ReuseStatsInThisWork(session);
            return Task.FromResult(character.Stats(session)[(int)attribute]);
        }).GetAwaiter().GetResult();
        method = 0;
        Assert.Equal(outside, HotRead());
        Assert.Equal(outside, HotRead());
        method = row.PlayingMethods[0];
        Assert.Equal(outside + Bonus(row), HotRead());
    }

    [ClientDataFact]
    public async Task AGuildTechLevelWithoutAStatEffectCanBeBoughtAndAddsNothing()
    {
        await using var alice = await InWorld("Alice");
        await Level("alice");
        long guildId = await CreateGuild(alice, "Lore");
        var data = _app.Services.GetRequiredService<GameData>();
        var store = _app.Services.GetRequiredService<GuildStore>();
        // A level whose effects hold no attribute op (the export's "Test Buff" / "Test Skill" rows).
        var row = GuildWarTechConfig.All(data).First(r => r.Level == 1
            && r.SpecialEffects.All(id => SpecialEffectConfig.Get(data, id)?.SpecialEffect is not { } ops || ops.All(op => op is not [1, _, _])));
        var guild = store.Get(guildId)!;
        guild.Buildings[InstituteBuilding] = 3;
        guild.Materials = row.CostMoney[1];
        Assert.True(store.Save(guild));
        Assert.Equal(0, (await BuyTech(alice, row.Group, 1)).Result);
        Assert.Equal(0, store.Get(guildId)!.Materials);
        var tech = _app.Services.GetRequiredService<GuildTechService>();
        var session = _app.Services.GetRequiredService<SessionRegistry>().ByRole((long)alice.Self!.Uuid)!;
        using var hook = tech.PlayingMethods.Subscribe(_ => row.PlayingMethods[0]);
        Assert.Equal(row.PlayingMethods[0], await _app.Services.GetRequiredService<GameLoop>()
            .InvokeAsync(() => Task.FromResult(tech.MemoKey(session).Method)));
    }
}
