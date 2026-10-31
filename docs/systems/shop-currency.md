# Currency Redemption and paid products

## Currency Redemption (2005 ITEM_EXCHANGE)

The shop's Currency Redemption window (functions 7005/7006 under shop 70, `UI_Sys_CoinExchangeWidgetCtrl`) turns one
currency into another at the rate of the client's `ItemExchangeConfig`. The widget opens on the currency it buys,
picks the currency it pays (Zeny is bought with Sacred Relic Crystal, Minted Coin with Zeny), reads the row keyed by
the paid item, shows `1:rate`, shows `count x rate` as what the player gets and caps the count at
`min(owned, single limit)`. It sends `ItemExchangeReq {itemId = paid item, count}`.

`ShopService.ExchangeCurrencyAsync` checks the row, its function, the count and the single limit, then pays `count`
of the paid item and grants `count x rate` of the target in one bag transaction. A request above the single limit is
refused with 209 "Single-exchange limit exceeded" instead of being trimmed (the client never sends one). The widget
shows "Redeemed" for result 0 and "Exchange failed" for anything else; balances arrive with the usual currency sync.
