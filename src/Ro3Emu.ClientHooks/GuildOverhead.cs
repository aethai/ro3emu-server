using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Restores overhead guild names and guild emblems on all maps:
    /// <list type="bullet">
    /// <item>Wraps LC_HudHelper.GetMapLimitParam so ShowGuildName and ShowGuildIcon stay enabled on every map instead
    /// of only the three dragon raid maps.</item>
    /// <item>Wraps LC_HudElement_Guild.RefreshAll to restore the removed emblem branch (GuildIconSprite,
    /// GuildFrameSprite and ShowGuildIcon from GuildIconConfig.GetSmallIcon) and rebuilds the nameplate when a
    /// player's emblem changes.</item>
    /// <item>Wraps LC_MeshUI_NamePlate.UpdateGuildState and SetImage to toggle Layout_GuildArea (forcing a HUDUber
    /// redraw on new sprites), apply neutral white tint so packed atlas colors render unmodified via
    /// <see cref="SpriteRegion"/>, preload the Guild_Icon SpriteAtlas, and retry emblem SetImage calls until the atlas
    /// binds.</item>
    /// </list>
    /// </summary>
    public static class GuildOverhead
    {
        /// <summary>LC_HudHelper.GetMapLimitParam() -> limit table.</summary>
        public delegate object MapLimitMethod(object a1, object a2);

        /// <summary>LC_HudElement_Guild.RefreshAll(self) / LC_MeshUI_NamePlate.UpdateGuildState(self).</summary>
        public delegate void SelfMethod(Obf_R.Obf_ff self);

        /// <summary>LC_MeshUI_NamePlate.SetImage(self, node, resourceId).</summary>
        public delegate void SetImageMethod(Obf_R.Obf_ff self, object node, object id);

        /// <summary>Lua_UT_ResourceUtils.LoadUObjectToIDWithTag callback.</summary>
        public delegate void LoadCallback(object id);

        private sealed class RetryEntry
        {
            public Obf_R.Obf_ff Mesh;
            public long Node;
            public object Id;
            public string ExpectedName;
            public int Attempts;
        }

        private static readonly string[] AtlasNames = { "Guild_Icon.spriteatlasv2", "guild_icon.spriteatlasv2" };

        // Kept alive here: Lua holds pushed delegates only weakly through xLua's object pool.
        private static readonly MapLimitMethod MapLimitWrapper = OnGetMapLimitParam;
        private static readonly SelfMethod RefreshAllWrapper = OnRefreshAll;
        private static readonly SelfMethod UpdateGuildWrapper = OnUpdateGuildState;
        private static readonly SetImageMethod SetImageWrapper = OnSetImage;
        private static readonly LoadCallback AtlasLoaded = OnAtlasLoaded;

        private static LA_LuaManager _manager;
        private static Obf_R.Obf_Cf _env;
        private static Obf_R.Obf_ff _global, _whiteColor;
        private static Obf_R.Obf_Ef _next, _rawEqual, _origMapLimit, _origRefreshAll, _origUpdateGuild, _origSetImage;
        private static bool _hookedGuild, _hookedPlate, _reported;
        private static int _tickCount;

        private static readonly Dictionary<string, string> LastEmblem = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly List<Obf_R.Obf_ff> PendingRebuild = new List<Obf_R.Obf_ff>();
        private static readonly List<Obf_R.Obf_ff> PendingRedraw = new List<Obf_R.Obf_ff>();
        private static readonly List<RetryEntry> PendingRetry = new List<RetryEntry>();

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(LA_LuaManager manager, Obf_R.Obf_Cf env, Obf_R.Obf_ff global)
        {
            _manager = manager;
            _env = env;
            _global = global;
            _whiteColor = null;
            _next = global?.Obf_RuA<Obf_R.Obf_Ef>("next");
            _rawEqual = global?.Obf_RuA<Obf_R.Obf_Ef>("rawequal");
            _origMapLimit = _origRefreshAll = _origUpdateGuild = _origSetImage = null;
            _hookedGuild = _hookedPlate = _reported = false;
            _tickCount = 0;
            LastEmblem.Clear();
            PendingRebuild.Clear();
            PendingRedraw.Clear();
            PendingRetry.Clear();

            try
            {
                var helper = LuaPatches.Module(manager, global, "lc_hudhelper");
                var getLimit = helper?.Obf_RuA<Obf_R.Obf_Ef>("GetMapLimitParam");
                if (getLimit == null)
                {
                    Debug.LogWarning("[Ro3Emu.GuildOverhead] lc_hudhelper.GetMapLimitParam not found");
                    return;
                }
                _origMapLimit = getLimit;
                helper.Obf_luA<string, MapLimitMethod>("GetMapLimitParam", MapLimitWrapper);
                PreloadAtlas();
                HookModules();
                Debug.Log("[Ro3Emu.GuildOverhead] installed (guild=" + _hookedGuild + ", plate=" + _hookedPlate + ")");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.GuildOverhead] " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Called from UiTweaks.Tick (every 10th frame).</summary>
        internal static void Tick()
        {
            if (_global == null || _origMapLimit == null) return;
            try
            {
                ProcessRebuilds();
                ProcessRedraws();
                if (++_tickCount % 12 != 0) return;
                if (!_hookedGuild || !_hookedPlate) HookModules();
                ProcessRetries();
            }
            catch (Exception e) { Once(e); }
        }

        private static void PreloadAtlas()
        {
            var ru = _global?.Obf_RuA<Obf_R.Obf_ff>("Lua_UT_ResourceUtils");
            var load = ru?.Obf_RuA<Obf_R.Obf_Ef>("LoadUObjectToIDWithTag");
            if (load == null) return;
            foreach (var name in AtlasNames)
            {
                try { load.Obf_HuA(name, AtlasLoaded); }
                catch (Exception e) { Once(e); }
            }
        }

        private static void OnAtlasLoaded(object id) { }

        private static void HookModules()
        {
            if (!_hookedGuild)
            {
                var guild = LuaPatches.Module(_manager, _global, "lc_hudelement_guild");
                var refresh = guild?.Obf_RuA<Obf_R.Obf_Ef>("RefreshAll");
                if (refresh != null)
                {
                    _origRefreshAll = refresh;
                    guild.Obf_luA<string, SelfMethod>("RefreshAll", RefreshAllWrapper);
                    _hookedGuild = true;
                }
            }
            if (!_hookedPlate)
            {
                var plate = LuaPatches.Module(_manager, _global, "lc_meshui_nameplate");
                var updateGuild = plate?.Obf_RuA<Obf_R.Obf_Ef>("UpdateGuildState");
                if (updateGuild != null)
                {
                    _origUpdateGuild = updateGuild;
                    plate.Obf_luA<string, SelfMethod>("UpdateGuildState", UpdateGuildWrapper);
                    var setImage = plate.Obf_RuA<Obf_R.Obf_Ef>("SetImage");
                    if (setImage != null)
                    {
                        _origSetImage = setImage;
                        plate.Obf_luA<string, SetImageMethod>("SetImage", SetImageWrapper);
                    }
                    PatchPropertyHandler(plate);
                    _hookedPlate = true;
                }
            }
        }

        private static void PatchPropertyHandler(Obf_R.Obf_ff plate)
        {
            long guildDisplay = HudProp("GuildDisplay");
            if (guildDisplay == long.MinValue) return;
            var handlers = plate.Obf_RuA<Obf_R.Obf_ff>("PropertyHandlers");
            var entry = handlers?.Obf_ruA<long, Obf_R.Obf_ff>(guildDisplay);
            entry?.Obf_luA<long, SelfMethod>(1L, UpdateGuildWrapper);
        }

        private static object OnGetMapLimitParam(object a1, object a2)
        {
            var res = _origMapLimit?.Obf_HuA(a1, a2);
            object ret = res != null && res.Length > 0 ? res[0] : null;
            if (ret is Obf_R.Obf_ff table)
            {
                try
                {
                    long showName = HudProp("ShowGuildName");
                    long showIcon = HudProp("ShowGuildIcon");
                    if (showName != long.MinValue) table.Obf_luA<long, bool>(showName, true);
                    if (showIcon != long.MinValue) table.Obf_luA<long, bool>(showIcon, true);
                }
                catch (Exception e) { Once(e); }
            }
            return ret;
        }

        private static void OnRefreshAll(Obf_R.Obf_ff self)
        {
            _origRefreshAll?.Obf_HuA(self);
            if (self == null) return;
            try { ApplyEmblem(self); }
            catch (Exception e) { Once(e); }
        }

        private static void ApplyEmblem(Obf_R.Obf_ff self)
        {
            var unit = CallTable(self, "GetUnit");
            if (unit == null) return;
            long isMask = AttrKey("IsMaskFace");
            if (isMask != long.MinValue && IsTruthy(CallOne(unit, "GetHeadInfoDataRaw", isMask))) return;
            long guildAttr = AttrKey("Guild");
            if (guildAttr == long.MinValue || LuaPatches.ToLong(CallOne(unit, "GetHeadInfoData", guildAttr)) <= 0) return;

            var setting = CallTable(self, "GetCachedSetting");
            var context = self.Obf_RuA<Obf_R.Obf_ff>("m_kContext");
            bool showNames = setting == null || !(setting.Obf_RuA<object>("bIsShowNameInfo") is bool b && !b);
            bool selected = context != null && (IsTruthy(context.Obf_RuA<object>("bSelected")) || IsTruthy(context.Obf_RuA<object>("bIsOnHover")));
            if (!showNames && !selected) return;

            long guildIconAttr = AttrKey("GuildIcon");
            if (guildIconAttr == long.MinValue) return;
            if (!(CallOne(unit, "GetHeadInfoData", guildIconAttr) is Obf_R.Obf_ff icon)) return;

            var dbInst = _global.Obf_LuA<Obf_R.Obf_Ef>("Lua_DBManager.GetInstance")?.Obf_HuA(_global.Obf_RuA<object>("Lua_DBManager"));
            var db = dbInst != null && dbInst.Length > 0 ? dbInst[0] as Obf_R.Obf_ff : null;
            var cfg = db?.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_GuildIconConfig");
            if (cfg == null) return;

            string iconName = CallOne(cfg, "GetSmallIcon", icon.Obf_RuA<object>("iconType")) as string;
            string bgName = CallOne(cfg, "GetSmallIcon", icon.Obf_RuA<object>("backgroundType")) as string;
            if (string.IsNullOrEmpty(iconName) || string.IsNullOrEmpty(bgName)) return;

            string uuid = unit.Obf_RuA<object>("m_kUUID")?.ToString();
            if (!string.IsNullOrEmpty(uuid))
            {
                string key = iconName + "|" + bgName;
                if (LastEmblem.TryGetValue(uuid, out var prev) && !string.Equals(prev, key, StringComparison.Ordinal))
                    AddUnique(PendingRebuild, self);
                LastEmblem[uuid] = key;
            }

            var setProp = self.Obf_RuA<Obf_R.Obf_Ef>("SetProperty");
            if (setProp == null) return;
            long iconSprite = HudProp("GuildIconSprite");
            long frameSprite = HudProp("GuildFrameSprite");
            long showIcon = HudProp("ShowGuildIcon");
            if (iconSprite == long.MinValue || frameSprite == long.MinValue || showIcon == long.MinValue) return;
            setProp.Obf_HuA(self, iconSprite, iconName);
            setProp.Obf_HuA(self, frameSprite, bgName);
            setProp.Obf_HuA(self, showIcon, true);

            var meshes = self.Obf_RuA<Obf_R.Obf_ff>("m_kMeshUI");
            if (meshes == null || _next == null) return;
            object cursor = null;
            for (int guard = 0; guard < 32; guard++)
            {
                var pair = _next.Obf_HuA(meshes, cursor);
                if (pair == null || pair.Length < 2 || pair[0] == null) break;
                cursor = pair[0];
                if (pair[1] is Obf_R.Obf_ff mesh && HasGuildArea(mesh))
                {
                    PatchPropertyHandler(mesh);
                    mesh.Obf_RuA<Obf_R.Obf_Ef>("UpdateGuildState")?.Obf_HuA(mesh);
                }
            }
        }

        private static void OnUpdateGuildState(Obf_R.Obf_ff self)
        {
            _origUpdateGuild?.Obf_HuA(self);
            if (self == null) return;
            try
            {
                var hud = self.Obf_RuA<Obf_R.Obf_ff>("m_kHud");
                var node = self.Obf_RuA<Obf_R.Obf_ff>("Node");
                if (hud == null || node == null) return;
                long areaNode = LuaPatches.ToLong(node.Obf_RuA<object>("Layout_GuildArea"));
                long showProp = HudProp("ShowGuildIcon");
                if (areaNode == long.MinValue || showProp == long.MinValue) return;
                bool show = CallOne(hud, "GetProperty", showProp) is bool b && b;
                var setActive = self.Obf_RuA<Obf_R.Obf_Ef>("SetActive");
                if (setActive == null) return;
                if (!(show && ContainsTable(PendingRedraw, self)))
                    setActive.Obf_HuA(self, areaNode, show);
                long bgNode = LuaPatches.ToLong(node.Obf_RuA<object>("Image_GuildBg"));
                if (bgNode != long.MinValue)
                    setActive.Obf_HuA(self, bgNode, true);
            }
            catch (Exception e) { Once(e); }
        }

        private static void OnSetImage(Obf_R.Obf_ff self, object nodeObj, object idObj)
        {
            long node = LuaPatches.ToLong(nodeObj);
            long guildNode = long.MinValue, bgNode = long.MinValue, areaNode = long.MinValue;
            if (self != null)
            {
                var nodes = self.Obf_RuA<Obf_R.Obf_ff>("Node");
                if (nodes != null)
                {
                    guildNode = LuaPatches.ToLong(nodes.Obf_RuA<object>("Image_Guild"));
                    bgNode = LuaPatches.ToLong(nodes.Obf_RuA<object>("Image_GuildBg"));
                    areaNode = LuaPatches.ToLong(nodes.Obf_RuA<object>("Layout_GuildArea"));
                }
            }
            bool isEmblem = node != long.MinValue && (node == guildNode || node == bgNode);

            try
            {
                _origSetImage?.Obf_HuA(self, nodeObj, idObj);
            }
            catch (Exception)
            {
                if (!isEmblem) throw;
                QueueRetry(self, node, idObj);
                return;
            }

            if (!isEmblem) return;
            RemoveRetry(self, node);
            try
            {
                var hud = self.Obf_RuA<Obf_R.Obf_ff>("m_kHud");
                long showProp = HudProp("ShowGuildIcon");
                if (areaNode == long.MinValue || hud == null || showProp == long.MinValue) return;
                if (!(CallOne(hud, "GetProperty", showProp) is bool show && show)) return;

                var white = WhiteTable();
                if (white != null)
                {
                    self.Obf_RuA<Obf_R.Obf_Ef>("SetColor")?.Obf_HuA(self, node, white);
                    long colorProp = HudProp(node == bgNode ? "GuildFrameColor" : "GuildIconColor");
                    if (colorProp != long.MinValue) hud.Obf_RuA<Obf_R.Obf_Ef>("SetProperty")?.Obf_HuA(hud, colorProp, white);
                }
                self.Obf_RuA<Obf_R.Obf_Ef>("SetActive")?.Obf_HuA(self, areaNode, false);
                AddUnique(PendingRedraw, self);
            }
            catch (Exception e) { Once(e); }
        }

        private static void ProcessRebuilds()
        {
            if (PendingRebuild.Count == 0 || _next == null) return;
            var batch = PendingRebuild.ToArray();
            PendingRebuild.Clear();
            foreach (var comp in batch)
            {
                try
                {
                    var meshes = comp?.Obf_RuA<Obf_R.Obf_ff>("m_kMeshUI");
                    if (meshes == null) continue;
                    object cursor = null;
                    for (int guard = 0; guard < 32; guard++)
                    {
                        var pair = _next.Obf_HuA(meshes, cursor);
                        if (pair == null || pair.Length < 2 || pair[0] == null) break;
                        cursor = pair[0];
                        if (pair[1] is Obf_R.Obf_ff mesh && HasGuildArea(mesh))
                        {
                            comp.Obf_RuA<Obf_R.Obf_Ef>("TryRemoveMeshUI")?.Obf_HuA(comp, cursor);
                            var attached = comp.Obf_RuA<Obf_R.Obf_Ef>("TryAttachMeshUI")?.Obf_HuA(comp, cursor);
                            var fresh = attached != null && attached.Length > 0 ? attached[0] as Obf_R.Obf_ff : null;
                            if (fresh == null)
                                fresh = comp.Obf_RuA<Obf_R.Obf_ff>("m_kMeshUI")?.Obf_ruA<object, Obf_R.Obf_ff>(cursor);
                            fresh?.Obf_RuA<Obf_R.Obf_Ef>("UpdateGuildState")?.Obf_HuA(fresh);
                            break;
                        }
                    }
                }
                catch (Exception e) { Once(e); }
            }
        }

        private static void ProcessRedraws()
        {
            if (PendingRedraw.Count == 0) return;
            var batch = PendingRedraw.ToArray();
            PendingRedraw.Clear();
            long showProp = HudProp("ShowGuildIcon");
            if (showProp == long.MinValue) return;
            foreach (var mesh in batch)
            {
                try
                {
                    var hud = mesh?.Obf_RuA<Obf_R.Obf_ff>("m_kHud");
                    var node = mesh?.Obf_RuA<Obf_R.Obf_ff>("Node");
                    long areaNode = node != null ? LuaPatches.ToLong(node.Obf_RuA<object>("Layout_GuildArea")) : long.MinValue;
                    if (hud != null && areaNode != long.MinValue && CallOne(hud, "GetProperty", showProp) is bool show && show)
                        mesh.Obf_RuA<Obf_R.Obf_Ef>("SetActive")?.Obf_HuA(mesh, areaNode, true);
                }
                catch (Exception e) { Once(e); }
            }
        }

        private static void ProcessRetries()
        {
            if (PendingRetry.Count == 0) return;
            var batch = PendingRetry.ToArray();
            foreach (var entry in batch)
            {
                try
                {
                    var names = entry.Mesh?.Obf_RuA<Obf_R.Obf_ff>("m_kSpriteNameMap");
                    string current = names?.Obf_ruA<long, string>(entry.Node);
                    if (entry.Attempts > 30 || !string.Equals(current, entry.ExpectedName, StringComparison.Ordinal))
                    {
                        PendingRetry.Remove(entry);
                        continue;
                    }
                    OnSetImage(entry.Mesh, entry.Node, entry.Id);
                }
                catch (Exception e) { Once(e); }
            }
        }

        private static void QueueRetry(Obf_R.Obf_ff mesh, long node, object id)
        {
            var names = mesh?.Obf_RuA<Obf_R.Obf_ff>("m_kSpriteNameMap");
            string expected = names?.Obf_ruA<long, string>(node);
            for (int i = 0; i < PendingRetry.Count; i++)
            {
                if (PendingRetry[i].Node == node && SameLuaTable(PendingRetry[i].Mesh, mesh))
                {
                    PendingRetry[i].Attempts = Equals(PendingRetry[i].Id, id) ? PendingRetry[i].Attempts + 1 : 1;
                    PendingRetry[i].Id = id;
                    PendingRetry[i].ExpectedName = expected;
                    return;
                }
            }
            PendingRetry.Add(new RetryEntry { Mesh = mesh, Node = node, Id = id, ExpectedName = expected, Attempts = 1 });
        }

        private static void RemoveRetry(Obf_R.Obf_ff mesh, long node)
        {
            for (int i = PendingRetry.Count - 1; i >= 0; i--)
                if (PendingRetry[i].Node == node && SameLuaTable(PendingRetry[i].Mesh, mesh))
                    PendingRetry.RemoveAt(i);
        }

        private static Obf_R.Obf_ff WhiteTable()
        {
            if (_whiteColor != null) return _whiteColor;
            if (_env == null) return null;
            var t = _env.Obf_wTA();
            t.Obf_luA<string, double>("r", 1.0);
            t.Obf_luA<string, double>("g", 1.0);
            t.Obf_luA<string, double>("b", 1.0);
            t.Obf_luA<string, double>("a", 1.0);
            _whiteColor = t;
            return t;
        }

        private static bool HasGuildArea(Obf_R.Obf_ff mesh)
        {
            var node = mesh?.Obf_RuA<Obf_R.Obf_ff>("Node");
            return node != null && node.Obf_RuA<object>("Layout_GuildArea") != null;
        }

        private static long HudProp(string name) =>
            _global == null ? long.MinValue : LuaPatches.ToLong(_global.Obf_LuA<object>("enum_HudProperty." + name));

        private static long AttrKey(string name) =>
            _global == null ? long.MinValue : LuaPatches.ToLong(_global.Obf_LuA<object>("enum_unitAttrClientMap." + name));

        private static Obf_R.Obf_ff CallTable(Obf_R.Obf_ff target, string method)
        {
            var res = target?.Obf_RuA<Obf_R.Obf_Ef>(method)?.Obf_HuA(target);
            return res != null && res.Length > 0 ? res[0] as Obf_R.Obf_ff : null;
        }

        private static object CallOne(Obf_R.Obf_ff target, string method, object arg)
        {
            var res = target?.Obf_RuA<Obf_R.Obf_Ef>(method)?.Obf_HuA(target, arg);
            return res != null && res.Length > 0 ? res[0] : null;
        }

        private static bool IsTruthy(object value) => value != null && !(value is bool b && !b);

        private static bool SameLuaTable(Obf_R.Obf_ff a, Obf_R.Obf_ff b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || _rawEqual == null) return false;
            var res = _rawEqual.Obf_HuA(a, b);
            return res != null && res.Length > 0 && res[0] is bool eq && eq;
        }

        private static bool ContainsTable(List<Obf_R.Obf_ff> list, Obf_R.Obf_ff table)
        {
            for (int i = 0; i < list.Count; i++)
                if (SameLuaTable(list[i], table)) return true;
            return false;
        }

        private static void AddUnique(List<Obf_R.Obf_ff> list, Obf_R.Obf_ff table)
        {
            if (!ContainsTable(list, table)) list.Add(table);
        }

        private static void Once(Exception e)
        {
            if (_reported) return;
            _reported = true;
            Debug.LogWarning("[Ro3Emu.GuildOverhead] " + e.GetType().Name + ": " + e.Message);
        }
    }
}
