using System;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The Title rank panel's three stat rows (HP, PATK, MATK) are drawn from TitleConfig._kAttr, and this client's export
    /// zeroes that list in every rank ("HP +0 / PATK +0 / MATK +0"). The server grants a real bonus per rank
    /// (TitleService.Attributes: HP = _iPowerAdd x TitleHpScale, PATK = MATK = _iPowerAdd x TitleAttackScale), so this
    /// patch replaces each rank row's _kAttr with the same numbers and the panel shows what the character actually gets.
    /// <para>
    /// The rows are the ones every reader of TitleConfig gets: Lua_DBManager's m_kCFG_TitleConfig, whose m_kValues holds
    /// the decoded rows (PassChallenges fills its own tables the same way) and whose GetTitleConfig(id) is what
    /// LC_DAL_Title calls. Both are tried, and the row is written with rawset so the write lands ahead of the lazy
    /// decoder. The numbers are hard-coded here because the client cannot read the server's options: keep
    /// <see cref="HpScale"/> and <see cref="AttackScale"/> equal to Ro3Emu:Title:HpScale (2) and
    /// Ro3Emu:Title:AttackScale (0.05).
    /// </para>
    /// </summary>
    internal static class TitleStats
    {
        /// <summary>TitleConfig _iPowerAdd of ranks 1..21 (Beginner .. Soaring Sky V), in rank order.</summary>
        private static readonly long[] Power = [200, 400, 800, 1200, 1600, 2000, 2400, 2800, 3200, 3600, 4200, 4800,
            5400, 6000, 6600, 7200, 7800, 8400, 9000, 9600, 10200];

        private const long HpScale = 2;             // Ro3Emu:Title:HpScale
        private const double AttackScale = 0.05;    // Ro3Emu:Title:AttackScale
        private const int AttrHp = 101, AttrPatk = 103, AttrMatk = 104;

        private static Obf_R.Obf_ff _global, _patched;
        private static int _checkIn;
        private static bool _reported;

        /// <summary>From LuaPatches (once per LuaEnv, after the game helpers are up).</summary>
        internal static void Install(LA_LuaManager manager, Obf_R.Obf_ff global)
        {
            _global = global;
            _patched = null;
            _reported = false;
            Apply();
        }

        /// <summary>From UiTweaks.Tick (every 10th frame): re-applies after a config reload, cheap when already right.</summary>
        internal static void Tick()
        {
            if (_global == null) return;
            if (++_checkIn < 30) return;
            _checkIn = 0;
            try { Apply(); }
            catch (Exception e) { Warn(e.GetType().Name + ": " + e.Message); }
        }

        private static void Apply()
        {
            if (_global == null) return;
            var manager = Configs();
            if (manager == null)
            {
                Warn("Lua_DBManager:GetInstance() not found; the rank panel keeps the client's zeros");
                return;
            }
            var config = manager.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_TitleConfig");
            var rows = PassChallenges.Values(manager, "m_kCFG_TitleConfig");
            if (config == null && rows == null)
            {
                Warn("m_kCFG_TitleConfig not found; the rank panel keeps the client's zeros");
                return;
            }
            // ReLoadAllCfg builds a new m_kValues: a different table means the rows must be written again.
            if (rows != null && ReferenceEquals(rows, _patched)) return;
            var get = config?.Obf_RuA<Obf_R.Obf_Ef>("GetTitleConfig");
            long before = 0, after = 0;
            int found = 0, written = 0;
            for (int i = 0; i < Power.Length; i++)
            {
                long id = i + 1;
                var row = rows?.Obf_ruA<long, Obf_R.Obf_ff>(id);
                if (row == null && get != null)
                {
                    try
                    {
                        var result = get.Obf_HuA(config, id);
                        row = result != null && result.Length > 0 ? result[0] as Obf_R.Obf_ff : null;
                    }
                    catch { row = null; }
                }
                if (row == null) continue;
                found++;
                if (i == 0) before = Read(row);
                long hp = Power[i] * HpScale, atk = (long)Math.Round(Power[i] * AttackScale);
                PassChallenges.RawSet(row, "_kAttr",
                    PassChallenges.List(PassChallenges.List((long)AttrHp, hp), PassChallenges.List((long)AttrPatk, atk),
                        PassChallenges.List((long)AttrMatk, atk)));
                if (i == 0) after = Read(row);
                written++;
            }
            if (written == 0)
            {
                Warn("no TitleConfig row could be read (rows " + (rows != null) + ", GetTitleConfig " + (get != null)
                    + ", found " + found + "); the rank panel keeps the client's zeros");
                return;
            }
            if (rows != null) _patched = rows;
            if (_reported) return;
            _reported = true;
            UnityEngine.Debug.Log("[Ro3Emu.TitleStats] rank attributes set on " + written + " ranks of " + Power.Length
                + (rows != null ? " (m_kValues)" : " (GetTitleConfig)") + "; rank 1 HP " + before + " -> " + after
                + "; rank 4 = +" + (Power[3] * HpScale) + " HP, rank " + Power.Length + " = +" + (Power[Power.Length - 1] * HpScale) + " HP");
        }

        /// <summary>Lua_DBManager:GetInstance(): the configs' owner, the object LC_DAL_Title reads TitleConfig from.</summary>
        private static Obf_R.Obf_ff Configs()
        {
            var db = _global.Obf_RuA<Obf_R.Obf_ff>("Lua_DBManager");
            var manager = db?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(db);
            return manager != null && manager.Length > 0 ? manager[0] as Obf_R.Obf_ff : null;
        }

        /// <summary>The HP the row's first attribute carries now, -1 when the field is not a pair list.</summary>
        private static long Read(Obf_R.Obf_ff row)
        {
            try
            {
                var attrs = row.Obf_RuA<Obf_R.Obf_ff>("_kAttr");
                var first = attrs?.Obf_ruA<long, Obf_R.Obf_ff>(1);
                var value = first?.Obf_ruA<long, object>(2);
                return value switch { long l => l, double d => (long)d, int n => n, _ => -1 };
            }
            catch { return -1; }
        }

        private static void Warn(string message)
        {
            if (_reported) return;
            _reported = true;
            UnityEngine.Debug.LogWarning("[Ro3Emu.TitleStats] " + message);
        }
    }
}