using Microsoft.Extensions.Time.Testing;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// A monster kill's reward raises CharacterService.StateChanged only when it touched what a listener reads
/// (CharacterInventoryService.QuietKillReward): a level change, a card, or an entry a listener watches. Any other
/// exchange raises it as before. Synthetic item 1000000 is an ordinary consumable.
/// </summary>
public sealed class KillRewardStateTests : IAsyncLifetime
{
    private const int Consumable = 1000000;
    private readonly FakeTimeProvider _clock = TestServer.CreateFakeClock();
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartSyntheticAsync(clock: _clock);

    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private static ExchangeRequest Loot(bool kill) => new()
    {
        Goods = [(Consumable, 1)], Popup = false, Sync = false,
        RewardSettlement = kill ? new RewardSettlement("kill/test/" + Guid.NewGuid().ToString("N"), "monster-kill") : null,
    };

    [Fact]
    public async Task OrdinaryKillLootRaisesNothingAndAWatchedEntryOrAnotherExchangeDoes()
    {
        var bot = await _server.InWorldAsync("Loot");
        var s = _server.Session(bot);
        var character = _server.Service<CharacterService>();
        int raised = 0;
        using var counting = character.StateChanged.Subscribe(session => { if (session == s) raised++; });

        await _server.OnLoopAsync(async () => Assert.NotNull(await character.ExchangeAsync(s, Loot(kill: true))));
        Assert.Equal(0, raised);

        await _server.OnLoopAsync(async () => Assert.NotNull(await character.ExchangeAsync(s, Loot(kill: false))));
        Assert.Equal(1, raised);

        character.WatchStateEntries(() => [Consumable]);
        await _server.OnLoopAsync(async () => Assert.NotNull(await character.ExchangeAsync(s, Loot(kill: true))));
        Assert.Equal(2, raised);
    }
}
