#nullable enable
namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Whether the Fashion cart is the server's (ShopCart), kept apart from Unity so the rule is testable (the server test
    /// project compiles this file too). LC_DAL_Shop.IsShoppingCartProtocolSupported is "self.m_iShoppingCartProtocolVersion
    /// == 1"; only the DAL instance has that field, so a caller holding the module's class table reads the instance's.
    /// </summary>
    internal static class ShopCartProtocol
    {
        /// <summary>5024 DBShopInfo.shoppingCartProtocolVersion with the cart on the server (LC_DAL_Shop.lua:265-272).</summary>
        public const long ServerCart = 1;

        /// <summary>
        /// own: the caller's own m_iShoppingCartProtocolVersion (null on the class table, which has no such field);
        /// dal: the DAL instance's, which the 5024 handler wrote (null before the DAL exists).
        /// </summary>
        public static bool Supported(object? own, object? dal) => Number(own ?? dal) == ServerCart;

        private static long Number(object? value) => value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            float f => (long)f,
            _ => long.MinValue,
        };
    }
}
