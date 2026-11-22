using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The shop's buy window only prints the total ("Total: 2,380"), so a player who holds
    /// 50 Sacred Relic Crystals cannot see what is missing before pressing Buy. The cart
    /// panel's Lua (<c>UI_Sys_AppearanceShopCart_PanelCtrl:_calcTotalPrice</c>) is wrapped
    /// here: the original runs first (it writes the total and the currency icons), then the
    /// total label gets a small second line with the balance, the shortfall and the CR a
    /// top-up on the website costs.
    ///
    /// Everything is read through the Lua objects the panel already holds:
    /// <c>m_kCartListData[i].m_kCFG._kItemPerPrice</c> = [currency, unit price] and
    /// <c>.m_iCount</c>; the balance is <c>LC_ItemHelper.GetItemCount(currency)</c>. Only
    /// the selected entries count, exactly like the total.
    ///
    /// The wrapper never blocks the original: a failure leaves the window as it was and is
    /// logged once.
    /// </summary>
    internal static class ShopCartInfo
    {
        /// <summary>
        /// The website's Sacred Relic Crystal top-up (website shop_item entry 3): 50 crystals
        /// for 100 CR, so one crystal costs 2 CR. Keep in sync with Admin -> Catalogue.
        /// </summary>
        private const long CrystalEntry = 3, CrystalsPerPackage = 50, CreditsPerPackage = 100;

        /// <summary>Marks our own suffix, so a second run of the wrapper cannot double it.</summary>
        private const string Marker = "\u200B";

        /// <summary>
        /// Build stamp the launcher looks for in the installed DLL (player\PlayerConfig.cs, HookMarker) to tell
        /// whether the game folder carries this build. Bump it whenever this hook changes.
        /// </summary>
        private const string Version = "Ro3Emu.ShopCart v6";

        private static Obf_R.Obf_ff _global;
        private static Obf_R.Obf_ff _itemHelper;
        private static bool _installed;
        private static bool _detailed;
        private static int _idle;
        private static readonly List<LuaPatches.SelfMethod> Kept = new List<LuaPatches.SelfMethod>();
        private static readonly HashSet<string> Reported = new HashSet<string>();

        /// <summary>
        /// Called from the per-frame pump: wraps <c>_calcTotalPrice</c> on whichever already-loaded module has one.
        /// The panel's module is not loaded under a name we can guess (the client refuses to load
        /// <c>ui_sys_appearanceshopcart_panlectrl</c> from its recovery payload: "reconstructed_module_not_admitted"),
        /// so nothing is required by name — the loaded modules are scanned for the method instead. Cheap once wrapped.
        /// </summary>
        internal static void TryInstall(Obf_R.Obf_ff global)
        {
            if (_installed || global == null) return;
            _global = global;
            try
            {
                if (Wrap(global, global.Obf_LuA<Obf_R.Obf_ff>("package.loaded"), "package.loaded")) return;
                Wrap(global, global, "_G");
            }
            catch (Exception e)
            {
                Once("scan: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Wraps every table in <paramref name="table"/> that carries a _calcTotalPrice function.</summary>
        private static bool Wrap(Obf_R.Obf_ff global, Obf_R.Obf_ff table, string where)
        {
            var next = global.Obf_ruA<string, object>("next") as Obf_R.Obf_Ef;
            if (next == null || table == null) return false;
            bool wrapped = false;
            object key = null;
            for (int guard = 0; guard < 8000; guard++)
            {
                object[] pair;
                try { pair = key == null ? next.Obf_HuA(table) : next.Obf_HuA(table, key); }
                catch (Exception e) { Once("next(" + where + "): " + e.GetType().Name + ": " + e.Message); break; }
                if (pair == null || pair.Length < 2 || pair[0] == null) break;
                key = pair[0];
                if (!(pair[1] is Obf_R.Obf_ff value)) continue;
                object candidate;
                try { candidate = value.Obf_ruA<string, object>("_calcTotalPrice"); }
                catch (Exception) { continue; }
                if (!(candidate is Obf_R.Obf_Ef original)) continue;

                // A fresh delegate per table, kept alive here: xLua holds pushed delegates only weakly.
                LuaPatches.SelfMethod wrapper = self => Wrapped(original, self);
                Kept.Add(wrapper);
                try { value.Obf_luA<string, LuaPatches.SelfMethod>("_calcTotalPrice", wrapper); }
                catch (Exception e) { Once("wrap " + where + ": " + e.GetType().Name + ": " + e.Message); continue; }
                wrapped = true;
                Debug.Log("[Ro3Emu.ShopCart] " + Version + ": wrapped _calcTotalPrice in " + where + "['"
                    + (key as string ?? key.ToString()) + "']");
            }
            if (wrapped) _installed = true;
            return wrapped;
        }

        /// <summary>The same retry on every frame, reusing the Lua environment found by the first call.</summary>
        internal static void Retry()
        {
            if (_installed || _global == null) return;
            if (++_idle % 20 != 0) return; // scanning package.loaded is cheap but not free: about twice a second
            TryInstall(_global);
        }

        private static void Wrapped(Obf_R.Obf_Ef original, Obf_R.Obf_ff self)
        {
            try { original?.Obf_HuA(self); }
            catch (Exception e) { Once("original _calcTotalPrice: " + e.GetType().Name + ": " + e.Message); }
            try { Annotate(self); }
            catch (Exception e) { Once("annotate: " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Annotate(Obf_R.Obf_ff self)
        {
            object raw;
            try { raw = self.Obf_LuA<object>("View.m_kTTXT_Total"); }
            catch (Exception e) { Once("View.m_kTTXT_Total: " + e.GetType().Name + ": " + e.Message); return; }
            if (!(raw is TMP_Text label))
            {
                Once("no View.m_kTTXT_Total label on the cart panel (" + (raw == null ? "null" : raw.GetType().Name) + ")");
                return;
            }

            string total = label.text ?? "";
            if (total.Contains(Marker, StringComparison.Ordinal)) return;

            var lines = Shortages(self, total);
            if (!_detailed)
            {
                _detailed = true;
                Debug.Log("[Ro3Emu.ShopCart] label '" + label.name + "' text='"
                    + Shorten(total) + "' richText=" + label.richText + " size=" + label.rectTransform.rect.width.ToString("F0")
                    + "x" + label.rectTransform.rect.height.ToString("F0") + " lines=" + lines.Count);
            }
            if (lines.Count == 0) return;

            // Rich text when the label allows it (smaller, muted), plain otherwise: a tag would otherwise be shown.
            string suffix = label.richText
                ? "<size=74%><color=#8aa2c4> " + lines[0] + "</color></size>"
                : "   " + lines[0];
            label.text = total + suffix + Marker;
            Log("buy window: " + Shorten(total) + " -> " + Shorten(label.text));
        }

        /// <summary>
        /// One "Balance 150 · 1,730 more needed (≈ 3,460 CR)" line per currency the cart is short of.
        /// <paramref name="totalText"/> is what the panel wrote; it is only used by the fallback below.
        /// </summary>
        private static List<string> Shortages(Obf_R.Obf_ff self, string totalText)
        {
            var result = new List<string>();
            var items = self.Obf_RuA<Obf_R.Obf_ff>("m_kCartListData");
            int length = items == null ? 0 : items.Obf_b;

            var totals = new Dictionary<long, long>();
            for (int i = 1; i <= length; i++)
            {
                if (!(items.Obf_ruA<int, object>(i) is Obf_R.Obf_ff entry)) continue;
                // A row is the list entry (selection flags) around the cart item data (its config and count);
                // the shapes seen in the panel's Lua are entry.m_kCartItem.m_kCFG / .m_iCount, with the flags on
                // the entry. Both are read permissively so a layout change only loses the line, never the window.
                var data = entry.Obf_RuA<object>("m_kCartItem") as Obf_R.Obf_ff ?? entry;
                // Manage mode selects with m_bSelect, the buy flow with m_bBuySelect.
                bool counted = Flag(entry, "m_bBuySelect") || Flag(entry, "m_bSelect")
                    || Flag(data, "m_bBuySelect") || Flag(data, "m_bSelect");
                if (!counted) continue;
                if (!(data.Obf_RuA<object>("m_kCFG") is Obf_R.Obf_ff cfg)) continue;
                if (!(cfg.Obf_RuA<object>("_kItemPerPrice") is Obf_R.Obf_ff price)) continue;
                long currency = LuaPatches.ToLong(price.Obf_ruA<int, object>(1));
                long unit = LuaPatches.ToLong(price.Obf_ruA<int, object>(2));
                long count = LuaPatches.ToLong(data.Obf_RuA<object>("m_iCount"));
                if (count <= 0 && !ReferenceEquals(data, entry)) count = LuaPatches.ToLong(entry.Obf_RuA<object>("m_iCount"));
                if (currency <= 0 || unit <= 0 || count <= 0) continue;
                long discount = LuaPatches.ToLong(cfg.Obf_RuA<object>("_iDiscount"));
                long unitPrice = discount > 0 ? (long)Math.Ceiling(unit * (discount / 10000.0)) : unit;
                totals[currency] = totals.TryGetValue(currency, out long running) ? running + unitPrice * count : unitPrice * count;
            }

            if (totals.Count == 0)
            {
                // The panel's rows could not be read on this client build: fall back to the currency its own
                // price icon shows, the Fashion shop's own crystal when even that fails, and the total it printed.
                long currency = CurrencyFromIcon(self);
                long printed = Digits(totalText);
                if (printed <= 0) { Log("the total label has no amount; the window is left as it was"); return result; }
                if (currency <= 0) currency = CrystalEntry;
                totals[currency] = printed;
                Log("cart totals unreadable, using the printed total " + printed.ToString("N0") + " in currency " + currency);
            }

            foreach (var pair in totals)
            {
                long held = Held(pair.Key);
                if (held < 0) { Log("balance of currency " + pair.Key + " is unknown to LC_ItemHelper"); continue; }
                Log("currency " + pair.Key + ": total " + pair.Value.ToString("N0") + ", held " + held.ToString("N0"));
                if (held >= pair.Value) continue;
                long missing = pair.Value - held;
                string topUp = pair.Key == CrystalEntry ? " (\u2248 " + Credits(missing).ToString("N0") + " CR)" : "";
                result.Add("Balance " + held.ToString("N0") + " \u00B7 need " + missing.ToString("N0") + " more" + topUp);
            }
            return result;
        }

        /// <summary>The currency a price icon shows: GetIconPath names the sprite "item_&lt;entry&gt;".</summary>
        private static long CurrencyFromIcon(Obf_R.Obf_ff self)
        {
            foreach (var name in new[] { "View.m_kIMG_Price01", "View.m_kIMG_Price02" })
            {
                if (!(self.Obf_LuA<object>(name) is UnityEngine.UI.Image image)) continue;
                string sprite = image.sprite == null ? "" : image.sprite.name;
                int at = sprite.IndexOf("item_", StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                long entry = Digits(sprite.Substring(at + 5));
                if (entry > 0) return entry;
            }
            return 0;
        }

        /// <summary>The first run of digits in a text (the panel prints "1,880").</summary>
        private static long Digits(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            long value = 0;
            bool started = false;
            foreach (char c in text)
            {
                if (c >= '0' && c <= '9') { value = value * 10 + (c - '0'); started = true; }
                else if (c == ',' || c == '.' || c == ' ') { if (started) break; }
                else if (started) break;
                else if (c == '<') { /* rich text tag */ }
            }
            return value;
        }

        /// <summary>CR a shortage costs at the website's rate (50 crystals = 100 CR, so 2 CR per crystal).</summary>
        private static long Credits(long crystals) => crystals * CreditsPerPackage / CrystalsPerPackage;

        /// <summary>LC_ItemHelper.GetItemCount(entry), or -1 when the helper cannot answer.</summary>
        private static long Held(long entry)
        {
            try
            {
                _itemHelper ??= _global?.Obf_RuA<Obf_R.Obf_ff>("LC_ItemHelper");
                var get = _itemHelper?.Obf_RuA<Obf_R.Obf_Ef>("GetItemCount");
                if (get == null) return -1;
                var answer = get.Obf_HuA(entry);
                if (answer == null || answer.Length == 0) return -1;
                long value = LuaPatches.ToLong(answer[0]);
                return value == long.MinValue ? -1 : value;
            }
            catch (Exception e)
            {
                Once("GetItemCount: " + e.GetType().Name + ": " + e.Message);
                return -1;
            }
        }

        private static bool Flag(Obf_R.Obf_ff table, string name) => table != null && ToBool(table.Obf_RuA<object>(name));

        private static bool ToBool(object value) => value is bool flag && flag
            || value is long number && number != 0
            || value is int integer && integer != 0;

        private static string Shorten(string text) => text == null ? "" : (text.Length > 80 ? text.Substring(0, 80) : text);

        private static void Once(string message) { if (Reported.Add(message)) Debug.LogWarning("[Ro3Emu.ShopCart] " + message); }

        private static void Log(string message) { if (Reported.Add(message)) Debug.Log("[Ro3Emu.ShopCart] " + message); }
    }
}
