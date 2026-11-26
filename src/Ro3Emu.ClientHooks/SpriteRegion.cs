using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.U2D;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Extracts a sprite's sub-rectangle from its atlas texture for HUDUber.Image.WriteImageToDynamic (patched by
    /// Ro3Emu.ClientTools.HudSpritePatch). Without this, HUDUber copies the entire atlas page instead of the sprite's
    /// cell, so every overhead guild emblem renders the same region. Also resolves late-binding SpriteAtlas sprites
    /// (such as Guild_Icon) once their atlas is loaded and widens packed compressed sprite rects to 4x4 block bounds.
    /// </summary>
    public static class SpriteRegion
    {
        private static readonly Dictionary<Sprite, Texture2D> Cache = new Dictionary<Sprite, Texture2D>();
        private static readonly Dictionary<string, Sprite> Bound = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private static SpriteAtlas[] _atlases = Array.Empty<SpriteAtlas>();
        private static float _lastScan = -999f;
        private static int _logs;

        private static void Log(Sprite s, string msg)
        {
            if (s == null || s.name == null || !s.name.Contains("guild", StringComparison.OrdinalIgnoreCase)) return;
            if (_logs++ > 300) return;
            Debug.Log("[Ro3Emu.SpriteRegion] " + s.name + ": " + msg);
        }

        private static string CleanName(string name) =>
            name != null && name.EndsWith("(Clone)", StringComparison.Ordinal) ? name.Substring(0, name.Length - 7) : name;

        private static Sprite BindLateAtlas(Sprite sprite)
        {
            string name = CleanName(sprite.name);
            if (string.IsNullOrEmpty(name)) return null;
            if (Bound.TryGetValue(name, out var cached) && cached != null && cached.texture != null) return cached;
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    if (Time.realtimeSinceStartup - _lastScan < 1f) break;
                    _lastScan = Time.realtimeSinceStartup;
                    _atlases = Resources.FindObjectsOfTypeAll<SpriteAtlas>();
                }
                foreach (var atlas in _atlases)
                {
                    if (atlas == null) continue;
                    bool canBind = false;
                    try { canBind = atlas.CanBindTo(sprite); }
                    catch (Exception) { }
                    Sprite bound = atlas.GetSprite(name);
                    if (bound != null && bound.texture != null && (canBind || bound.name.StartsWith(name, StringComparison.Ordinal)))
                    {
                        Bound[name] = bound;
                        Log(sprite, "bound to atlas " + atlas.name + " (" + atlas.spriteCount + " sprites)");
                        return bound;
                    }
                }
            }
            return null;
        }

        /// <summary>Replaces UnityEngine.Sprite.get_texture() inside HUDUber.Image.WriteImageToDynamic().</summary>
        public static Texture2D TextureOf(Sprite sprite)
        {
            if (sprite == null) return null;
            Texture2D tex = sprite.texture;
            if (tex == null)
            {
                Sprite bound = BindLateAtlas(sprite);
                if (bound == null)
                {
                    Log(sprite, "no texture and no loaded SpriteAtlas binds it yet; retrying later");
                    return null;
                }
                sprite = bound;
                tex = bound.texture;
            }
            Rect r;
            try { r = sprite.textureRect; }
            catch (Exception e)
            {
                Log(sprite, "textureRect unavailable (" + e.GetType().Name + "), using full page");
                return tex;
            }
            int x = Mathf.RoundToInt(r.x), y = Mathf.RoundToInt(r.y), w = Mathf.RoundToInt(r.width), h = Mathf.RoundToInt(r.height);
            if (x == 0 && y == 0 && w == tex.width && h == tex.height) return tex;
            if (Cache.TryGetValue(sprite, out var sub) && sub != null) return sub;
            if (sprite.packed && sprite.packingRotation != SpritePackingRotation.None) return tex;
            if (x % 4 != 0 || y % 4 != 0 || w % 4 != 0 || h % 4 != 0)
            {
                if (!sprite.packed) return tex;
                int x0 = (x / 4) * 4, y0 = (y / 4) * 4;
                int x1 = Mathf.Min(tex.width, ((x + w + 3) / 4) * 4), y1 = Mathf.Min(tex.height, ((y + h + 3) / 4) * 4);
                x = x0; y = y0; w = x1 - x0; h = y1 - y0;
            }
            try
            {
                sub = new Texture2D(w, h, tex.format, false) { name = CleanName(sprite.name) };
                Graphics.CopyTexture(tex, 0, 0, x, y, w, h, sub, 0, 0, 0, 0);
            }
            catch (Exception e)
            {
                Log(sprite, "CopyTexture failed: " + e.Message);
                return tex;
            }
            Cache[sprite] = sub;
            return sub;
        }
    }
}
