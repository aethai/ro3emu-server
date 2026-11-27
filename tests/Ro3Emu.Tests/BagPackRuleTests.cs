using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// An item's bag pack is the BackpackConfig tab whose _iTabContent lists its _iType (CharacterInventoryService.ConfigPack),
/// not ItemConfig._iBagTab. Synthetic rows: 3990011 a material naming tab 4, 3990012 a totem and 3990013 a Soul Echo
/// naming tab 1, 3990014 a material naming the card tab.
/// </summary>
public sealed class BagPackRuleTests
{
    private static readonly GameData Data = new(Options.Create(new ServerOptions { GameDataDir = SyntheticData.Dir }),
        NullLogger<GameData>.Instance);

    [Theory]
    [InlineData(3990011, 1)] // a material in the Divine Totem tab was the reported bug
    [InlineData(3990014, 1)] // a non-card in the card pack breaks the client's card sort
    [InlineData(3990012, 4)] // totems live in PackType_Totem whatever _iBagTab says
    [InlineData(3990013, 5)] // Soul Echoes in PackType_Emblem
    [InlineData(1000000, 1)] // a consumable
    [InlineData(3990004, 1)]
    public void ThePackFollowsTheItemTypeTab(int entry, int pack) =>
        Assert.Equal(pack, CharacterService.ConfigPack(Data, Data.Row("ItemConfig", entry)!.Value));

    [Fact]
    public void EquipmentGoesToTheEquipmentPack()
    {
        var equip = Data.Table("ItemConfig").Values.First(r => GameData.Int(r, "_iType") == 5);
        Assert.Equal(2, CharacterService.ConfigPack(Data, equip));
    }
}
