using Romsg;
using Ro3Emu.Server.Game.Messaging;

namespace Ro3Emu.Server.Game;

/// <summary>Shops, mail claims, auctions, player trade and stalls.</summary>
public sealed class EconomyMessages(
    ShopService shops,
    MailService mail,
    AuctionService auctions,
    TradeService trade,
    StallsService stalls,
    TimedBattleService timedBattles) : IMessageModule
{
    public void Map(MessageRoutes routes)
    {
        // Trade and stalls handle their own message ranges via interceptors and reserve their goods themselves.
        routes.AdmitEconomic(id => id is >= 32601 and <= 32630); // trade
        routes.AdmitEconomic(id => id is >= 33401 and <= 33418); // stalls
        routes.Intercept("trade", id => id is >= 32601 and <= 32630, (session, msgId, body) =>
            session.Role != null && session.Progress != null ? trade.HandleAsync(session, msgId, body) : Task.FromResult(false));
        routes.Intercept("stalls", id => id is >= StallsService.Start and <= StallsService.Share, (session, msgId, body) =>
            session.Role != null && session.Progress != null ? stalls.HandleAsync(session, msgId, body) : Task.FromResult(false));

        MapShopsAndMail(routes);
        MapAuctions(routes);
    }

    private void MapShopsAndMail(MessageRoutes routes)
    {
        routes.Map<BuyCommodityReq>(ShopService.BuyCommodity, "shops", RouteAdmission.Role, async (session, buy) =>
        {
            // A Survivor Battle shop NPC is paid from the match pack (TimedBattleService.ChickenShop).
            if (!await timedBattles.TryBuyAsync(session, buy)) await shops.BuyAsync(session, buy);
        });
        routes.Map<GetDungeonShopReq>(TimedBattleService.DungeonShop, "shops", RouteAdmission.Role, timedBattles.GetDungeonShopAsync);
        routes.Map<ItemExchangeReq>(ShopService.ItemExchange, "shops", RouteAdmission.Role, shops.ExchangeCurrencyAsync);
        routes.Map<BatchBuyCommodityReq>(ShopService.BatchBuyCommodity, "shops", RouteAdmission.Role, shops.BatchBuyAsync);
        routes.Map<CommodityCollectReq>(ShopService.CommodityCollect, "shops", RouteAdmission.Role, shops.CollectAsync);
        // Present/Pick reserve the recipient's own "gifts" blob themselves (another role, not the sender's).
        routes.Map<CommodityPresentReq>(ShopService.CommodityPresent, "shops", RouteAdmission.Economic, shops.PresentAsync);
        routes.Map<CommodityPickReq>(ShopService.CommodityPick, "shops", RouteAdmission.Economic, shops.PickAsync);
        routes.Map<CommodityGetInfoReq>(ShopService.CommodityGetInfo, "shops", RouteAdmission.Role, shops.CommodityInfoAsync);
        routes.Map<ClaimMailReq>(MailService.ClaimMail, "mail", RouteAdmission.Economic, mail.ClaimAsync);
    }

    private void MapAuctions(MessageRoutes routes)
    {
        routes.Map<QueryAuctionReq>(AuctionService.Query, "auctions", RouteAdmission.Economic, auctions.QueryAsync);
        routes.Map<BiddingAuctionReq>(AuctionService.Bid, "auctions", RouteAdmission.Economic, auctions.BidAsync);
        routes.Map<QueryHistoryAuctionReq>(AuctionService.History, "auctions", RouteAdmission.Economic, auctions.HistoryAsync);
        routes.Map<QuerySelfAuctionReq>(AuctionService.Self, "auctions", RouteAdmission.Economic, auctions.SelfAsync);
        routes.Map<QueryAuctionBonusReq>(AuctionService.QueryBonus, "auctions", RouteAdmission.Economic, auctions.QueryBonusAsync);
        routes.Map<GetAuctionBonusReq>(AuctionService.GetBonus, "auctions", RouteAdmission.Economic, auctions.GetBonusAsync);
        routes.Map<GetAuctionReq>(AuctionService.Get, "auctions", RouteAdmission.Economic, auctions.GetAsync);
        routes.Map<CancelBiddingAuctionReq>(AuctionService.Cancel, "auctions", RouteAdmission.Economic, auctions.CancelAsync);
        routes.Map<QuitAuctionReq>(AuctionService.Quit, "auctions", RouteAdmission.Economic, auctions.QuitAsync);
        routes.Map<BatchBiddingAuctionReq>(AuctionService.BatchBid, "auctions", RouteAdmission.Economic, auctions.BatchBidAsync);
        routes.Map(AuctionService.Resume, "auctions", RouteAdmission.Economic, auctions.ResumeAsync);
        routes.Map<PreAuctionItemStackQueryReq>(AuctionService.PreStack, "auctions", RouteAdmission.Economic, auctions.PreStackAsync);
        routes.Map<BatchCancelBiddingAuctionReq>(AuctionService.BatchCancel, "auctions", RouteAdmission.Economic, auctions.BatchCancelAsync);
        routes.Map<QueryAuctionCountReq>(AuctionService.Count, "auctions", RouteAdmission.Economic, auctions.CountAsync);
    }
}
