using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Content packs for the client: the same <c>ro3emu\content\*.json</c> files the server reads from its
    /// <c>data\content\</c> (server side: Ro3Emu.Server.Data.ContentPacks), applied to the client's own tables so a
    /// changed item, quest, skill or event row reads the same in the panels as it does on the server.
    /// <para>
    /// Without a pack file nothing happens. A pack names tables and the rows to set; each row is looked up through the
    /// table's own <c>GetEntryPtr</c> (the config classes' reader) and the named fields are written on it. Only scalar
    /// values are written - a string, number, boolean or null; a pack that puts an object or an array in a field, or
    /// that asks to remove rows, is reported and skipped, because the client's rows are shared structures that other
    /// tables and the UI hold on to.
    /// </para>
    /// </summary>
    internal static class ContentPacks
    {
        /// <summary>The folder next to the game executable the player's launcher writes the packs into.</summary>
        private const string Folder = "ro3emu";

        private static object _applied;
        private static readonly HashSet<string> Reported = new HashSet<string>();

        /// <summary>From LuaPatches.Tick, once per LuaEnv and only once the configs' owner answers.</summary>
        internal static void Install(object env, Obf_R.Obf_ff global)
        {
            if (ReferenceEquals(env, _applied)) return;
            try
            {
                string directory = Path.Combine(GameRoot(), Folder, "content");
                if (!Directory.Exists(directory)) return;
                string[] files = Directory.GetFiles(directory, "*.json");
                if (files.Length == 0) return;
                Array.Sort(files, StringComparer.Ordinal);

                var database = global?.Obf_RuA<Obf_R.Obf_ff>("Lua_DBManager");
                var configs = First(database?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(database)) as Obf_R.Obf_ff;
                if (configs == null) return; // the configs are not loaded yet: try again on the next tick

                int packs = 0, rows = 0, skipped = 0, problems = 0;
                foreach (string file in files)
                {
                    var pack = MiniJson.Parse(File.ReadAllText(file)) as Dictionary<string, object>;
                    if (pack == null || !(pack.TryGetValue("tables", out object tables) && tables is Dictionary<string, object> specs))
                    {
                        Once(Path.GetFileName(file) + ": no \"tables\" object");
                        problems++;
                        continue;
                    }

                    bool bad = false;
                    foreach (var spec in specs)
                    {
                        if (!(spec.Value is Dictionary<string, object> body))
                        {
                            Once(Path.GetFileName(file) + ": " + spec.Key + " is not an object");
                            bad = true;
                            continue;
                        }
                        if (body.TryGetValue("remove", out object _))
                        {
                            Once(Path.GetFileName(file) + ": " + spec.Key + " remove is not applied on the client");
                            skipped++;
                        }
                        if (!body.TryGetValue("set", out object setValue) || !(setValue is Dictionary<string, object> set)) continue;

                        var config = configs.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_" + spec.Key);
                        var get = config?.Obf_RuA<Obf_R.Obf_Ef>("GetEntryPtr");
                        if (get == null)
                        {
                            Once(Path.GetFileName(file) + ": the client has no config " + spec.Key);
                            bad = true;
                            continue;
                        }
                        foreach (var entry in set)
                        {
                            if (!(entry.Value is Dictionary<string, object> fields))
                            {
                                Once(Path.GetFileName(file) + ": " + spec.Key + " row " + entry.Key + " is not an object");
                                bad = true;
                                continue;
                            }
                            object id = long.TryParse(entry.Key, out long number) ? (object)number : entry.Key;
                            if (!(First(get.Obf_HuA(config, id)) is Obf_R.Obf_ff row))
                            {
                                Once(Path.GetFileName(file) + ": " + spec.Key + " has no row " + entry.Key);
                                skipped++;
                                continue;
                            }
                            foreach (var field in fields)
                            {
                                if (!Scalar(field.Value))
                                {
                                    Once(Path.GetFileName(file) + ": " + spec.Key + " " + entry.Key + "." + field.Key
                                        + " is not a plain value (objects and arrays are not applied on the client)");
                                    skipped++;
                                    continue;
                                }
                                row.Obf_luA<string, object>(field.Key, field.Value);
                                rows++;
                            }
                        }
                    }
                    if (!bad) packs++;
                }

                _applied = env;
                Debug.Log("[Ro3Emu.Content] " + packs + " pack(s), " + rows + " field(s) set, " + skipped
                    + " skipped, " + problems + " problem(s) from " + directory);
            }
            catch (Exception e)
            {
                _applied = env; // one attempt per LuaEnv: a broken pack is reported once, not every tick
                Debug.LogWarning("[Ro3Emu.Content] packs not applied (" + e.GetType().Name + ": " + e.Message + ")");
            }
        }

        private static bool Scalar(object value) =>
            value == null || value is string || value is bool || value is long || value is double || value is int;

        private static string GameRoot()
        {
            try { return Path.GetDirectoryName(Application.dataPath); }
            catch (Exception) { return null; }
        }

        private static object First(object[] result) => result != null && result.Length > 0 ? result[0] : null;

        private static void Once(string message)
        {
            if (Reported.Add(message)) Debug.LogWarning("[Ro3Emu.Content] " + message);
        }
    }
}
