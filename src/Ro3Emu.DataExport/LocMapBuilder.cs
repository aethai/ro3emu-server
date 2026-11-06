using System.Text;
using System.Text.Json;

namespace Ro3Emu.DataExport;

/// <summary>
/// The English text map Ro3Emu.Loc.dll reads: "zh\ten" lines, \t \n \r \\ escaped.
/// A port of tools/Loc/build_locmap.js, in its order:
///   1. optional: re/loc/translation_queue.tsv rows that already have English in the client's tables (developers only);
///   2. every Localization_zh_CN / Localization_en pair of the player's own client: text equal to a table string gets
///      the client's own English (the most frequent English when one Chinese text has several);
///   3. our translations (src/Ro3Emu.Loc/translations/*.tsv, later files win).
/// RO3Emu.exe writes it to &lt;client&gt;/ro3emu/loc_en.tsv, which Ro3Loc loads after its embedded map, so the
/// release's Loc DLL carries no text taken from the client.
/// </summary>
public sealed class LocMapBuilder(string language = LocMapBuilder.English)
{
    /// <summary>The default map: the client's Chinese texts become English.</summary>
    public const string English = "en";

    /// <summary>
    /// The client's Simplified Chinese texts become Traditional Chinese: step 2 pairs Localization_zh_CN with the
    /// client's own Localization_zh_TW (the publisher's Taiwan wording), step 3 uses src/Ro3Emu.Loc/translations-zhtw.
    /// </summary>
    public const string TraditionalChinese = "zh-TW";

    public static bool IsSupported(string? language) => language is English or TraditionalChinese;

    /// <summary>The language code as written here ("zh-tw" -> "zh-TW"), or null when it is not one we build.</summary>
    public static string? Supported(string? language) => language?.ToLowerInvariant() switch
    {
        "en" => English,
        "zh-tw" => TraditionalChinese,
        _ => null,
    };

    private readonly OrderedDictionary<string, string> _map = new(StringComparer.Ordinal);
    private bool Chinese => language == TraditionalChinese;

    /// <summary>The language a map file was built for (<see cref="English"/> when its first line does not say).</summary>
    public static string LanguageOf(string path)
    {
        try
        {
            var first = File.ReadLines(path, Encoding.UTF8).FirstOrDefault() ?? "";
            return first.Contains($"(language {TraditionalChinese})", StringComparison.Ordinal) ? TraditionalChinese : English;
        }
        catch (IOException) { return English; }
    }

    public int FromQueue { get; private set; }
    public int FromTables { get; private set; }
    public int Ours { get; private set; }
    public int Count => _map.Count;

    public static bool HasCjk(string s)
    {
        foreach (char c in s)
            if (c is >= '\u2E80' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF') return true;
        return false;
    }

    /// <summary>Step 1: the translation queue (columns text, existing_en).</summary>
    public void AddQueue(string tsvPath)
    {
        foreach (var row in ReadTsv(tsvPath))
        {
            row.TryGetValue("existing_en", out var en);
            row.TryGetValue("text", out var text);
            if (string.IsNullOrEmpty(en) || en == "None" || HasCjk(en) || !HasCjk(text ?? "")) continue;
            _map[text!] = en;
            FromQueue++;
        }
    }

    /// <summary>
    /// Step 2: the client's own zh_CN / target pairs (ConfigExport JSON of Localization_zh_CN and of Localization_en,
    /// or of Localization_zh_TW for <see cref="TraditionalChinese"/>).
    /// </summary>
    public void AddClientTables(string zhCnJson, string targetJson)
    {
        using var zh = JsonDocument.Parse(File.ReadAllBytes(zhCnJson));
        using var en = JsonDocument.Parse(File.ReadAllBytes(targetJson));
        var enRows = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in en.RootElement.EnumerateObject()) enRows[p.Name] = p.Value;
        var votes = new OrderedDictionary<string, OrderedDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var (key, row) in JsOrder(zh.RootElement))
        {
            string? z = Des(row), e = enRows.TryGetValue(key, out var enRow) ? Des(enRow) : null;
            if (z == null || e == null || !HasCjk(z) || e.Trim().Length == 0 || e == "None") continue;
            if (Chinese ? !HasCjk(e) || e == z : HasCjk(e)) continue;
            if (!votes.TryGetValue(z, out var v)) votes[z] = v = new OrderedDictionary<string, int>(StringComparer.Ordinal);
            v[e] = v.TryGetValue(e, out var count) ? count + 1 : 1;
        }
        foreach (var (z, v) in votes)
        {
            if (_map.ContainsKey(z)) continue;
            // The most frequent English; on a tie the first seen (a stable sort by count, as the JS did).
            string best = "";
            int bestCount = 0;
            foreach (var (text, count) in v)
                if (count > bestCount) { best = text; bestCount = count; }
            _map[z] = best;
            FromTables++;
        }
    }

    /// <summary>Step 3: our translations, "zh\ttext" per line; each source is one file's text, applied in order.</summary>
    public void AddTranslations(IEnumerable<string> fileTexts)
    {
        foreach (var text in fileTexts)
        {
            foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
            {
                var l = raw.EndsWith('\r') ? raw[..^1] : raw;
                if (l.Length == 0 || l.StartsWith('#')) continue;
                int tab = l.IndexOf('\t');
                if (tab <= 0) continue;
                string zh = Unescape(l[..tab]), en = Unescape(l[(tab + 1)..]).Trim();
                if (en.Length == 0 || !HasCjk(zh) || (!Chinese && HasCjk(en))) continue;
                _map[zh] = en;
                Ours++;
            }
        }
    }

    /// <summary>The translation files of a folder, sorted by name (src/Ro3Emu.Loc/translations).</summary>
    public static IEnumerable<string> TranslationFiles(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.tsv").OrderBy(Path.GetFileName, StringComparer.Ordinal).Select(f => File.ReadAllText(f, Encoding.UTF8))
            : [];

    public void Write(string path)
    {
        // The first line starts the same in every language: ClientPatcher.GeneratedLocHeader tells our file from a hand-made one.
        var body = new StringBuilder(Chinese
            ? $"# zh\ten - generated by RO3Emu (language {TraditionalChinese}), do not edit (edit src/Ro3Emu.Loc/translations-zhtw/*.tsv)\n"
            : "# zh\ten - generated by tools/Loc/build_locmap.js, do not edit (edit src/Ro3Emu.Loc/translations/*.tsv)\n");
        foreach (var (zh, en) in _map) body.Append(Escape(zh)).Append('\t').Append(Escape(en)).Append('\n');
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, body.ToString(), new UTF8Encoding(false));
    }

    private static string? Des(JsonElement row) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty("_kDes", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

    /// <summary>Object.entries order: array-index keys ascending by value, then the other keys as written.</summary>
    private static IEnumerable<(string Key, JsonElement Value)> JsOrder(JsonElement obj)
    {
        var props = obj.EnumerateObject().Select(p => (p.Name, p.Value)).ToList();
        static bool Index(string k, out uint v) => uint.TryParse(k, System.Globalization.NumberStyles.None, null, out v) && v != uint.MaxValue && v.ToString() == k;
        var numeric = props.Where(p => Index(p.Name, out _)).OrderBy(p => uint.Parse(p.Name));
        return numeric.Concat(props.Where(p => !Index(p.Name, out _)));
    }

    private static List<Dictionary<string, string>> ReadTsv(string file)
    {
        var lines = File.ReadAllText(file, Encoding.UTF8).TrimStart('\uFEFF').Split('\n').ToList();
        var header = lines[0].TrimEnd('\r').Split('\t');
        lines.RemoveAt(0);
        return [.. lines.Where(l => l.Trim().Length > 0).Select(l =>
        {
            var cells = l.TrimEnd('\r').Split('\t');
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < header.Length; i++) row[header[i]] = Unescape(i < cells.Length ? cells[i] : "");
            return row;
        })];
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char c = s[++i];
                sb.Append(c switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => c });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
}
