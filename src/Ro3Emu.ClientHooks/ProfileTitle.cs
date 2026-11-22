using System;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The profile card's "Title" block (UI_Sys_Personal_InfoWidget Pan_Designation/HeadTitle, "Titleless" when empty) prints
    /// RolePublicBaseInfo.headtitle: a NewbieTitlesConfig row whose _iTitleName text is resolved through LanguageKV. This
    /// export spends those rows (ids 1-7) on the Growth Manual titles, so a character with a Title rank but no finished
    /// chapter read "Titleless" on its profile card.
    /// <para>
    /// Ro3Emu.Server TitleService.HeadTitle puts the rank in that slot (rank, capped at the seven rows), and this renames the
    /// seven rows' texts with the names of TitleConfig ranks 1-7 - the same words the Title panel shows ("Beginner",
    /// "Wind's Departure I"...). Renaming the text, not adding rows, is what the card follows: a row added to a config after
    /// it loaded is visible to the editors that walk the table but not to a lookup by id, which is what the card does.
    /// </para>
    /// </summary>
    internal static class ProfileTitle
    {
        private const long RankNameBase = 10150000000;    // TitleConfig _iTitleName of rank 1 (10150000000 + rank - 1)
        private const long GrowthTitleBase = 12100000000; // NewbieTitlesConfig _iTitleName of ids 1-7
        private const int MaxRank = 21;                   // TitleConfig rows; the server caps at the growth rows

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
            var texts = _global.Obf_RuA<Obf_R.Obf_ff>("LanguageKV");
            if (texts == null)
            {
                Warn("LanguageKV not found; the profile card keeps the Growth Manual title names");
                return;
            }
            // ReLoadAllCfg builds a new LanguageKV: a different table means the names must be written again.
            if (ReferenceEquals(texts, _patched)) return;
            int renamed = 0;
            object first = null, last = null;
            for (int rank = 1; rank <= MaxRank; rank++)
            {
                var source = texts.Obf_ruA<long, Obf_R.Obf_ff>(RankNameBase + rank - 1);
                var name = source?.Obf_RuA<object>("_kDes") as string;
                if (string.IsNullOrEmpty(name)) break; // the rank names are consecutive
                PassChallenges.Text(texts, GrowthTitleBase + rank - 1, name);
                if (rank == 1) first = name;
                last = name;
                renamed++;
            }
            if (renamed == 0)
            {
                Warn("TitleConfig has no rank name text; the profile card keeps the Growth Manual title names");
                return;
            }
            _patched = texts;
            if (_reported) return;
            _reported = true;
            UnityEngine.Debug.Log("[Ro3Emu.ProfileTitle] profile titles: " + renamed + " head title names now read "
                + first + " ... " + last);
        }

        private static void Warn(string message)
        {
            if (_reported) return;
            _reported = true;
            UnityEngine.Debug.LogWarning("[Ro3Emu.ProfileTitle] " + message);
        }
    }
}