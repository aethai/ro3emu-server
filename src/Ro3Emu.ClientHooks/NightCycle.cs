using System;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The optional night look (server option Ro3Emu:NightCycle, docs/systems/night-cycle.md). The game has no day and night:
    /// every map has fixed, pre-baked light, so night is drawn on top of the finished picture instead: a see-through
    /// dark layer in a canvas of the world camera, after the world and before the game's menus (which stay bright). It
    /// follows the player's own PC clock and changes only this player's screen:
    /// <list type="bullet">
    /// <item>06:00-17:00 day (no layer).</item>
    /// <item>17:00-19:00 dusk: fades in, warm orange first, then blue.</item>
    /// <item>19:00-05:00 night: full layer (<see cref="NightAlpha"/>).</item>
    /// <item>05:00-06:00 dawn: fades out.</item>
    /// </list>
    /// Nothing shows until the server sends "time on" (the option, or a GM's !day / !dusk / !night / !dawn / !time).
    /// The server also sends "time place outdoor|indoor": only towns and fields are outdoor, so dungeons, raids and story
    /// scenes never get the layer. Commands arrive as message 77012 (Diagnostics "time ..."). Logged as "[Ro3Emu.NightCycle]".
    /// </summary>
    internal static class NightCycle
    {
        private const string LayerName = "Ro3Emu_NightLayer";
        private const float NightAlpha = 0.55f;
        private const float CheckSeconds = 0.5f;
        private const int MaxLogs = 60;
        private static readonly Color NightColour = new Color(0.02f, 0.05f, 0.16f);
        private static readonly Color DuskColour = new Color(0.35f, 0.14f, 0.04f);

        private static GameObject _layer;
        private static Canvas _canvas;
        private static Image _image;
        private static Camera _camera;
        private static bool _enabled;    // "time on" from the server, or any GM time command
        private static bool? _outdoor;   // from "time place ...": null until the server has said
        private static string _gm;       // a GM's setting (day, dusk, night, dawn, an hour); null = the PC clock
        private static float _nextCheck;
        private static string _lastState, _lastScene;
        private static int _logs;
        private static bool _failed;

        /// <summary>From UiTweaks.Tick (every 10th frame); works twice a second.</summary>
        internal static void Tick()
        {
            if (_failed || Time.realtimeSinceStartup < _nextCheck) return;
            _nextCheck = Time.realtimeSinceStartup + CheckSeconds;
            Update();
        }

        /// <summary>From Diagnostics (message 77012): "on", "place outdoor|indoor", or a GM setting (auto, day, dusk, night,
        /// dawn, an hour 0-24) that wins over the clock until "auto" or until the game is closed.</summary>
        internal static void Command(string value)
        {
            value = (value ?? "auto").Trim().ToLowerInvariant();
            if (value == "on") _enabled = true;
            else if (value.StartsWith("place ", StringComparison.Ordinal))
            {
                bool outdoor = value.EndsWith("outdoor", StringComparison.Ordinal);
                if (outdoor != _outdoor) Log("place: " + (outdoor ? "town or field" : "dungeon, raid or story (no night layer)"));
                _outdoor = outdoor;
            }
            else
            {
                _enabled = true;
                _gm = value.Length == 0 || value == "auto" ? null : value;
                Log("GM setting: " + (_gm ?? "auto (clock)"));
            }
            _failed = false;
            Update();
        }

        private static void Update()
        {
            try
            {
                var cam = Camera.main;
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                float hour = Hour();
                Shade(hour, out var colour, out float alpha, out string state);
                bool skip = !_enabled || _outdoor == false || cam == null
                    || scene.StartsWith("sc_creator", StringComparison.OrdinalIgnoreCase)
                    || scene.Contains("login", StringComparison.OrdinalIgnoreCase);
                if (skip || alpha <= 0.001f)
                {
                    if (_layer != null && _layer.activeSelf) _layer.SetActive(false);
                }
                else
                {
                    Ensure(cam);
                    _image.color = new Color(colour.r, colour.g, colour.b, alpha);
                    if (!_layer.activeSelf) _layer.SetActive(true);
                }
                string summary = state + (skip ? " (not shown here)" : "");
                if (summary == _lastState && scene == _lastScene) return;
                _lastState = summary;
                _lastScene = scene;
                Log("map " + scene + ", clock " + hour.ToString("F2") + (_gm != null ? " (GM: " + _gm + ")" : "")
                    + ": " + summary + ", layer " + (alpha * 100f).ToString("F0") + "%");
            }
            catch (Exception e)
            {
                _failed = true;
                Log("stopped: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static float Hour()
        {
            switch (_gm)
            {
                case "night": return 22f;
                case "dusk": return 18f;
                case "dawn": return 5.5f;
                case "day": return 12f;
            }
            if (_gm != null && float.TryParse(_gm, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float h))
                return Mathf.Repeat(h, 24f);
            var now = DateTime.Now;
            return now.Hour + now.Minute / 60f + now.Second / 3600f;
        }

        /// <summary>Colour and strength of the layer for an hour of the day (0-24).</summary>
        private static void Shade(float hour, out Color colour, out float alpha, out string state)
        {
            colour = NightColour;
            if (hour >= 6f && hour < 17f) { alpha = 0f; state = "day"; return; }
            if (hour >= 19f || hour < 5f) { alpha = NightAlpha; state = "night"; return; }
            if (hour >= 17f && hour < 19f)
            {
                float t = (hour - 17f) / 2f; // 0 at 17:00, 1 at 19:00
                colour = Color.Lerp(DuskColour, NightColour, Mathf.Clamp01(t * 1.5f));
                alpha = Mathf.Lerp(0.15f, NightAlpha, t);
                state = "dusk";
                return;
            }
            alpha = Mathf.Lerp(NightAlpha, 0f, hour - 5f); // dawn 05:00-06:00
            state = "dawn";
        }

        /// <summary>The layer: a full-screen picture in a canvas drawn by the world camera itself, just in front of it, so
        /// the menus (drawn by another camera after the world) stay bright. Re-attached when the world camera changes.</summary>
        private static void Ensure(Camera cam)
        {
            if (_layer == null)
            {
                _layer = new GameObject(LayerName);
                UnityEngine.Object.DontDestroyOnLoad(_layer);
                _canvas = _layer.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceCamera;
                _canvas.sortingOrder = 32000;
                var imageObject = new GameObject("Shade", typeof(RectTransform));
                imageObject.transform.SetParent(_layer.transform, false);
                var rect = (RectTransform)imageObject.transform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                _image = imageObject.AddComponent<Image>();
                _image.raycastTarget = false;
                Log("night layer made");
            }
            if (_camera == cam) return;
            _camera = cam;
            _canvas.worldCamera = cam;
            _canvas.planeDistance = Mathf.Max(cam.nearClipPlane + 0.05f, cam.nearClipPlane * 1.5f);
            int layer = FirstLayer(cam.cullingMask);
            _layer.layer = layer;
            _image.gameObject.layer = layer;
            Log("night layer on camera " + cam.name + " (near " + cam.nearClipPlane.ToString("F2") + ", layer " + layer + ")");
        }

        /// <summary>A layer the world camera draws (prefers the Default layer).</summary>
        private static int FirstLayer(int mask)
        {
            if ((mask & 1) != 0) return 0;
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) return i;
            return 0;
        }

        private static void Log(string message)
        {
            if (_logs++ < MaxLogs) Debug.Log("[Ro3Emu.NightCycle] " + message);
        }
    }
}
