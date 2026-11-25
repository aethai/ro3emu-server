using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Fixes the two swapped attribute rows in the client's RefiningSeasonAttrConfig table
    /// (Config/DataConfig/RefiningSeasonAttrConfig.lua):
    /// <list type="bullet">
    /// <item>row 2 ("Basic Defense", defense icon) lists PATK/MATK (103/104) +20 instead of PDEF/MDEF (105/106) +20;</item>
    /// <item>row 12 ("Advanced Attack", attack icon) lists PDEF/MDEF (105/106) +80 instead of PATK/MATK (103/104) +80.</item>
    /// </list>
    /// The server's SeasonRefineService.GroupAttributes applies the same correction so the stats shown on the
    /// Refine Ascension cards match what the server grants. Only rows still holding the swapped ids are touched.
    /// </summary>
    internal static class SeasonRefineAttrs
    {
        internal static void Install(Obf_R.Obf_ff config)
        {
            try
            {
                var values = config?.Obf_RuA<Obf_R.Obf_ff>("m_kValues");
                if (values == null)
                {
                    Debug.LogWarning("[Ro3Emu.SeasonRefineAttrs] refiningseasonattrconfig.m_kValues not found");
                    return;
                }
                int fixedRows = 0;
                if (SwapRow(values, 2L, 103L, 104L, 105L, 106L)) fixedRows++;
                if (SwapRow(values, 12L, 105L, 106L, 103L, 104L)) fixedRows++;
                Debug.Log("[Ro3Emu.SeasonRefineAttrs] fixed " + fixedRows + " Refine Ascension attribute row(s)");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.SeasonRefineAttrs] " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool SwapRow(Obf_R.Obf_ff values, long rowId, long oldFirst, long oldSecond, long newFirst, long newSecond)
        {
            var row = values.Obf_ruA<long, Obf_R.Obf_ff>(rowId);
            var attrs = row?.Obf_RuA<Obf_R.Obf_ff>("_kAttrs");
            if (attrs == null) return false;
            var first = attrs.Obf_ruA<long, Obf_R.Obf_ff>(1L);
            var second = attrs.Obf_ruA<long, Obf_R.Obf_ff>(2L);
            if (first == null || second == null) return false;
            if (LuaPatches.ToLong(first.Obf_ruA<long, object>(1L)) != oldFirst
                || LuaPatches.ToLong(second.Obf_ruA<long, object>(1L)) != oldSecond)
                return false;
            first.Obf_luA<long, object>(1L, newFirst);
            second.Obf_luA<long, object>(1L, newSecond);
            return true;
        }
    }
}
