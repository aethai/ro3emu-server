using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The server's chat channel cooldowns (Ro3Emu:ChatCooldowns, #632). The client keeps its own per-channel cooldown,
    /// ChatChannelConfig._iCD read through Lua_CFG_ChatChannelConfig.GetCD(channel) by LC_ChatHelper.SendMsg and the
    /// voice senders; the server never sees it. When the server sends "chatcd 2=0,3=1" over message 77012 at login,
    /// GetCD answers those channels (ChatChannelConfig _iID, seconds) with the server's value and every other channel
    /// with the client's own. The wrapper sits on the config class, as LevelCapTip's does (BaseClass's __newindex writes
    /// the shared vtbl, so the Lua_DBManager proxy reaches it). A logout clears the overrides, so another server starts
    /// with the client's cooldowns.
    /// </summary>
    public static class ChatCooldown
    {
        /// <summary>A config method called as cfg:Method(id) -> number or nil.</summary>
        public delegate object IdLookup(object self, object id);

        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly IdLookup CooldownWrapper = Cooldown;

        private static readonly Dictionary<long, long> Overrides = new Dictionary<long, long>();
        private static Obf_R.Obf_Ef _getCd;

        /// <summary>From LuaPatches.Tick, once per LuaEnv, with the config class (require "lua_cfg_chatchannelconfig").</summary>
        internal static void Install(Obf_R.Obf_ff config)
        {
            _getCd = null;
            try
            {
                var getCd = config?.Obf_RuA<Obf_R.Obf_Ef>("GetCD");
                if (getCd == null)
                {
                    Debug.LogWarning("[Ro3Emu.ChatCooldown] Lua_CFG_ChatChannelConfig.GetCD not found; chat cooldowns stay the client's");
                    return;
                }
                _getCd = getCd;
                config.Obf_luA<string, IdLookup>("GetCD", CooldownWrapper);
                Debug.Log("[Ro3Emu.ChatCooldown] installed (" + Overrides.Count + " channel override(s))");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.ChatCooldown] not wrapped (" + e.GetType().Name + ": " + e.Message + ")");
            }
        }

        /// <summary>From Diagnostics (message 77012): "2=0,3=1" sets channel 2 to 0 s and channel 3 to 1 s; "" clears.</summary>
        internal static void Command(string value)
        {
            Overrides.Clear();
            foreach (var pair in (value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=');
                if (kv.Length == 2 && long.TryParse(kv[0].Trim(), out long channel) && long.TryParse(kv[1].Trim(), out long seconds)
                    && seconds >= 0)
                    Overrides[channel] = seconds;
            }
            Debug.Log("[Ro3Emu.ChatCooldown] " + Overrides.Count + " channel override(s) from the server");
        }

        /// <summary>On logout: another server starts with the client's own cooldowns.</summary>
        internal static void ResetSession() => Overrides.Clear();

        private static object Cooldown(object self, object id)
        {
            if (Overrides.Count > 0 && Channel(id) is long channel && Overrides.TryGetValue(channel, out long seconds)) return seconds;
            var res = _getCd?.Obf_HuA(self, id);
            return res != null && res.Length > 0 ? res[0] : null;
        }

        private static long? Channel(object id)
        {
            switch (id)
            {
                case long l: return l;
                case int i: return i;
                case double d: return (long)d;
                default: return null;
            }
        }
    }
}
