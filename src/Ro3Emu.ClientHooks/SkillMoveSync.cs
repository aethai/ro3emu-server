using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Keeps the local movement controller's logical position in sync with server-driven skill displacements (31112
    /// SkillStartMoveNotify). LC_PerformEffect_Agent.LateUpdate moves the unit's transform and writes the legacy
    /// m_kMovement.m_vNowPos field, but LC_Movement_Smooth_Local stores its position in m_kMovementPosition
    /// (AutoSmoothPosition). Because m_kMovementPosition stayed at the leap's start, casting the next skill snapped
    /// the character back to where Twilight or Fearless Charge began until WASD updated the movement controller.
    /// Wrapping LateUpdate and _forceSyncPosition on LC_PerformEffect_Agent calls m_kMovement:SetRawPosition(x, y, z)
    /// during and at the end of a skill move so the movement controller stays at the landing.
    /// </summary>
    public static class SkillMoveSync
    {
        /// <summary>LC_PerformEffect_Agent.LateUpdate(self, dt, unscaledDt, clientTime, moveFlag, animFlag).</summary>
        public delegate void LateUpdateMethod(Obf_R.Obf_ff self, object a1, object a2, object a3, object a4, object a5);

        /// <summary>LC_PerformEffect_Agent._forceSyncPosition(self, pos, dir).</summary>
        public delegate void ForceSyncMethod(Obf_R.Obf_ff self, object pos, object dir);

        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly LateUpdateMethod LateUpdateWrapper = OnLateUpdate;
        private static readonly ForceSyncMethod ForceSyncWrapper = OnForceSyncPosition;

        private static Obf_R.Obf_Ef _lateUpdate, _forceSync;
        private static bool _reported;

        internal static void Install(Obf_R.Obf_ff agent)
        {
            _lateUpdate = _forceSync = null;
            _reported = false;
            try
            {
                var lateUpdate = agent?.Obf_RuA<Obf_R.Obf_Ef>("LateUpdate");
                var forceSync = agent?.Obf_RuA<Obf_R.Obf_Ef>("_forceSyncPosition");
                if (lateUpdate != null)
                {
                    _lateUpdate = lateUpdate;
                    agent.Obf_luA<string, LateUpdateMethod>("LateUpdate", LateUpdateWrapper);
                }
                if (forceSync != null)
                {
                    _forceSync = forceSync;
                    agent.Obf_luA<string, ForceSyncMethod>("_forceSyncPosition", ForceSyncWrapper);
                }
                Debug.Log("[Ro3Emu.SkillMoveSync] LC_PerformEffect_Agent LateUpdate "
                    + (_lateUpdate != null ? "wrapped" : "missing")
                    + ", _forceSyncPosition " + (_forceSync != null ? "wrapped" : "missing"));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.SkillMoveSync] " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void OnLateUpdate(Obf_R.Obf_ff self, object a1, object a2, object a3, object a4, object a5)
        {
            _lateUpdate?.Obf_HuA(self, a1, a2, a3, a4, a5);

            if (self == null || !(a4 is bool moveFlag && moveFlag)) return;
            try
            {
                SyncMovement(self, self.Obf_RuA<object>("m_kMoveCurPos"));
            }
            catch (Exception e) { Once(e); }
        }

        private static void OnForceSyncPosition(Obf_R.Obf_ff self, object pos, object dir)
        {
            _forceSync?.Obf_HuA(self, pos, dir);
            if (self == null || pos == null) return;
            try { SyncMovement(self, pos); }
            catch (Exception e) { Once(e); }
        }

        private static void SyncMovement(Obf_R.Obf_ff agent, object pos)
        {
            if (!TryCoords(pos, out double x, out double y, out double z)) return;
            var unit = agent.Obf_RuA<Obf_R.Obf_ff>("m_kUnit");
            var movement = unit?.Obf_RuA<Obf_R.Obf_ff>("m_kMovement");
            var setRaw = movement?.Obf_RuA<Obf_R.Obf_Ef>("SetRawPosition");
            setRaw?.Obf_HuA(movement, x, y, z);
        }

        private static bool TryCoords(object pos, out double x, out double y, out double z)
        {
            if (pos is Vector3 v)
            {
                x = v.x; y = v.y; z = v.z;
                return !double.IsNaN(x) && !double.IsNaN(y) && !double.IsNaN(z);
            }
            if (pos is Obf_R.Obf_ff t)
            {
                if (TryDouble(t.Obf_RuA<object>("x"), out x)
                    && TryDouble(t.Obf_RuA<object>("y"), out y)
                    && TryDouble(t.Obf_RuA<object>("z"), out z))
                    return true;
            }
            x = y = z = 0;
            return false;
        }

        private static bool TryDouble(object value, out double result)
        {
            switch (value)
            {
                case double d when !double.IsNaN(d) && !double.IsInfinity(d): result = d; return true;
                case float f when !float.IsNaN(f) && !float.IsInfinity(f): result = f; return true;
                case long l: result = l; return true;
                case int i: result = i; return true;
                default: result = 0; return false;
            }
        }

        private static void Once(Exception e)
        {
            if (_reported) return;
            _reported = true;
            Debug.LogWarning("[Ro3Emu.SkillMoveSync] " + e.GetType().Name + ": " + e.Message);
        }
    }
}
