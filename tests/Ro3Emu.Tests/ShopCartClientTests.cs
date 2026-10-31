using System.Text.RegularExpressions;
using Ro3Emu.ClientHooks;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Why the client needs Ro3Emu.ClientHooks.ShopCart to use the server's Fashion cart, and the rule it applies
/// (src/Ro3Emu.ClientHooks/ShopCartProtocol.cs, compiled into this project). The client checks the 5024 version with
/// IsShoppingCartProtocolSupported on LC_DAL_Shop's class table, while the 5024 handler writes it on the DAL instance.
/// </summary>
public sealed class ShopCartClientTests
{
    private static readonly string LuaDir = Path.Combine(ClientData.RepoRoot, "re", "clientB", "lua-src");

    /// <summary>Every caller of IsShoppingCartProtocolSupported outside LC_DAL_Shop.</summary>
    private static readonly string[] UiCallers =
    [
        "UI/Shop/Controllers/UI_Sys_AppearanceShopWidgetCtrl.lua", "UI/Shop/Controllers/UI_Sys_AppearanceShopCart_PanelCtrl.lua",
        "UI/Shop/Controllers/UI_Sys_Shop_TradeTipsWidgetCtrl.lua", "UI/Shop/Models/UI_Sys_AppearanceShopCart_PanelModel.lua",
    ];

    [Fact]
    public void TheHookAndTheServerAgreeOnTheVersion() =>
        Assert.Equal(ShopService.CartProtocolVersion, ShopCartProtocol.ServerCart);

    [Fact]
    public void TheClassTableFollowsTheDalInstance()
    {
        // The UI's require("lc_dal_shop") has no field of its own: the DAL instance's 5024 value decides.
        Assert.True(ShopCartProtocol.Supported(own: null, dal: 1L));
        Assert.False(ShopCartProtocol.Supported(own: null, dal: 0L));
        Assert.False(ShopCartProtocol.Supported(own: null, dal: null)); // no DAL yet: the client's own cart
        Assert.True(ShopCartProtocol.Supported(own: null, dal: 1.0));
    }

    [Fact]
    public void TheDalInstanceKeepsItsOwnValue()
    {
        // LC_DAL_Shop's own calls (OnShopInfoNtfAll, OnShoppingCartUpdate) pass the instance: its field decides, as before.
        Assert.True(ShopCartProtocol.Supported(own: 1L, dal: null));
        Assert.False(ShopCartProtocol.Supported(own: 0L, dal: 1L));
    }

    [ClientLuaFact]
    public void TheUiAsksTheClassTableWhileTheVersionIsWrittenOnTheInstance()
    {
        string Read(string path) => File.ReadAllText(Path.Combine(LuaDir, path));
        // The UI keeps the module itself (the class table) and asks it.
        foreach (var path in UiCallers)
        {
            string lua = Read(path);
            var module = Regex.Match(lua, @"(L\d+_1) = require\r?\n(L\d+_1) = ""lc_dal_shop""\r?\n\1 = \1\(\2\)\r?\n");
            Assert.True(module.Success, path + ": require(\"lc_dal_shop\") into an upvalue");
            string name = module.Groups[1].Value;
            Assert.DoesNotMatch(name + " = " + name + @"\.New\b", lua); // never .New(): the class table itself
            Assert.Contains("IsShoppingCartProtocolSupported", lua);
        }
        // The asset manager makes the DAL an instance (require(name).New()), and the 5024 handler writes the instance.
        Assert.Matches(@"L3_2 = require\r?\n\s*L4_2 = A1_2\r?\n\s*L3_2 = L3_2\(L4_2\)\r?\n\s*L3_2 = L3_2\.New\r?\n",
            Read("Logic/Asset/LC_AssetManager.lua"));
        string shop = Read("Logic/Asset/LC_DAL_Shop.lua");
        Assert.Contains("A0_2.m_iShoppingCartProtocolVersion = L5_2", shop);
        Assert.DoesNotContain("L0_1.m_iShoppingCartProtocolVersion =", shop);
    }
}
