using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Fixes a bug in the client's LC_TriggerAreaManager_AreaData._equal (LC_TriggerAreaManager.lua:30-38), which
    /// compares only self.m_iMapId == other.m_iMapId. Because LC_TriggerAreaManager._hasRegistered and Unregister
    /// compare AreaData entries with == (calling __eq -> _equal), every trigger area registered after the first on a
    /// map was silently dropped as a duplicate - preventing multi-area maps such as Prontera (10100) from firing
    /// cutscene triggers like 101050501 (plot 130061, quest 10105050) and 1010718001 (plot 130091, quest 10107190).
    /// Wrapping _hasRegistered patches AreaData._equal and instance metatables to compare mapId, areaId, reason and
    /// callback object, and checks membership with the same full comparison.
    /// </summary>
    public static class TriggerAreaFix
    {
        /// <summary>LC_TriggerAreaManager._hasRegistered(self, areaData).</summary>
        public delegate bool HasRegisteredMethod(Obf_R.Obf_ff self, Obf_R.Obf_ff data);

        /// <summary>LC_TriggerAreaManager_AreaData._equal(self, other).</summary>
        public delegate bool AreaEqualMethod(Obf_R.Obf_ff a, Obf_R.Obf_ff b);

        // Kept alive here: Lua holds pushed delegates only weakly through xLua's object pool.
        private static readonly HasRegisteredMethod HasRegisteredWrapper = OnHasRegistered;
        private static readonly AreaEqualMethod AreaEqualWrapper = AreasEqual;

        private static Obf_R.Obf_Ef _rawEqual, _getMetatable;
        private static bool _classPatched, _reported;

        internal static void Install(Obf_R.Obf_ff global, Obf_R.Obf_ff manager)
        {
            _rawEqual = global?.Obf_RuA<Obf_R.Obf_Ef>("rawequal");
            _getMetatable = global?.Obf_RuA<Obf_R.Obf_Ef>("getmetatable");
            _classPatched = _reported = false;
            try
            {
                if (manager == null || manager.Obf_RuA<Obf_R.Obf_Ef>("_hasRegistered") == null)
                {
                    Debug.LogWarning("[Ro3Emu.TriggerAreaFix] LC_TriggerAreaManager._hasRegistered not found");
                    return;
                }
                manager.Obf_luA<string, HasRegisteredMethod>("_hasRegistered", HasRegisteredWrapper);
                Debug.Log("[Ro3Emu.TriggerAreaFix] LC_TriggerAreaManager._hasRegistered wrapped");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.TriggerAreaFix] " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool OnHasRegistered(Obf_R.Obf_ff self, Obf_R.Obf_ff data)
        {
            if (self == null || data == null) return false;
            try
            {
                PatchAreaDataClass(data);
                PatchMetatable(data);
                long mapId = LuaPatches.ToLong(data.Obf_RuA<object>("m_iMapId"));
                var byMap = self.Obf_RuA<Obf_R.Obf_ff>("m_kAreaDataList");
                var list = byMap?.Obf_ruA<long, Obf_R.Obf_ff>(mapId);
                if (list == null) return false;
                for (long i = 1; ; i++)
                {
                    var existing = list.Obf_ruA<long, Obf_R.Obf_ff>(i);
                    if (existing == null) return false;
                    PatchMetatable(existing);
                    if (AreasEqual(existing, data)) return true;
                }
            }
            catch (Exception e)
            {
                Once(e);
                return false;
            }
        }

        private static bool AreasEqual(Obf_R.Obf_ff a, Obf_R.Obf_ff b)
        {
            if (a == null || b == null) return a == null && b == null;
            try
            {
                return LuaPatches.ToLong(a.Obf_RuA<object>("m_iMapId")) == LuaPatches.ToLong(b.Obf_RuA<object>("m_iMapId"))
                    && LuaPatches.ToLong(a.Obf_RuA<object>("m_iAreaId")) == LuaPatches.ToLong(b.Obf_RuA<object>("m_iAreaId"))
                    && SameValue(a.Obf_RuA<object>("m_kReason"), b.Obf_RuA<object>("m_kReason"))
                    && SameValue(a.Obf_RuA<object>("m_kObject"), b.Obf_RuA<object>("m_kObject"));
            }
            catch (Exception e)
            {
                Once(e);
                return false;
            }
        }

        private static bool SameValue(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a is string || a is long || a is int || a is double || a is float || a is bool)
                return Equals(a, b);
            if (_rawEqual != null)
            {
                var res = _rawEqual.Obf_HuA(a, b);
                if (res != null && res.Length > 0 && res[0] is bool eq) return eq;
            }
            return ReferenceEquals(a, b);
        }

        private static void PatchAreaDataClass(Obf_R.Obf_ff data)
        {
            if (_classPatched) return;
            var cls = data.Obf_RuA<Obf_R.Obf_ff>("class");
            if (cls == null) return;
            cls.Obf_luA<string, AreaEqualMethod>("_equal", AreaEqualWrapper);
            _classPatched = true;
        }

        private static void PatchMetatable(Obf_R.Obf_ff data)
        {
            if (_getMetatable == null) return;
            var res = _getMetatable.Obf_HuA(data);
            if (res != null && res.Length > 0 && res[0] is Obf_R.Obf_ff mt)
                mt.Obf_luA<string, AreaEqualMethod>("__eq", AreaEqualWrapper);
        }

        private static void Once(Exception e)
        {
            if (_reported) return;
            _reported = true;
            Debug.LogWarning("[Ro3Emu.TriggerAreaFix] " + e.GetType().Name + ": " + e.Message);
        }
    }
}
