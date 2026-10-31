using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The Fashion cart kept by the server (5024 shoppingCartProtocolVersion 1, the server's ShopService.Cart.cs). The
    /// client can never use it as shipped:
    /// <list type="bullet">
    /// <item>The version lives on the DAL instance. LC_AssetManager.GetDAL creates it with require("lc_dal_shop").New()
    /// (LC_AssetManager.lua:98-129), and LC_DAL_Shop.OnShopInfoNtfAll writes self.m_iShoppingCartProtocolVersion on it
    /// (LC_DAL_Shop.lua:239 in the decoded copy, 250 in the 2026.10.30.1 client).</item>
    /// <item>The UI asks the module's class table instead: UI_Sys_AppearanceShopWidgetCtrl keeps require("lc_dal_shop")
    /// in L12_1 (lua:57-59) and _addToCart calls L12_1:IsShoppingCartProtocolSupported() (lua:3402-3413); the cart panel
    /// (lua:16-18, 488, 793), its model (lua:6-8, 89) and UI_Sys_Shop_TradeTipsWidgetCtrl (lua:16-18, 300) do the same.</item>
    /// <item>BaseClass keeps instance fields off the class: an instance is a fresh table whose __index is the class's
    /// method table (Lua_BaseClass.lua:54-80). The class reads nil, "nil == 1" is false, so the UI never sends
    /// 82341/82342 and keeps its cart in memory, whatever 5024 says (seen 31.10: 5024 with version 1, no 82341).</item>
    /// </list>
    /// IsShoppingCartProtocolSupported is replaced on the class (BaseClass's __newindex writes the shared method table,
    /// Lua_BaseClass.lua:82-98, so the instance's own calls reach it too) by a C# delegate: the caller's own field, else
    /// the DAL instance's (LC_AssetManagerIns.m_kDALMapping.lc_dal_shop), so the UI follows what the server sent.
    /// </summary>
    public static class ShopCart
    {
        /// <summary>A Lua method called as self:Method() that answers a boolean.</summary>
        public delegate bool SelfPredicate(Obf_R.Obf_ff self);

        private const string Field = "m_iShoppingCartProtocolVersion";
        // Kept alive here: Lua holds the pushed delegate only weakly through xLua's object pool.
        private static readonly SelfPredicate SupportedWrapper = IsSupported;
        private static Obf_R.Obf_ff _global;
        private static bool _reported;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(Obf_R.Obf_ff global, Obf_R.Obf_ff shop)
        {
            _global = global;
            try
            {
                if (shop?.Obf_RuA<object>("IsShoppingCartProtocolSupported") == null)
                {
                    Debug.LogWarning("[Ro3Emu.ShopCart] LC_DAL_Shop.IsShoppingCartProtocolSupported not found; the Fashion cart stays local");
                    return;
                }
                shop.Obf_luA<string, SelfPredicate>("IsShoppingCartProtocolSupported", SupportedWrapper);
                Debug.Log("[Ro3Emu.ShopCart] LC_DAL_Shop.IsShoppingCartProtocolSupported reads the DAL instance: the Fashion cart follows 5024");
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.ShopCart] " + e.GetType().Name + ": " + e.Message); }
        }

        private static bool IsSupported(Obf_R.Obf_ff self)
        {
            try
            {
                object own = self?.Obf_RuA<object>(Field);
                object dal = own == null ? _global?.Obf_LuA<Obf_R.Obf_ff>("LC_AssetManagerIns.m_kDALMapping.lc_dal_shop")?.Obf_RuA<object>(Field) : null;
                return ShopCartProtocol.Supported(own, dal);
            }
            catch (Exception e)
            {
                if (!_reported) Debug.LogWarning("[Ro3Emu.ShopCart] " + e.GetType().Name + ": " + e.Message);
                _reported = true;
                return false;
            }
        }
    }
}
