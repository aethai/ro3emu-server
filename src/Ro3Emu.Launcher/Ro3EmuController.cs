using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Ro3Emu.Setup;
using SetupSettings = Ro3Emu.Setup.Settings;

namespace Ro3Emu.Launcher;

/// <summary>
/// RO3Emu's own app (no operator launcher.json): the window over what RO3Emu.exe does (Ro3Emu.Setup's flows run
/// behind <see cref="Ui.Sink"/>), plus the app's updates and news from our GitHub releases.
/// Phases sent to the page: checking, nogame (no game found: download it or pick its folder), gameupdate (the game
/// does not fit: update it from Genesis' update servers), appupdate (a newer RO3Emu), updating (a download, pausable),
/// working (export / server start, not pausable), ready (Play), launching, running, error.
/// </summary>
public sealed class Ro3EmuController : ILauncherController, IUiSink
{
    public const string GitHubUrl = "https://github.com/aethai/ro3emu-server";
    public const string DiscordUrl = "https://discord.gg/vVkcvMFKy3";

    readonly string _exePath;
    readonly Action<object> _post;
    readonly Func<string, string?> _pickFolder;
    readonly Action<bool> _gameRunningChanged;
    readonly UserSettings _user = UserSettings.ForApp();
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly HttpClient _ping = new() { Timeout = TimeSpan.FromSeconds(3) };
    readonly GitHubReleases _github;
    readonly CancellationTokenSource _life = new();
    readonly Version _version = typeof(Ro3EmuController).Assembly.GetName().Version ?? new Version(1, 0, 0);
    /// <summary>A build from the source tree updates through git, never from the releases.</summary>
    readonly bool _fromSource = SetupHome.RepositoryRoot() != null;
    readonly Options _options = Options.Parse(["start", "--yes"]);

    string _phase = "checking";
    bool _started;
    CancellationTokenSource? _work;
    bool _busy;
    List<GitHubReleases.Release> _releases = [];
    GitHubReleases.Release? _appUpdate;
    bool _appUpdateLater;
    Flows.LocalServer? _server;
    string? _serverKind;
    /// <summary>The address and GM accounts the running host server was started with ("" for local play).</summary>
    string _serverSetup = "";
    bool _serverStarting;
    /// <summary>launcher.json "panelUrl": the Web panel entry's address whatever the server (PanelLink.ForLauncher).</summary>
    readonly string? _panelUrl;
    /// <summary>The joined server's web panel once found; <see cref="_joinProbing"/> while looking; <see cref="_joinProbe"/> drops an older join's answer.</summary>
    string? _joinedPanel;
    bool _joinProbing;
    int _joinProbe;
    Process? _game;
    string _unit = "bytes";
    string _stage = "";
    readonly SpeedMeter _meter = new();
    readonly Stopwatch _sinceProgress = Stopwatch.StartNew();

    public Ro3EmuController(string exePath, Action<object> post, Func<string, string?> pickFolder, Action<bool> gameRunningChanged, string? panelUrl = null)
    {
        _exePath = exePath;
        _panelUrl = panelUrl;
        _post = post;
        _pickFolder = pickFolder;
        _gameRunningChanged = gameRunningChanged;
        _github = new GitHubReleases(_http, Path.Combine(SetupHome.Root, "launcher-releases.json"));
        Ui.Interactive = false;
        Ui.Sink = this;
        GitHubReleases.DeleteLeftovers(SetupHome.AppDir);
    }

    public HeroImage? Hero => null;

    string AppVersion => _version.ToString(3);

    public async Task HandleAsync(string cmd, JsonElement message)
    {
        switch (cmd)
        {
            case "ready":
                await OnPageReadyAsync();
                break;
            case "primary":
                if (_busy) return;
                switch (_phase)
                {
                    case "appupdate": await UpdateAppAsync(); break;
                    case "nogame": await InstallGameAsync(InstallFlow.SuggestedFolder()); break;
                    case "gameupdate": await InstallGameAsync(Client()!); break;
                    case "ready": await PlayAsync("local", null, []); break;
                    case "error" or "offline": await CheckAsync(); break;
                }
                break;
            case "later":
                _appUpdateLater = true;
                await CheckAsync();
                break;
            case "pause":
                _work?.Cancel();
                break;
            case "repair":
                if (!_busy && Client() is { } client) await InstallGameAsync(client);
                break;
            case "changeDir":
                await ChangeDirectoryAsync();
                break;
            case "openDir":
                if (Client() is { } dir && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                break;
            case "host":
                if (!_busy) await PlayAsync("host", Text(message, "address"), Text(message, "gm")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []);
                break;
            case "join":
                if (!_busy) await JoinAsync(Text(message, "address") ?? "");
                break;
            case "stopServer":
                await StopServerAsync();
                await CheckAsync();
                break;
            case "shortcuts":
                CreateShortcuts(Flag(message, "desktop"), Flag(message, "startMenu"));
                break;
            case "notesSeen":
                break;
            case "panel":
                await OpenPanelAsync();
                break;
        }
    }

    static bool Flag(JsonElement message, string name) => message.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static string? Text(JsonElement message, string name) =>
        message.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;

    async Task OnPageReadyAsync()
    {
        PostSettings();
        if (!_started)
        {
            _started = true;
            _ = ServerLoopAsync();
            if (!_user.ShortcutsAsked) _post(new { type = "shortcuts", status = "ask" });
            await LoadReleasesAsync();
        }
        else PostNews();
        if (GameRunning()) ShowForeignGame();
        else await CheckAsync();
    }

    void PostSettings()
    {
        string? client = Client();
        _post(new
        {
            type = "settings",
            mode = "ro3emu",
            dir = client ?? "",
            launcher = AppVersion,
            serverName = "RO3Emu",
            links = new object[] { new { label = "GitHub", url = GitHubUrl, icon = "code" }, new { label = "Discord", url = DiscordUrl, icon = "chat" } },
            canUninstall = false,
            gameRelease = client is null ? null : ClientLocator.InstalledRelease(client),
            lanAddress = Flows.LanAddress(),
            serverRunning = _server != null,
            serverKind = _serverKind,
            dataDir = SetupHome.DataDir,
            panel = PanelBody(),
        });
    }

    void PostState(string phase, string? message = null, string? title = null, object? link = null)
    {
        _phase = phase;
        string? remote = phase == "appupdate" && _appUpdate?.Version is { } v ? v.ToString(3) : AppVersion;
        _post(new { type = "state", phase, installed = AppVersion, remote, message, title, link });
    }

    /// <summary>The game folder: the remembered one, else the only (or first) one found on this PC (then remembered).</summary>
    string? Client()
    {
        if (Flows.KnownClient(_options) is { } known) return known;
        var settings = SetupSettings.Load();
        var found = ClientLocator.Candidates(settings);
        if (found.Count == 0) return null;
        settings.ClientDir = found[0];
        settings.Save();
        return found[0];
    }

    async Task CheckAsync()
    {
        if (_busy) return;
        PostState("checking", "Looking for the game...");
        PostSettings();
        if (_appUpdate != null && !_appUpdateLater)
        {
            PostState("appupdate", "Only RO3Emu itself is replaced. Your game, characters and settings are kept.",
                $"RO3Emu {_appUpdate.Version!.ToString(3)} is available", new { text = "Later", cmd = "later" });
            return;
        }
        if (GameRunning())
        {
            ShowForeignGame();
            return;
        }
        // RO3Emu's own client DLLs (client\ next to the exe) missing is a broken RO3Emu folder, not an old game.
        if (Flows.HooksDll == null || Flows.LocDll == null)
        {
            PostState("error", $"RO3Emu's client files are missing ({(Flows.HooksDll == null ? "Ro3Emu.ClientHooks.dll" : "Ro3Emu.Loc.dll")} in the client folder). Extract the whole RO3Emu zip again.",
                "RO3Emu is incomplete");
            return;
        }
        string? client = await Task.Run(Client);
        if (client == null)
        {
            PostState("nogame", $"Download it from Genesis' update servers (about 24 GB) into {InstallFlow.SuggestedFolder()}, or pick the folder you have.",
                "Ragnarok 3 not found", new { text = "Pick game folder", cmd = "changeDir" });
            return;
        }
        try
        {
            await Task.Run(() => Flows.CheckClient(client, needExport: true));
        }
        catch (SetupException e)
        {
            PostState("gameupdate", FirstLine(e.Message) + " Update it from Genesis' update servers.", "The game needs an update",
                new { text = "Pick another folder", cmd = "changeDir" });
            return;
        }
        string release = ClientLocator.InstalledRelease(client) ?? "release unknown";
        PostState("ready", _server != null ? $"Game {release}. Your server is running." : $"Game {release}. Play starts your own server on this PC.");
    }

    /// <summary>A Setup message for the one-line status: its first line, with the first detail line when it lists some.</summary>
    static string FirstLine(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) return "";
        string first = lines[0].TrimEnd('.');
        return lines.Length > 1 ? $"{first.TrimEnd(':')}: {lines[1].TrimEnd('.')}." : first + ".";
    }

    /// <summary>Downloads the game (or the files that differ) from Genesis' update servers; pausable, continues where it stopped.</summary>
    async Task InstallGameAsync(string folder)
    {
        if (GameRunning())
        {
            PostState("error", "Close the game before updating it.");
            return;
        }
        await StopServerAsync();
        _busy = true;
        _work = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _unit = "bytes";
        PostState("updating");
        var token = _work.Token;
        try
        {
            await Task.Run(() => InstallFlow.InstallAsync(folder, _options, stop: token));
            if (ClientLocator.IsClient(folder))
            {
                var settings = SetupSettings.Load();
                settings.ClientDir = folder;
                settings.Save();
            }
            EndWork();
            await CheckAsync();
        }
        catch (OperationCanceledException)
        {
            EndWork();
            PostState(ClientLocator.IsClient(folder) ? "gameupdate" : "nogame", "Paused. Downloaded parts are kept; continue any time.", "Download paused");
        }
        catch (Exception e)
        {
            EndWork();
            Fail(e);
        }
    }

    /// <summary>Play (on this PC) or Host (for friends): game data, the server, the patch, then the game.</summary>
    async Task PlayAsync(string kind, string? publicAddress, IReadOnlyList<string> gms)
    {
        if (Client() is not { } client)
        {
            await CheckAsync();
            return;
        }
        if (GameRunning())
        {
            PostState("running");
            return;
        }
        _busy = true;
        try
        {
            var server = await EnsureServerAsync(client, kind, publicAddress, gms);
            await LaunchAsync(client, kind == "host" ? $"Friends join with the address {server.PublicHost}." : null);
        }
        catch (Exception e)
        {
            _serverStarting = false;
            Fail(e);
        }
        finally
        {
            _busy = false;
            PostServer();
        }
    }

    /// <summary>The server Play or Host needs: the running one when it fits, else game data, then a new server.</summary>
    async Task<Flows.LocalServer> EnsureServerAsync(string client, string kind, string? publicAddress, IReadOnlyList<string> gms)
    {
        // Hosting again with another address or other GM accounts needs a server started with them.
        string setup = kind == "host" ? $"{publicAddress}|{string.Join(',', gms)}" : "";
        if (_server is { } running && _serverKind == kind && _serverSetup == setup && running.Process is not { HasExited: true }) return running;
        await StopServerAsync();
        _unit = "items";
        PostState("working", null, "Getting ready");
        await Task.Run(() =>
        {
            Flows.CheckClient(client, needExport: true);
            Flows.EnsureGameData(client, force: false);
        });
        _unit = "none";
        _serverStarting = true;
        PostServer();
        var server = await Task.Run(() => Flows.StartServerAsync(client, kind == "host", publicAddress, gms, _options.Ports));
        _server = server;
        _serverKind = kind;
        _serverSetup = setup;
        _serverStarting = false;
        PostServer();
        PostSettings();
        return server;
    }

    /// <summary>The local server without the game, for the Web panel entry while it is stopped: the same start as Play.</summary>
    async Task<bool> StartServerOnlyAsync()
    {
        if (Client() is not { } client)
        {
            await CheckAsync();
            return false;
        }
        _busy = true;
        try
        {
            await EnsureServerAsync(client, "local", null, []);
        }
        catch (Exception e)
        {
            _serverStarting = false;
            Fail(e);
            return false;
        }
        finally
        {
            _busy = false;
            PostServer();
        }
        await CheckAsync();
        return true;
    }

    /// <summary>Someone else's server: patch the game for it and start the game (our own server is not needed).</summary>
    async Task JoinAsync(string address)
    {
        if (Flows.ServerUrl(address) is not { } url)
        {
            PostState("error", $"'{address}' is not a server address. Use the address the server owner gave you, for example 203.0.113.10.");
            return;
        }
        if (Client() is not { } client)
        {
            await CheckAsync();
            return;
        }
        _busy = true;
        try
        {
            await StopServerAsync();
            _unit = "none";
            PostState("working", null, $"Contacting {url}");
            await Task.Run(() =>
            {
                Flows.CheckClient(client, needExport: false);
                return Flows.PrepareJoinAsync(client, url);
            });
            _serverKind = "join";
            _ = FindJoinedPanelAsync(url);
            await LaunchAsync(client, $"Playing on {url}.");
        }
        catch (Exception e) { Fail(e); }
        finally { _busy = false; }
    }

    async Task LaunchAsync(string client, string? note)
    {
        PostState("launching", note);
        var game = await Task.Run(() => Flows.LaunchGame(client));
        _game = game;
        game.EnableRaisingEvents = true;
        game.Exited += (_, _) => System.Windows.Application.Current.Dispatcher.InvokeAsync(() => OnGameExitedAsync(game));
        PostState("running", note);
        _gameRunningChanged(true);
    }

    async Task OnGameExitedAsync(Process game)
    {
        if (_game == game) _game = null;
        game.Dispose();
        _gameRunningChanged(false);
        await CheckAsync();
    }

    static bool GameRunning() => Process.GetProcessesByName("ro3").Length > 0;

    /// <summary>
    /// A game this launcher did not start (open before it, or started some other way): "running" until it closes, then
    /// the usual check. Without the watch the page stayed on Running after that game closed.
    /// </summary>
    void ShowForeignGame()
    {
        PostState("running");
        foreach (var process in Process.GetProcessesByName("ro3"))
        {
            if (_game != null && process.Id == _game.Id) { process.Dispose(); continue; }
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    process.Dispose();
                    if (_game == null && !GameRunning()) await CheckAsync();
                });
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                process.Dispose();
            }
        }
    }

    async Task StopServerAsync()
    {
        if (_server is not { } server) return;
        _server = null;
        _serverKind = null;
        if (server.Process is { } process) await Task.Run(() => Flows.StopServer(process));
        PostServer();
        PostSettings();
    }

    void Fail(Exception e)
    {
        SetupLog.Write("launcher: " + e);
        string text = e is SetupException s ? $"{FirstLine(s.Message)} {s.Fix}" : e.Message;
        PostState("error", text);
    }

    void EndWork()
    {
        _work?.Dispose();
        _work = null;
        _busy = false;
    }

    async Task ChangeDirectoryAsync()
    {
        if (_busy) return;
        string? folder = _pickFolder("Ragnarok 3 game folder (the one with ro3.exe)");
        if (folder is null) return;
        string? client = new[] { folder, Path.Combine(folder, "ROO_PC") }.FirstOrDefault(ClientLocator.IsClient);
        if (client == null)
        {
            PostState("nogame", $"{folder} has no ro3.exe. Pick the folder that contains it, or download the game.", "That is not the game folder",
                new { text = "Pick game folder", cmd = "changeDir" });
            return;
        }
        var settings = SetupSettings.Load();
        settings.ClientDir = Path.GetFullPath(client);
        settings.Save();
        await StopServerAsync();
        PostSettings();
        await CheckAsync();
    }

    // ---------- the Web panel entry ----------

    /// <summary>
    /// The title bar's Web panel entry (PanelLink.ForLauncher): launcher.json's panelUrl, else the joined server's panel,
    /// else the local server's (started by the entry when it is not running).
    /// </summary>
    PanelLink.Entry? PanelEntry() => PanelLink.ForLauncher(_panelUrl, ro3emuMode: true, joined: _serverKind == "join", _joinedPanel, _joinProbing,
        LocalPanel(), localRunning: _server != null && _server.Process is not { HasExited: true });

    /// <summary>The local server's panel: the ports it runs with (or will start with) and the bind address of its settings.</summary>
    string? LocalPanel() => Flows.LocalPanelUrl(_server?.Ports ?? _options.Ports, host: _serverKind == "host");

    object? PanelBody() => PanelEntry() is { } entry ? new { state = entry.State, url = entry.Url, hint = entry.Hint } : null;

    async Task OpenPanelAsync()
    {
        var entry = PanelEntry();
        if (entry?.State == PanelLink.Start)
        {
            if (_busy || _phase != "ready")
            {
                Toast("Your server is not running. Press Play to start it, then open the web panel.");
                return;
            }
            if (!await StartServerOnlyAsync()) return;
            entry = PanelEntry();
        }
        if (entry is not { State: PanelLink.Open, Url: { } url })
        {
            if (entry?.Hint is { } hint) Toast(hint);
            return;
        }
        // The local panel can be off in the server's own settings (Ro3Emu:Panel:Enabled); a joined one answered already.
        if (url == LocalPanel() && !await PanelLink.AnswersAsync(_ping, url, _life.Token))
        {
            Toast($"Your server's web panel does not answer on {url}. It may be turned off in the server's settings.");
            return;
        }
        MainWindow.OpenExternal(url);
    }

    /// <summary>After a join: the joined server's web panel, when it answers where RO3Emu puts it (PanelLink.JoinCandidates).</summary>
    async Task FindJoinedPanelAsync(string serverUrl)
    {
        int probe = ++_joinProbe;
        _joinedPanel = null;
        _joinProbing = true;
        PostSettings();
        string? panel = await PanelLink.FirstAnsweringAsync(PanelLink.JoinCandidates(serverUrl), Flows.PanelTimeout, _life.Token);
        if (probe != _joinProbe) return;
        _joinedPanel = panel;
        _joinProbing = false;
        SetupLog.Write($"launcher: web panel of {serverUrl}: {panel ?? "none"}");
        PostSettings();
    }

    void Toast(string text) => _post(new { type = "toast", text });

    // ---------- the local server pill ----------

    async Task ServerLoopAsync()
    {
        while (!_life.IsCancellationRequested)
        {
            PostServer();
            try { await Task.Delay(TimeSpan.FromSeconds(5), _life.Token); }
            catch (TaskCanceledException) { return; }
        }
    }

    void PostServer()
    {
        if (_serverStarting)
        {
            _post(new { type = "server", online = false, starting = true, label = "Your server is starting" });
            return;
        }
        if (_server is not { } server || server.Process is { HasExited: true })
        {
            if (_server != null && _server.Process is { HasExited: true })
            {
                _server = null;
                _serverKind = null;
            }
            _post(new { type = "server", online = false, label = _serverKind == "join" ? "Playing on another server" : "Your server is stopped" });
            return;
        }
        _ = PostPlayersAsync(server);
    }

    async Task PostPlayersAsync(Flows.LocalServer server)
    {
        int? players = null;
        try
        {
            using var status = await _ping.GetAsync($"http://127.0.0.1:{server.Ports.Http}/ro3emu/status", _life.Token);
            if (status.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync(_life.Token));
                if (json.RootElement.TryGetProperty("players", out var p) && p.TryGetInt32(out int n)) players = n;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
        _post(new { type = "server", online = true, players, label = _serverKind == "host" ? $"Hosting on {server.PublicHost}" : "Your server is running" });
    }

    // ---------- releases: news, What's new, the app's own update ----------

    async Task LoadReleasesAsync()
    {
        _releases = await _github.LoadAsync(_life.Token);
        PostNews();
        var newest = _releases.Where(r => r.Version != null && r.Windows != null).MaxBy(r => r.Version);
        if (!_fromSource && newest?.Version is { } v && v > _version) _appUpdate = newest;
        ShowNotesIfNew();
    }

    void PostNews() => _post(new
    {
        type = "news",
        items = _releases.Take(4).Select(r => new
        {
            tag = "Release",
            date = r.Published == default ? null : r.Published.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            title = r.Name,
            text = GitHubReleases.Summary(r.Body),
            url = r.Url,
        }),
    });

    /// <summary>After an update: the running version's release notes, once.</summary>
    void ShowNotesIfNew()
    {
        string current = AppVersion;
        string? seen = _user.AppNotesSeenVersion;
        if (seen != current)
        {
            _user.AppNotesSeenVersion = current;
            _user.Save();
        }
        // The first start has nothing to compare with; a build from source has no release of its own.
        if (seen == null || seen == current || _fromSource) return;
        if (_releases.FirstOrDefault(r => r.Version?.ToString(3) == current) is not { } release) return;
        var points = GitHubReleases.Points(release.Body);
        if (points.Count > 0) _post(new { type = "notes", version = current, items = points });
    }

    async Task UpdateAppAsync()
    {
        if (_appUpdate is not { Windows: { } asset } release) return;
        if (GameRunning())
        {
            PostState("error", "Close the game before updating RO3Emu.");
            return;
        }
        _busy = true;
        _work = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _unit = "bytes";
        PostState("updating", null, $"Downloading RO3Emu {release.Version!.ToString(3)}");
        try
        {
            var meter = new SpeedMeter();
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                if (_sinceProgress.ElapsedMilliseconds < 150 && p.Done < p.Total) return;
                _sinceProgress.Restart();
                var (rate, eta) = meter.Sample(p.Done, p.Total);
                _post(new { type = "progress", stage = "Downloading RO3Emu", done = p.Done, total = p.Total, rate, eta, unit = "bytes" });
            });
            string zip = await _github.DownloadAsync(asset, SetupHome.DownloadDir, progress, _work.Token);
            await StopServerAsync();
            _post(new { type = "progress", stage = "Installing the update", done = 0, total = 0, unit = "none" });
            await Task.Run(() => GitHubReleases.Apply(zip, SetupHome.AppDir));
            try { File.Delete(zip); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Process.Start(new ProcessStartInfo(_exePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_exePath)! });
            System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
        }
        catch (OperationCanceledException)
        {
            EndWork();
            PostState("appupdate", "Paused. Start the update again any time.", $"RO3Emu {release.Version!.ToString(3)} is available",
                new { text = "Later", cmd = "later" });
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            EndWork();
            PostState("error", $"The update could not be installed: {e.Message} Nothing was changed.");
        }
    }

    void CreateShortcuts(bool desktop, bool startMenu)
    {
        var failed = desktop || startMenu ? Shortcuts.Create(_exePath, "RO3Emu", desktop, startMenu) : [];
        _user.ShortcutsAsked = true;
        _user.Save();
        _post(new { type = "shortcuts", status = failed.Count == 0 ? "done" : "error", created = desktop || startMenu, failed });
    }

    // ---------- Ui.Sink: the flows' messages and progress ----------

    void IUiSink.Message(UiKind kind, string text)
    {
        if (kind == UiKind.Step)
        {
            _stage = text;
            if (_phase is "working" or "updating") _post(new { type = "progress", stage = text, done = 0, total = 0, unit = "none" });
        }
    }

    void IUiSink.Progress(string stage, long done, long total)
    {
        if (_sinceProgress.ElapsedMilliseconds < 150 && done < total && stage == _stage) return;
        _sinceProgress.Restart();
        _stage = stage;
        string unit = _unit;
        if (stage.StartsWith("Loading", StringComparison.Ordinal)) unit = "none";
        var (rate, eta) = unit == "bytes" && stage == "Downloading" ? _meter.Sample(done, total) : (0, 0);
        _post(new { type = "progress", stage, done, total, rate, eta, unit });
    }

    public void Dispose()
    {
        _life.Cancel();
        _work?.Cancel();
        Ui.Sink = null;
        // Closing the window stops the server it started (the game, if still open, loses its server).
        if (_server?.Process is { } process)
        {
            try { process.Stop(TimeSpan.FromSeconds(8)); process.Dispose(); }
            catch (Exception e) { SetupLog.Write("stop on close: " + e.Message); }
        }
        _game?.Dispose();
        _http.Dispose();
        _ping.Dispose();
    }
}
