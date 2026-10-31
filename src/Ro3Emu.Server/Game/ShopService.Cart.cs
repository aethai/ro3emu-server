using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 82341/82342: the Fashion-tab cart (UI_Sys_AppearanceShopCart_Panel), kept per role in the "shop" blob. 5024
/// shoppingCartProtocolVersion 1 (LC_DAL_Shop.IsShoppingCartProtocolSupported, LC_DAL_Shop.lua:265-272) switches the
/// client from its in-memory cart to this one: UI_Sys_AppearanceShopWidgetCtrl._addToCart (lua:3395-3459) sends
/// SHOPPING_CART_ADD {commodityId, count 1}, the cart's delete (UI_Sys_AppearanceShopCart_PanelCtrl._deleteSelected,
/// lua:442-559) one SHOPPING_CART_REMOVE {commodityId} per selected entry and per stale one. There is no "change count"
/// or "clear" message: a clear is one remove per entry. No reply handler is registered for either message; the client
/// reads the cart back only from 5024 shoppingCart {commodityId -> {commodityId, count, addTime}}
/// (LC_DAL_Shop.OnShopInfoNtfAll and the end handler OnShoppingCartUpdate -> _syncShoppingCart, lua:178-398), which
/// lists entries by addTime then id and marks an entry whose commodity no longer resolves as stale. Buying from the
/// cart is the ordinary 82340 (_doBuySelected, lua:742-850); with protocol 1 the client leaves the bought entries in
/// its copy (OnProcessBatchBuyCommodityResult, lua:557-677), so a successful buy drops its commodity from the cart in
/// the purchase's own commit (BuyOneAsync), for 82332 too.
///
/// (inferred) The original's refusals and limits are not in the client: an unknown commodity answers ShopNoConfig as
/// the other shop requests do; a commodity that is not a look (_iAppearanceId 0) or a count other than 1 (a look is
/// bought one at a time, Malformed) is a Parameter error; a look the role already owns answers ShopAppearanceOwned; a
/// full cart (<see cref="CartLimit"/>) answers Parameter; adding an entry already in the cart changes nothing and keeps
/// its addTime (the protocol-0 client ignored a second add the same way); removing an absent entry succeeds. A cart
/// change moves no goods, so it commits the blob alone, without an economy receipt.
/// </summary>
public sealed partial class ShopService
{
    /// <summary>The 5024 shoppingCartProtocolVersion the client switches on (LC_DAL_Shop.lua:265-272).</summary>
    public const int CartProtocolVersion = 1;

    /// <summary>
    /// (inferred) Entries a cart holds, stale ones included. The client data has no cart limit; this client sells 27
    /// looks, so 50 never stops a player and only bounds the saved blob.
    /// </summary>
    public const int CartLimit = 50;

    public sealed class CartEntry
    {
        public int Count { get; set; }
        /// <summary>Unix seconds of the add (inferred unit; the client only sorts by it).</summary>
        public long AddTime { get; set; }
    }

    /// <summary>82341 SHOPPING_CART_ADD: one look into the cart.</summary>
    public async Task CartAddAsync(GameSession session, ShoppingCartAddReq req)
    {
        using var guard = await CharacterService.Lock(session);
        session.Owners!.Current!.BindPresentation(session);
        int code = CartRefusal(session, req.CommodityId, req.Count);
        if (code == 0 && !session.Shop!.Cart.ContainsKey(req.CommodityId))
        {
            var next = Copy(session.Shop);
            next.Cart[req.CommodityId] = new CartEntry { Count = req.Count, AddTime = Now };
            await CommitCartAsync(session, next, "shop/cart-add");
            await SyncAsync(session);
        }
        await session.SendGameAsync(ShoppingCartAdd, new ShoppingCartAddResp { Result = code });
    }

    /// <summary>82342 SHOPPING_CART_REMOVE: any entry, stale ones (no ShopCommodityConfig row any more) included.</summary>
    public async Task CartRemoveAsync(GameSession session, ShoppingCartRemoveReq req)
    {
        using var guard = await CharacterService.Lock(session);
        session.Owners!.Current!.BindPresentation(session);
        if (session.Shop!.Cart.ContainsKey(req.CommodityId))
        {
            var next = Copy(session.Shop);
            next.Cart.Remove(req.CommodityId);
            await CommitCartAsync(session, next, "shop/cart-remove");
            await SyncAsync(session);
        }
        await session.SendGameAsync(ShoppingCartRemove, new ShoppingCartRemoveResp { Result = 0 });
    }

    /// <summary>Why a commodity cannot go into the cart, or 0 (0 also when it is already there).</summary>
    private int CartRefusal(GameSession session, int commodityId, int count)
    {
        if (ShopCommodityConfig.Get(_data, commodityId) is not { } commodity) return ErrorCodes.ShopNoConfig;
        if (commodity.AppearanceId is <= 0 or > int.MaxValue || count != 1) return ErrorCodes.Parameter;
        if (session.Shop!.Cart.ContainsKey(commodityId)) return 0;
        if (session.Wardrobe is { } wardrobe && Owns(wardrobe, (int)commodity.AppearanceId))
            return ErrorCodes.ShopAppearanceOwned;
        return session.Shop.Cart.Count >= CartLimit ? ErrorCodes.Parameter : 0;
    }

    private async Task CommitCartAsync(GameSession session, ShopState next, string label)
    {
        var tx = _roles.NewTransaction();
        tx.RoleBlob(session.Role!.Id, "shop", JsonSerializer.SerializeToUtf8Bytes(next));
        await _roles.CommitAsync(tx, label, _ => session.Shop = next);
    }
}
