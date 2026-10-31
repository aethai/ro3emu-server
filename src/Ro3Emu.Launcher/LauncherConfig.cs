using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ro3Emu.Launcher;

/// <summary>
/// launcher.json: built into the exe (LauncherConfigFile in the csproj); a file beside the exe wins. Two modes:
/// "operator" (a server's own launcher: its client repository, news, links) and "ro3emu" (RO3Emu's own app: the game
/// from Genesis' update servers, a local server, updates from our GitHub releases). Without "mode", a config that names a
/// "repository" is an operator's, anything else is RO3Emu's.
/// </summary>
public sealed class LauncherConfig
{
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("repository")] public string? Repository { get; set; }
    [JsonPropertyName("channel")] public string Channel { get; set; } = "test";
    [JsonPropertyName("server")] public string? Server { get; set; }
    /// <summary>The name on the title bar, the built-in hero, the tray and the shortcuts.</summary>
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = "RO3Emu";
    /// <summary>
    /// The hero picture: a file beside the exe, or a URL relative to the repository (an absolute URL works too).
    /// Null = &lt;repository&gt;/hero.jpg when it exists, else the page's built-in sky.
    /// </summary>
    [JsonPropertyName("hero")] public string? Hero { get; set; }
    /// <summary>The title bar's links (Website, Discord, Wiki…); none by default.</summary>
    [JsonPropertyName("links")] public List<LauncherLink> Links { get; set; } = [];
    /// <summary>
    /// The server's web panel (docs/guide/panel.md), for example https://panel.example.com: the title bar's Web panel
    /// entry opens it, in either mode. Null: an operator's launcher shows no entry, RO3Emu's own app the panel of the
    /// server it plays on (Ro3Emu.Setup.PanelLink).
    /// </summary>
    [JsonPropertyName("panelUrl")] public string? PanelUrl { get; set; }

    /// <summary>RO3Emu's own app rather than an operator's launcher (see the class summary).</summary>
    [JsonIgnore]
    public bool IsRo3EmuMode => Mode is { } mode
        ? string.Equals(mode, "ro3emu", StringComparison.OrdinalIgnoreCase)
        : string.IsNullOrWhiteSpace(Repository);

    /// <summary>The operator's repository; the old default for a config that names none in operator mode.</summary>
    [JsonIgnore]
    public string RepositoryUrl => string.IsNullOrWhiteSpace(Repository) ? "http://localhost:8790/" : Repository;

    static readonly JsonSerializerOptions Options = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static LauncherConfig Load(string besideExe)
    {
        try
        {
            if (File.Exists(besideExe)) return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllBytes(besideExe), Options) ?? new();
            using var stream = typeof(LauncherConfig).Assembly.GetManifestResourceStream("launcher.json");
            return stream is null ? new() : JsonSerializer.Deserialize<LauncherConfig>(stream, Options) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}

/// <summary>One link of the title bar: an http(s) URL opened in the player's browser.</summary>
public sealed class LauncherLink
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    /// <summary>Optional: globe, chat, book, trophy, flag, code or link; otherwise the page picks one from the label.</summary>
    [JsonPropertyName("icon")] public string? Icon { get; set; }
}

/// <summary>
/// Per-user choices, one file per launcher under %APPDATA%\RO3Emu: launcher-settings-ro3emu.json for RO3Emu's own app,
/// launcher-settings-&lt;repository&gt;.json for a server's launcher. Two servers' launchers on one PC must not share the
/// game folder they installed and patched, nor delete each other's on Uninstall.
/// </summary>
public sealed class UserSettings
{
    [JsonPropertyName("clientDirectory")] public string? ClientDirectory { get; set; }
    /// <summary>The Desktop / Start menu shortcut question was answered (asked once, on the first run).</summary>
    [JsonPropertyName("shortcutsAsked")] public bool ShortcutsAsked { get; set; }
    /// <summary>The game version whose "What's new" the player has seen; a newer installed version shows its notes once.</summary>
    [JsonPropertyName("notesSeenVersion")] public string? NotesSeenVersion { get; set; }
    /// <summary>
    /// The game folder this launcher created by installing into an empty or new folder: the only one Settings →
    /// Uninstall removes. A folder that already held a client (for example a Genesis ROO_PC) is never deleted.
    /// </summary>
    [JsonPropertyName("ownedDirectory")] public string? OwnedDirectory { get; set; }
    /// <summary>RO3Emu mode: the app version whose release notes were shown; a newer running version shows its notes once.</summary>
    [JsonPropertyName("appNotesSeenVersion")] public string? AppNotesSeenVersion { get; set; }

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RO3Emu");

    /// <summary>Before launchers had a file each, every one used this file.</summary>
    static string LegacyPath => Path.Combine(Folder, "launcher-settings.json");

    [JsonIgnore] string _path = "";

    /// <summary>RO3Emu's own app.</summary>
    public static UserSettings ForApp() => Load(Path.Combine(Folder, "launcher-settings-ro3emu.json"), legacy: false);

    /// <summary>
    /// A server's launcher, keyed by its repository address. The first start after the change takes over the shared
    /// file's choices once, so a player keeps the game folder the launcher already installed.
    /// </summary>
    public static UserSettings ForRepository(string repositoryUrl) =>
        Load(Path.Combine(Folder, "launcher-settings-" + FileKey(repositoryUrl) + ".json"), legacy: true);

    /// <summary>The repository address as a file name: host and path, anything else replaced by '_'.</summary>
    internal static string FileKey(string repositoryUrl)
    {
        string key = Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            ? uri.Host + (uri.IsDefaultPort ? "" : "_" + uri.Port) + uri.AbsolutePath : repositoryUrl;
        var chars = key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? char.ToLowerInvariant(c) : '_').ToArray();
        return new string(chars).Trim('_', '.');
    }

    static UserSettings Load(string path, bool legacy)
    {
        UserSettings settings;
        try
        {
            string? source = File.Exists(path) ? path : legacy && File.Exists(LegacyPath) ? LegacyPath : null;
            settings = source != null ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllBytes(source)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            settings = new();
        }
        settings._path = path;
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
