using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Gives a guild statue NPC its display component. An NPC's NPCConfig._iClientAct picks a lua_unitactconfig entry
    /// whose Components the unit adds before its default display: LC_Unit_Monster and LC_Unit_ModelEffect.CreateVisibleData
    /// walk Components and call LC_UnitCompHelper.TryAddUnitComponent(self, component, {kCreateCallback, kData}). This
    /// build's LC_Unit_NPC.CreateVisibleData reads the entry and drops it, so NPC 25000001 "Guild Area Statue"
    /// (_iClientAct 4, Lua_UnitActConfigExt: Display_GuildStatue) shows its name and no model. The wrapper adds the
    /// entry's components the way the monster does, only for entries holding Display_GuildStatue, then runs the
    /// original, whose LC_UnitBase.CreateVisibleData skips the default display once m_kDisplay is set. Once the statue's
    /// model is loaded it finishes what this build's LC_Display_Agent_GuildStatue leaves out (OnCreateDisplayOver).
    /// </summary>
    public static class NpcActComponents
    {
        /// <summary>LC_Unit_NPC.CreateVisibleData(self, createCallback).</summary>
        public delegate void CreateVisibleDataMethod(Obf_R.Obf_ff self, object callback);

        // Kept alive here: Lua holds the pushed delegate only weakly through xLua's object pool.
        private static readonly CreateVisibleDataMethod Wrapper = OnCreateVisibleData;
        private static readonly CreateVisibleDataMethod DisplayOverWrapper = OnCreateDisplayOver;
        /// <summary>Marks a unit given the statue display (the field is the hook's own).</summary>
        private const string StatueMark = "m_bRo3EmuStatue";

        private static Obf_R.Obf_Ef _original, _tryAdd, _displayOver;
        private static Obf_R.Obf_ff _acts;
        private static Obf_R.Obf_Cf _env;
        /// <summary>enum_UnitComponent.Display_GuildStatue: the component's class name in this build, compared as a value.</summary>
        private static object _statueComponent;
        private static bool _reported;

        internal static void Install(Obf_R.Obf_Cf env, Obf_R.Obf_ff global, Obf_R.Obf_ff npcClass, Obf_R.Obf_ff acts)
        {
            _original = _tryAdd = _displayOver = null;
            _acts = null;
            _env = null;
            _statueComponent = null;
            _reported = false;
            try
            {
                var original = npcClass?.Obf_RuA<Obf_R.Obf_Ef>("CreateVisibleData");
                var displayOver = npcClass?.Obf_RuA<Obf_R.Obf_Ef>("OnCreateDisplayOver");
                var tryAdd = global?.Obf_RuA<Obf_R.Obf_ff>("LC_UnitCompHelper")?.Obf_RuA<Obf_R.Obf_Ef>("TryAddUnitComponent");
                var statue = global?.Obf_RuA<Obf_R.Obf_ff>("enum_UnitComponent")?.Obf_RuA<object>("Display_GuildStatue");
                if (original == null || displayOver == null || tryAdd == null || acts == null || statue == null)
                {
                    Debug.LogWarning("[Ro3Emu.NpcActComponents] not installed: " + (original == null ? "CreateVisibleData " : "") + (displayOver == null ? "OnCreateDisplayOver " : "")
                        + (tryAdd == null ? "TryAddUnitComponent " : "") + (acts == null ? "lua_unitactconfig " : "")
                        + (statue == null ? "Display_GuildStatue" : ""));
                    return;
                }
                _env = env;
                _acts = acts;
                _tryAdd = tryAdd;
                _statueComponent = statue;
                _original = original;
                _displayOver = displayOver;
                npcClass.Obf_luA<string, CreateVisibleDataMethod>("CreateVisibleData", Wrapper);
                npcClass.Obf_luA<string, CreateVisibleDataMethod>("OnCreateDisplayOver", DisplayOverWrapper);
                Debug.Log("[Ro3Emu.NpcActComponents] LC_Unit_NPC.CreateVisibleData wrapped (guild statue display)");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.NpcActComponents] " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void OnCreateVisibleData(Obf_R.Obf_ff self, object callback)
        {
            // LC_UnitManager pools NPC units by type, not by NPC: a reused unit must not keep a statue's mark.
            try { self?.Obf_luA<string, object>(StatueMark, null); AddComponents(self, callback); }
            catch (Exception e) { Once(e); }
            _original?.Obf_HuA(self, callback);
        }

        private static void AddComponents(Obf_R.Obf_ff self, object callback)
        {
            var act = self.Obf_RuA<Obf_R.Obf_ff>("m_kNpcConfig")?.Obf_RuA<object>("_iClientAct");
            if (!(act is long || act is double || act is int) || Convert.ToInt64(act) == 0) return;
            var components = _acts.Obf_ruA<long, Obf_R.Obf_ff>(Convert.ToInt64(act))?.Obf_RuA<Obf_R.Obf_ff>("Components");
            if (components == null || !HoldsStatue(components)) return;
            for (long i = 1; components.Obf_ruA<long, Obf_R.Obf_ff>(i) is { } pair; i++)
            {
                var data = _env.Obf_wTA();
                data.Obf_luA<string, object>("kCreateCallback", callback);
                data.Obf_luA<string, object>("kData", pair.Obf_ruA<long, object>(2));
                _tryAdd.Obf_HuA(self, pair.Obf_ruA<long, object>(1), data);
                self.Obf_luA<string, object>(StatueMark, true);
            }
        }

        /// <summary>LC_UnitBase.OnCreateDisplayOver, then what this build's LC_Display_Agent_GuildStatue leaves out once its model
        /// is loaded: LC_Display_Agent and LC_Display_Agent_ChangeModelByProp call the unit's create callback after
        /// OnCreateDisplayOver (once, m_bIsInitModelOver), which finishes the unit; without it the model stays hidden.</summary>
        private static void OnCreateDisplayOver(Obf_R.Obf_ff self, object model)
        {
            _displayOver?.Obf_HuA(self, model);
            try
            {
                if (self == null || !(self.Obf_RuA<object>(StatueMark) is bool marked && marked)) return;
                self.Obf_luA<string, object>(StatueMark, null);
                Opaque(self);
                var display = self.Obf_RuA<Obf_R.Obf_ff>("m_kDisplay");
                var callback = display?.Obf_RuA<Obf_R.Obf_Ef>("m_kCreateCallback");
                if (display == null || callback == null || display.Obf_RuA<object>("m_bIsInitModelOver") is bool done && done) return;
                display.Obf_luA<string, object>("m_kCreateCallback", null);
                display.Obf_luA<string, object>("m_bIsInitModelOver", true);
                callback.Obf_HuA(self);
            }
            catch (Exception e) { Once(e); }
        }

        /// <summary>The model's materials start at _Alpha 0 (the fade-in value) and this build's statue agent never raises
        /// it, so URP draws nothing; a character's own materials sit at 1 once shown.</summary>
        private static void Opaque(Obf_R.Obf_ff unit)
        {
            var root = unit.Obf_RuA<Transform>("transform");
            if (root == null) return;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                foreach (var material in renderer.materials) // the renderer's own instances, not a shared asset
                    if (material != null && material.HasProperty(AlphaProperty)) material.SetFloat(AlphaProperty, 1f);
        }

        private static readonly int AlphaProperty = Shader.PropertyToID("_Alpha");

        private static bool HoldsStatue(Obf_R.Obf_ff components)
        {
            for (long i = 1; components.Obf_ruA<long, Obf_R.Obf_ff>(i) is { } pair; i++)
                if (Equals(pair.Obf_ruA<long, object>(1), _statueComponent)) return true;
            return false;
        }

        private static void Once(Exception e)
        {
            if (_reported) return;
            _reported = true;
            Debug.LogWarning("[Ro3Emu.NpcActComponents] " + e.GetType().Name + ": " + e.Message);
        }
    }
}
