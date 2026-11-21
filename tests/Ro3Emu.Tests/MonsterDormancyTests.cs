using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>A monster no player shows skips its tick only while nothing is in progress for it (MonsterService.Dormant).</summary>
public sealed class MonsterDormancyTests
{
    [Fact]
    public async Task AnIdleMonsterNobodySeesOnlyMovesItsRoamClock()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = await host.InWorldAsync("Alice");
        await host.Service<GameLoop>().InvokeAsync(async () =>
        {
            var (monster, map) = await UnseenAsync(host, bot, template => template with { RoamDistance = 5 });
            monster.NextRoamMs = 0;
            host.Service<MonsterService>().Tick(monster, map);
            Assert.True(monster.NextRoamMs > MonsterService.NowMs);
            Assert.False(monster.Moving);
        });
    }

    [Fact]
    public async Task AMonsterNobodySeesStillFinishesWhatItWasDoing()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = await host.InWorldAsync("Alice");
        await host.Service<GameLoop>().InvokeAsync(async () =>
        {
            var (monster, map) = await UnseenAsync(host, bot, template => template with { RoamDistance = 0, HealsOnReturn = true });
            monster.Hp = monster.Template.MaxHp / 2;
            monster.Returning = true;
            host.Service<MonsterService>().Tick(monster, map);
            Assert.False(monster.Returning);
            Assert.Equal(monster.Template.MaxHp, monster.Hp);
        });
    }

    private static async Task<(MonsterUnit Monster, MapInstance Map)> UnseenAsync(SyntheticHost host, Ro3Emu.Bot.BotClient bot,
        Func<MonsterTemplate, MonsterTemplate> shape)
    {
        var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)bot.Self!.Uuid);
        var monsters = host.Service<MonsterService>();
        var origin = session.Position;
        var monster = await monsters.SpawnSingleAsync(session.SceneEntry, 16320001,
            new MapPoint(origin.X, origin.Y, origin.Z, 0), session.InstanceId, customTemplate: shape(monsters.Template(16320001)!));
        Assert.NotNull(monster);
        foreach (var viewer in monster.Viewers) viewer.VisibleUnits.Remove(monster);
        monster.Viewers.Clear();
        return (monster, host.Service<World>().Map(monster.WorldScene));
    }
}
