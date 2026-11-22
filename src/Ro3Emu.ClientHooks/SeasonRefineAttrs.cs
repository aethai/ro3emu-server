using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// RefiningSeasonAttrConfig rows 2 and 12 name each other's stats in the shipped client:
    /// <list type="bullet">
    /// <item>2 "Basic Defense" (12470000001): _kAttrs 103/104 (PATK, MATK) x20 - the defense group granted attack;</item>
    /// <item>12 "Advanced Attack" (12470000004): _kAttrs 105/106 (PDEF, MDEF) x80 - the attack group granted defense.</item>
    /// </list>
    /// The server applies the corrected stats (data\config\RefiningSeasonAttrConfig.json: Basic Defense PDEF/MDEF 20,
    /// Advanced Attack PATK/MATK 80), so the strengthen panel and the gear tooltip have to show the same. Every reader of
    /// a row goes through Lua_CFG_Base:GetEntryPtr (Lua_CFG_RefiningSeasonAttrConfig.GetName, GetIconPath and
    /// GetAttrGroup call self:GetEntryPtr, UI_Sys_StrengthenRefine_PropertyWidgetCtrl and
    /// UI_Com_CardEquipTipWidgetCtrl call it directly), so the wrapper rewrites the attribute ids of those two rows'
    /// _kAttrs in place and leaves their values and every other row alone.
    /// </summary>
    public static class SeasonRefineAttrs
    {
        /// <summary>GetEntryPtr(itemId) -> the row table.</summary>
        private delegate object RowLookup(object self, object itemId);

        // Kept alive here: Lua holds the pushed delegate only weakly through xLua's object pool.
        private static readonly RowLookup Wrapper = RowOf;

        /// <summary>Row _iID 2 (Basic Defense) and 12 (Advanced Attack), and the attribute id their first _kAttrs entry
        /// should carry: 105 PDEF (then 106 MDEF) and 103 PATK (then 104 MATK).</summary>
        private const long BasicDefenseAttr = 105, AdvancedAttackAttr = 103;

        private static Obf_R.Obf_Ef _get;
        private static readonly System.Collections.Generic.HashSet<string> Reported = new System.Collections.Generic.HashSet<string>();

        /// <summary>From LuaPatches.Tick, once per LuaEnv, with the config class (require "lua_cfg_refiningseasonattrconfig").</summary>
        internal static void Install(Obf_R.Obf_ff config)
        {
            _get = null;
            Reported.Clear();
            try
            {
                var get = config?.Obf_RuA<Obf_R.Obf_Ef>("GetEntryPtr");
                if (get == null)
                {
                    Debug.LogWarning("[Ro3Emu.SeasonRefine] Lua_CFG_RefiningSeasonAttrConfig.GetEntryPtr not found; " +
                        "Basic Defense and Advanced Attack keep the client's swapped stats");
                    return;
                }
                _get = get;
                config.Obf_luA<string, RowLookup>("GetEntryPtr", Wrapper);
                Debug.Log("[Ro3Emu.SeasonRefine] Basic Defense shows PDEF/MDEF and Advanced Attack PATK/MATK");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.SeasonRefine] not wrapped (" + e.GetType().Name + ": " + e.Message + ")");
            }
        }

        /// <summary>The row as the client reads it, with _kAttrs corrected while it still carries the other group's ids.</summary>
        private static object RowOf(object self, object itemId)
        {
            var result = _get?.Obf_HuA(self, itemId);
            object row = result != null && result.Length > 0 ? result[0] : null;
            try
            {
                if (row is Obf_R.Obf_ff table) Correct(table);
            }
            catch (Exception e) { Once(e.GetType().Name + ": " + e.Message); }
            return row;
        }

        private static void Correct(Obf_R.Obf_ff row)
        {
            long first = FirstAttr(row);
            if (first == 0) return;
            var attrs = row.Obf_RuA<Obf_R.Obf_ff>("_kAttrs");
            if (attrs == null) return;
            bool changed = false;
            for (long i = 1; i <= 2; i++)
            {
                var pair = attrs.Obf_ruA<long, Obf_R.Obf_ff>(i);
                if (pair == null) break;
                long wanted = first + i - 1;
                if (LuaPatches.ToLong(pair.Obf_ruA<long, object>(1)) == wanted) continue;
                pair.Obf_luA<long, object>(1, wanted);
                changed = true;
            }
            if (changed)
                Once("row " + (first == BasicDefenseAttr ? "2 (Basic Defense)" : "12 (Advanced Attack)")
                    + " now shows attribute " + first + "/" + (first + 1) + ", the stats the server applies");
        }

        /// <summary>The attribute id the row's first _kAttrs entry should carry, or 0 for every other row.</summary>
        private static long FirstAttr(Obf_R.Obf_ff row)
        {
            // _iSeasonPower: only the refining season rows have it; _iID names the group (2 Basic Defense, 12 Advanced Attack).
            if (row.Obf_RuA<object>("_iSeasonPower") == null) return 0;
            long id = LuaPatches.ToLong(row.Obf_RuA<object>("_iID"));
            return id == 2 ? BasicDefenseAttr : id == 12 ? AdvancedAttackAttr : 0;
        }

        private static void Once(string message)
        {
            if (Reported.Add(message)) Debug.Log("[Ro3Emu.SeasonRefine] " + message);
        }
    }
}
