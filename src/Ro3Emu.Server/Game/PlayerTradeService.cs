using System.Globalization;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Player-to-player trade, driven from the chat line: <c>/trade &lt;player&gt;</c> offers, <c>/trade add</c> puts items
/// up, <c>/trade zeny</c> puts zeny up, both sides <c>/trade lock</c> then <c>/trade ok</c> and the swap commits.
/// <para>
/// Why chat: the client has no trade window. Its peer-to-peer surfaces are the trade market (326xx, TradeService), player
/// stalls (334xx, StallsService) and the friend gift (82209) - all served here - and none of them swaps two bags at once.
/// The swap itself is a market transaction (<see cref="MarketStore.RunAsync"/>), so both bags, both purses and the
/// instances (enchantments, attributes, binding) move in one SQLite transaction: a refusal rolls back every staged
/// change, and a committed trade publishes both bags to their sessions.
/// </para>
/// <para>
/// Offers name live bag instances by GUID, so the exact item a player put up is the item that moves; an offer whose
/// instance left the bag (used, sold, moved) refuses the trade instead of silently shipping another copy. Nothing is
/// reserved while the window is open: the trade is checked and moved once, at the commit.
/// </para>
/// </summary>
public sealed class PlayerTradeService(ChatService chat, MarketStore market, CharacterService character,
    TradeService trade, SessionRegistry sessions, GameLoop loop, GameData data, ILogger<PlayerTradeService> log)
{
    /// <summary>Currency 1 is Zeny (CurrencyDisplayConfig): the only purse a trade moves today.</summary>
    internal const int Zeny = 1;

    /// <summary>StallsBuyItemMail (1028): the template whose attachments are item stacks; a trade mails its goods.</summary>
    internal const int TradeMail = 1028;

    /// <summary>Stacks per side and zeny per offer: a chat window, not a warehouse.</summary>
    internal const int MaxOffers = 8;
    internal const long MaxZeny = 1_000_000_000;

    /// <summary>An unanswered <c>/trade &lt;player&gt;</c> expires; an open window closes after ten quiet minutes.</summary>
    private static readonly TimeSpan OfferWindow = TimeSpan.FromSeconds(90), TradeWindow = TimeSpan.FromMinutes(10);

    private sealed record Offer(long Guid, int Entry, string Name, int Count);

    private sealed class Side(long roleId, string name)
    {
        public long RoleId { get; } = roleId;
        public string Name { get; } = name;
        public List<Offer> Offers { get; } = [];
        public long Zeny { get; set; }
        public bool Locked { get; set; }
        public bool Confirmed { get; set; }
        /// <summary>Any change to what a side offers clears both confirmations: nobody is moved by a stale click.</summary>
        public void Changed() { Locked = false; Confirmed = false; }
        public bool Empty => Offers.Count == 0 && Zeny == 0;
    }

    private sealed class Window(Side a, Side b, DateTime expiresUtc)
    {
        public Side A { get; } = a;
        public Side B { get; } = b;
        public DateTime ExpiresUtc { get; set; } = expiresUtc;
        public Side Of(long roleId) => A.RoleId == roleId ? A : B;
        public Side Other(long roleId) => A.RoleId == roleId ? B : A;
        public bool Ready => A.Locked && B.Locked && A.Confirmed && B.Confirmed;
    }

    private readonly object _gate = new();
    /// <summary>Role id to the window it is in: one at a time, both sides.</summary>
    private readonly Dictionary<long, Window> _windows = [];

    /// <summary>Registered from the constructor so a chat line reaches this service (like /pass).</summary>
    public void Start() => chat.PlayerCommands["/trade"] = RunAsync;

    // ---------------------------------------------------------------- commands

    private async Task RunAsync(GameSession s, string args)
    {
        if (s.Role is not { } role) return;
        string[] words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = words.Length == 0 ? "" : words[0].ToLowerInvariant();
        string rest = words.Length <= 1 ? "" : string.Join(' ', words[1..]);
        try
        {
            switch (verb)
            {
                case "" or "status": await StatusAsync(s); return;
                case "help": await ReplyAsync(s, Help); return;
                case "yes" or "accept": await AcceptAsync(s); return;
                case "no" or "decline": await DeclineAsync(s); return;
                case "add": await AddAsync(s, words[1..]); return;
                case "remove": await RemoveAsync(s, rest); return;
                case "clear": await ClearAsync(s); return;
                case "zeny": await ZenyAsync(s, rest); return;
                case "lock": await LockAsync(s); return;
                case "ok" or "confirm": await ConfirmAsync(s); return;
                case "cancel": await CancelAsync(s, "you cancelled the trade"); return;
                default: await OfferAsync(s, string.Join(' ', words)); return;
            }
        }
        catch (Exception e)
        {
            log.LogError(e, "trade command failed for role {Role}", role.Id);
            await ReplyAsync(s, "The trade hit an error and was closed.");
            Cancel(s.Role!.Id);
        }
    }

    private const string Help =
        "Trade: /trade <player> offers | /trade yes | /trade no. Put items up with /trade add <name or id> [count], "
        + "zeny with /trade zeny <amount>, drop one with /trade remove <name or id> and everything with /trade clear. "
        + "Both sides /trade lock, then /trade ok moves the items and the zeny; /trade cancel stops it. Items go into the "
        + "other bag directly. /trade shows the window.";

    private async Task OfferAsync(GameSession s, string name)
    {
        if (name.Length == 0) { await ReplyAsync(s, Help); return; }
        var target = Online(name);
        if (target == null) { await ReplyAsync(s, $"No player named {name} is online."); return; }
        if (target.Role!.Id == s.Role!.Id) { await ReplyAsync(s, "You cannot trade with yourself."); return; }
        if (InWindow(s.Role!.Id) is { } mine) { await ReplyAsync(s, $"You are already trading with {mine.Other(s.Role!.Id).Name}."); return; }
        if (InWindow(target.Role.Id) is { } theirs) { await ReplyAsync(s, $"{target.Role.Name} is already trading with {theirs.Other(target.Role.Id).Name}."); return; }
        var window = new Window(new Side(s.Role!.Id, s.Role.Name), new Side(target.Role.Id, target.Role.Name),
            DateTime.UtcNow + OfferWindow);
        lock (_gate) { _windows[s.Role!.Id] = window; _windows[target.Role.Id] = window; }
        await ReplyAsync(s, $"Trade offered to {target.Role.Name}. Waiting for /trade yes.");
        await ReplyAsync(target, $"{s.Role.Name} wants to trade with you: /trade yes to open, /trade no to decline "
            + $"(expires in {(int)OfferWindow.TotalSeconds} s).");
    }

    private async Task AcceptAsync(GameSession s)
    {
        var window = InWindow(s.Role!.Id);
        if (window == null) { await ReplyAsync(s, "No trade is waiting for you."); return; }
        if (window.ExpiresUtc <= DateTime.UtcNow && window.A.Empty && window.B.Empty)
        {
            Close(window);
            await ReplyAsync(s, "That trade offer expired.");
            return;
        }
        window.ExpiresUtc = DateTime.UtcNow + TradeWindow;
        await ReplyAsync(s, $"Trading with {window.Other(s.Role!.Id).Name}. " + Help);
        if (Online(window.Other(s.Role!.Id).RoleId) is { } other) await ReplyAsync(other, $"Trading with {s.Role.Name}. " + Help);
    }

    private async Task DeclineAsync(GameSession s)
    {
        var window = InWindow(s.Role!.Id);
        if (window == null) { await ReplyAsync(s, "No trade is waiting for you."); return; }
        Close(window);
        await ReplyAsync(s, "Trade declined.");
        if (Online(window.Other(s.Role!.Id).RoleId) is { } other) await ReplyAsync(other, $"{s.Role.Name} declined the trade.");
    }

    private async Task AddAsync(GameSession s, string[] words)
    {
        var (window, me) = await OpenAsync(s);
        if (window == null) return;
        if (words.Length == 0) { await ReplyAsync(s, "Usage: /trade add <name or item id> [count]"); return; }
        int count = 1;
        string query = string.Join(' ', words);
        if (words.Length >= 2 && int.TryParse(words[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCount))
        {
            count = parsedCount;
            query = string.Join(' ', words[..^1]);
        }
        if (count <= 0) { await ReplyAsync(s, "The count must be at least 1."); return; }
        var matches = FindInBag(s, query);
        if (matches.Count == 0) { await ReplyAsync(s, $"You have no \"{query}\" in the bag."); return; }
        if (matches.Count > 1)
        {
            await ReplyAsync(s, "\"" + query + "\" matches: "
                + string.Join(", ", matches.Take(6).Select(m => $"{m.Name} ({m.Entry}, {m.Count} held)"))
                + ". Type /trade add <item id> <count>.");
            return;
        }
        if (me.Offers.Count >= MaxOffers) { await ReplyAsync(s, $"A trade holds at most {MaxOffers} stacks."); return; }
        var picked = await TakeFrom(s, me, matches[0].Entry, count);
        if (picked == null) return;
        me.Changed();
        await ReplyAsync(s, $"You offer {count}x {picked}.");
        await Echo(window);
    }

    private async Task RemoveAsync(GameSession s, string query)
    {
        var (window, me) = await OpenAsync(s);
        if (window == null) return;
        if (query.Length == 0) { await ReplyAsync(s, "Usage: /trade remove <name or item id>"); return; }
        var offers = me.Offers.Where(o => Matches(o.Name, o.Entry, query)).ToList();
        if (offers.Count == 0) { await ReplyAsync(s, $"You are not offering \"{query}\"."); return; }
        foreach (var offer in offers) me.Offers.Remove(offer);
        me.Changed();
        await ReplyAsync(s, "You take back " + string.Join(", ", offers.Select(o => $"{o.Count}x {o.Name}")) + ".");
        await Echo(window);
    }

    private async Task ClearAsync(GameSession s)
    {
        var (window, me) = await OpenAsync(s);
        if (window == null) return;
        me.Offers.Clear();
        me.Zeny = 0;
        me.Changed();
        await ReplyAsync(s, "Your side of the trade is empty.");
        await Echo(window);
    }

    private async Task ZenyAsync(GameSession s, string text)
    {
        var (window, me) = await OpenAsync(s);
        if (window == null) return;
        if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount)
            || amount < 0 || amount > MaxZeny)
        {
            await ReplyAsync(s, $"Usage: /trade zeny <0-{MaxZeny}>");
            return;
        }
        long held = s.Progress?.Currency.GetValueOrDefault(Zeny) ?? 0;
        if (amount > held) { await ReplyAsync(s, $"You hold {held} zeny."); return; }
        me.Zeny = amount;
        me.Changed();
        await ReplyAsync(s, amount == 0 ? "You no longer offer zeny." : $"You offer {amount} zeny.");
        await Echo(window);
    }

    private async Task LockAsync(GameSession s)
    {
        var (window, me) = await OpenAsync(s);
        if (window == null) return;
        if (me.Empty) { await ReplyAsync(s, "Put something up first: /trade add or /trade zeny."); return; }
        me.Locked = true;
        await ReplyAsync(s, "Your side is locked. /trade ok when the window looks right.");
        if (Online(window.Other(s.Role!.Id).RoleId) is { } other)
            await ReplyAsync(other, $"{s.Role!.Name} locked {(window.Of(s.Role!.Id).Empty ? "an empty side" : "their side")}.");
    }

    private async Task ConfirmAsync(GameSession s)
    {
        var (window, me) = await OpenAsync(s);
        if (window == null) return;
        if (!me.Locked) { await ReplyAsync(s, "Lock your side first: /trade lock."); return; }
        if (!window.Other(s.Role!.Id).Locked) { await ReplyAsync(s, "Waiting for the other player to lock their side."); return; }
        me.Confirmed = true;
        var otherSide = window.Other(s.Role!.Id);
        if (!otherSide.Confirmed)
        {
            await ReplyAsync(s, "Confirmed. Waiting for the other player to /trade ok.");
            if (Online(otherSide.RoleId) is { } other) await ReplyAsync(other,
                $"{s.Role!.Name} confirmed. /trade ok to finish the trade.");
            return;
        }
        // A chat line runs inside its own reservation; the swap covers both roles and needs the realm scope, which a
        // nested operation may not take. Queue it as the next loop operation: it starts clean and reports by chat.
        loop.Post(() => CommitAsync(s, window));
    }

    private async Task CancelAsync(GameSession s, string reason)
    {
        var window = InWindow(s.Role!.Id);
        if (window == null) { await ReplyAsync(s, "No trade is open."); return; }
        Close(window);
        await ReplyAsync(s, reason);
        if (Online(window.Other(s.Role!.Id).RoleId) is { } other) await ReplyAsync(other, $"{s.Role.Name} cancelled the trade.");
    }

    private async Task StatusAsync(GameSession s)
    {
        var window = InWindow(s.Role!.Id);
        if (window == null) { await ReplyAsync(s, "No trade is open. /trade <player> to offer one."); return; }
        var me = window.Of(s.Role!.Id);
        var other = window.Other(s.Role!.Id);
        await ReplyAsync(s, $"Trade with {other.Name}:\n"
            + $"You: {Describe(me)}\n{other.Name}: {Describe(other)}\n"
            + (me.Locked ? "Your side is locked." : "Your side is open.") + " "
            + (me.Confirmed ? "You confirmed." : "You have not confirmed."));
    }

    private static string Describe(Side side) =>
        (side.Offers.Count == 0 && side.Zeny == 0
            ? "(nothing)"
            : string.Join(", ", side.Offers.Select(o => $"{o.Count}x {o.Name}").Concat(side.Zeny > 0 ? [$"{side.Zeny} zeny"] : [])))
        + (side.Locked ? " [locked]" : "") + (side.Confirmed ? " [ok]" : "");

    // ---------------------------------------------------------------- commit

    private async Task CommitAsync(GameSession requester, Window window)
    {
        var a = window.A;
        var b = window.B;
        if (Online(b.RoleId) is not { } target)
        {
            Close(window);
            await ReplyAsync(requester, $"{b.Name} is no longer online; the trade is cancelled.");
            return;
        }
        if (requester.Dead || target.Dead) { await CancelAsync(requester, "A dead player cannot trade."); return; }
        var requesterSide = window.Of(requester.Role!.Id);
        var otherSide = window.Other(requester.Role.Id);
        int code = await market.RunAsync(requester, tx =>
        {
            // Both bags are read through the transaction: the check and the move see the same rows.
            var mine = Part(tx, requesterSide.RoleId);
            var theirs = Part(tx, otherSide.RoleId);
            if (!CanPay(mine, requesterSide) || !CanPay(theirs, otherSide)) return ErrorCodes.ItemsShort;
            var fromMine = Take(mine, requesterSide.Offers);
            if (fromMine == null) return ErrorCodes.ItemMissing;
            var fromTheirs = Take(theirs, otherSide.Offers);
            if (fromTheirs == null) return ErrorCodes.ItemMissing;
            // Bag room for the incoming stacks, checked before anything is staged as moved.
            // Delivery is the canonical item handover (market, stalls and friend gifts mail their goods): the
            // sender's stacks leave the bag and the receiver's arrive in the mailbox, all in this one commit.
            // Both purses move with the goods, in the same transaction as the bag writes.
            if (requesterSide.Zeny > 0)
            {
                mine.Currency[Zeny] = mine.Currency.GetValueOrDefault(Zeny) - requesterSide.Zeny;
                theirs.Currency[Zeny] = theirs.Currency.GetValueOrDefault(Zeny) + requesterSide.Zeny;
            }
            if (otherSide.Zeny > 0)
            {
                theirs.Currency[Zeny] = theirs.Currency.GetValueOrDefault(Zeny) - otherSide.Zeny;
                mine.Currency[Zeny] = mine.Currency.GetValueOrDefault(Zeny) + otherSide.Zeny;
            }
            if (fromTheirs.Count > 0) tx.DeliverMail(requesterSide.RoleId, TradeMail, fromTheirs);
            if (fromMine.Count > 0) tx.DeliverMail(otherSide.RoleId, TradeMail, fromMine);
            return 0;
        }, afterCommit: _ => Task.CompletedTask);
        log.LogInformation("trade {A}-{B}: result {Code}", a.RoleId, b.RoleId, code);
        Close(window);
        if (code != 0)
        {
            string why = code switch
            {
                ErrorCodes.ItemMissing => "an offered item left a bag before the trade was confirmed",
                ErrorCodes.ItemsShort => "a purse no longer holds the offered zeny",
                ErrorCodes.BagFull => "a bag has no room for the incoming items",
                _ => $"result {code}",
            };
            await ReplyAsync(requester, $"Trade cancelled: {why}.");
            if (Online(otherSide.RoleId) is { } still) await ReplyAsync(still, $"Trade cancelled: {why}.");
            return;
        }
        await ReplyAsync(requester, "Trade done: " + Describe(requesterSide) + " out, " + Describe(otherSide) + " in.");
        if (Online(otherSide.RoleId) is { } receiver)
            await ReplyAsync(receiver, "Trade done: " + Describe(otherSide) + " out, " + Describe(requesterSide) + " in.");
    }

    /// <summary>The participant, or null when the role is not readable in this transaction.</summary>
    private static MarketParticipant Part(MarketTransaction tx, long roleId) => tx.Role(roleId);

    private static bool CanPay(MarketParticipant participant, Side side) =>
        side.Zeny == 0 || participant.Currency.GetValueOrDefault(Zeny) >= side.Zeny;

    private static void Pay(MarketParticipant participant, Side side)
    {
        if (side.Zeny == 0) return;
        participant.Currency[Zeny] = participant.Currency.GetValueOrDefault(Zeny) - side.Zeny;
    }

    /// <summary>The offered instances, taken out of the bag; null when one of them is no longer there in that count.</summary>
    private static List<InvItem>? Take(MarketParticipant participant, IReadOnlyList<Offer> offers)
    {
        var taken = new List<InvItem>();
        foreach (var offer in offers)
        {
            // The offered instance first; a bag rebuilt since the offer (a relog, a market publication) is matched
            // by entry instead, so a trade is not refused for an instance id the client never sees.
            var item = participant.Items.FirstOrDefault(i => i.Guid == offer.Guid && i.Stack >= offer.Count)
                ?? participant.Items.FirstOrDefault(i => i.Entry == offer.Entry && i.Stack >= offer.Count);
            if (item == null) return null;
            var moved = item.Clone();
            moved.Stack = offer.Count;
            taken.Add(moved);
            item.Stack -= offer.Count;
            if (item.Stack == 0) participant.Items.Remove(item);
        }
        return taken;
    }

    private int GrantError(GameSession receiver, List<InvItem> incoming) =>
        incoming.Count == 0 ? 0 : character.PlainGrantError(receiver, [], incoming);

    // ---------------------------------------------------------------- helpers

    /// <summary>The window the caller is in, or null after telling them so.</summary>
    private async Task<(Window? Window, Side Me)> OpenAsync(GameSession s)
    {
        var window = InWindow(s.Role!.Id);
        if (window == null)
        {
            await ReplyAsync(s, "No trade is open. /trade <player> to offer one.");
            return (null, null!);
        }
        if (window.ExpiresUtc <= DateTime.UtcNow)
        {
            Close(window);
            await ReplyAsync(s, "The trade expired.");
            return (null, null!);
        }
        window.ExpiresUtc = DateTime.UtcNow + TradeWindow;
        return (window, window.Of(s.Role!.Id));
    }

    /// <summary>The caller's bag, grouped by entry, for /trade add lookups.</summary>
    private List<(int Entry, string Name, long Count)> FindInBag(GameSession s, string query)
    {
        var bag = s.Items.Where(i => i.Grid == 0).ToList();
        bool Hit(InvItem i) => Matches(data.ItemName(i.Entry), i.Entry, query);
        return [.. bag.Where(Hit)
            .GroupBy(i => i.Entry)
            .Select(g => (Entry: g.Key, Name: data.ItemName(g.Key), Count: g.Sum(i => (long)i.Stack)))
            .OrderBy(m => m.Name)];
    }

    private static bool Matches(string name, int entry, string query) =>
        int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
            ? id == entry
            : name.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>Puts up <paramref name="count"/> of an entry from the bag, instancing the real stacks; null when it cannot.</summary>
    private async Task<string?> TakeFrom(GameSession s, Side me, int entry, int count)
    {
        var offered = me.Offers.Where(o => o.Entry == entry).Sum(o => o.Count);
        var instances = s.Items.Where(i => i.Grid == 0 && i.Entry == entry).ToList();
        long held = instances.Sum(i => (long)i.Stack);
        if (held - offered < count)
        {
            await ReplyAsync(s, $"You hold {held} {data.ItemName(entry)}{(offered > 0 ? $" ({offered} already offered)" : "")}.");
            return null;
        }
        int left = count;
        foreach (var item in instances)
        {
            if (left == 0) break;
            if (me.Offers.Any(o => o.Guid == item.Guid)) continue;
            if (trade.TransactionItemError(s, item) is var error and not 0)
            {
                await ReplyAsync(s, $"{data.ItemName(entry)} cannot be traded (code {error}).");
                return null;
            }
            int take = Math.Min(left, item.Stack);
            me.Offers.Add(new Offer(item.Guid, entry, data.ItemName(entry), take));
            left -= take;
        }
        return left == 0 ? data.ItemName(entry) : null;
    }

    private async Task Echo(Window window)
    {
        foreach (var side in new[] { window.A, window.B })
            if (Online(side.RoleId) is { } session)
                await ReplyAsync(session, $"Trade window - {side.Name}: {Describe(side)}");
    }

    private GameSession? Online(string name) =>
        sessions.All().FirstOrDefault(x => x.Role != null
            && string.Equals(x.Role.Name, name, StringComparison.OrdinalIgnoreCase));

    private GameSession? Online(long roleId) =>
        sessions.All().FirstOrDefault(x => x.Role?.Id == roleId);

    private Window? InWindow(long roleId)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(roleId, out var window)) return null;
            if (Online(window.A.RoleId) == null || Online(window.B.RoleId) == null)
            {
                _windows.Remove(window.A.RoleId);
                _windows.Remove(window.B.RoleId);
                return null;
            }
            return window;
        }
    }

    private void Close(Window window)
    {
        lock (_gate)
        {
            if (_windows.TryGetValue(window.A.RoleId, out var current) && ReferenceEquals(current, window))
                _windows.Remove(window.A.RoleId);
            if (_windows.TryGetValue(window.B.RoleId, out var peer) && ReferenceEquals(peer, window))
                _windows.Remove(window.B.RoleId);
        }
    }

    private void Cancel(long roleId)
    {
        if (InWindow(roleId) is { } window) Close(window);
    }

    private static Task ReplyAsync(GameSession session, string text) => GmCommands.ReplyAsync(session, text);
}