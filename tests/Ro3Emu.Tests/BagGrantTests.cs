using Microsoft.Extensions.Time.Testing;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>A granted item stacks only in the bag pack it goes to (CharacterService.CanStackGrant): a stack of the same
/// entry in the merchant cart (pack 100) is cargo, not bag space. Synthetic item 1000000 is a stackable consumable.</summary>
public sealed class BagGrantTests : IAsyncLifetime
{
    private const int Consumable = 1000000, Cart = 100;
    private readonly FakeTimeProvider _clock = TestServer.CreateFakeClock();
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartSyntheticAsync(clock: _clock);

    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    [Fact]
    public async Task AGrantDoesNotStackIntoTheMerchantCart()
    {
        var bot = await _server.InWorldAsync("Cargo");
        var s = _server.Session(bot);
        var character = _server.Service<CharacterService>();
        var cargo = new InvItem { Guid = 9_200_001, Entry = Consumable, Stack = 1, Pack = Cart, Sort = 1 };
        await _server.OnLoopAsync(async () =>
        {
            s.Items.RemoveAll(i => i.Entry == Consumable);
            s.Items.Add(cargo);
            Assert.NotNull(await character.ExchangeAsync(s, new ExchangeRequest { Goods = [(Consumable, 3)], Popup = false, Sync = false }));
        });
        var held = await _server.OnLoopAsync(() => Task.FromResult(s.Items.Where(i => i.Entry == Consumable).Select(i => (i.Pack, i.Stack)).ToList()));
        Assert.Contains((Cart, 1), held);
        Assert.Contains((1, 3), held);
    }
}
