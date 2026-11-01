using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Ro3Emu.Patcher;

namespace Ro3Emu.Launcher;

/// <summary>
/// The launcher's logic behind the page: checks the channel, installs/updates/repairs the client with
/// <see cref="Updater"/>, updates the launcher itself, pings the game server and starts ro3.exe.
/// Messages to the page: state, progress, server, settings, news, hero, notes, shortcuts, uninstall (see ui/app.js).
/// </summary>
public sealed partial class LauncherController : ILauncherController
{
    readonly LauncherConfig _config;
    readonly string _exePath;
    readonly Action<object> _post;
    readonly Func<string, string?> _pickFolder;
    readonly Action<bool> _gameRunningChanged;
    readonly UserSettings _settings;
    readonly RepositoryClient _repo;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    readonly string _version = typeof(LauncherController).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    readonly CancellationTokenSource _life = new();
    InstallLayout _layout;
    ChannelInfo? _channel;
    LocalState? _installed;
    string _phase = "checking";
    CancellationTokenSource? _work;
    Process? _game;
    bool _started;

    public LauncherController(LauncherConfig config, string exePath, Action<object> post, Func<string, string?> pickFolder, Action<bool> gameRunningChanged)
    {
        _config = config;
        _exePath = exePath;
        _post = post;
        _pickFolder = pickFolder;
        _gameRunningChanged = gameRunningChanged;
        _settings = UserSettings.ForRepository(config.RepositoryUrl);
        _repo = new RepositoryClient(new Uri(config.RepositoryUrl));
        _layout = new InstallLayout(_settings.ClientDirectory ?? Path.Combine(Path.GetDirectoryName(exePath)!, "Client"));
    }

    /// <summary>The hero picture once it is loaded (served to the page as "hero-image"); null = the built-in one.</summary>
    public HeroImage? Hero { get; private set; }

    public async Task HandleAsync(string cmd, JsonElement message)
    {
        switch (cmd)
        {
            case "ready":
                await OnPageReadyAsync();
                break;
            case "primary":
                if (_work != null) return;
                if (_phase == "ready") await PlayAsync();
                else if (_phase is "install" or "update" or "repair") await UpdateAsync(verifyAll: _phase == "repair");
                else if (_phase is "offline" or "error") await CheckAsync();
                break;
            case "repair":
                if (_work == null && _phase != "running") await UpdateAsync(verifyAll: true);
                break;
            case "pause":
                _work?.Cancel();
                break;
            case "changeDir":
                await ChangeDirectoryAsync();
                break;
            case "openDir":
                if (Directory.Exists(_layout.GameDirectory))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_layout.GameDirectory}\"") { UseShellExecute = true });
                break;
            case "shortcuts":
                CreateShortcuts(Flag(message, "desktop"), Flag(message, "startMenu"));
                break;
            case "notesSeen":
                break; // remembered when the notes were shown
            case "uninstall":
                await UninstallAsync();
                break;
            case "panel":
                if (PanelUrl() is { } panel) MainWindow.OpenExternal(panel);
                break;
        }
    }

    static bool Flag(JsonElement message, string name) => message.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    async Task OnPageReadyAsync()
    {
        PostSettings();
        if (!_started)
        {
            _started = true;
            _ = PingLoopAsync();
            _ = LoadNewsAsync();
            _ = LoadHeroAsync();
            // first run: offer Desktop / Start menu shortcuts (asked once; the launcher never moves itself)
            if (!_settings.ShortcutsAsked) _post(new { type = "shortcuts", status = "ask" });
        }
        else if (Hero is not null) PostHero();
        if (IsGameRunning()) PostState("running");
        else await CheckAsync();
    }

    void PostSettings() => _post(new
    {
        type = "settings",
        dir = _layout.GameDirectory,
        launcher = _version,
        serverName = _config.ServerName,
        links = _config.Links.Where(l => l.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || l.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)).Select(l => new { label = l.Label, url = l.Url, icon = l.Icon }),
        canUninstall = CanUninstall(),
        panel = PanelUrl() is { } url ? new { state = Ro3Emu.Setup.PanelLink.Open, url } : null,
    });

    /// <summary>The title bar's Web panel entry: only the operator's "panelUrl" (an operator's launcher looks for no panel itself).</summary>
    string? PanelUrl() => Ro3Emu.Setup.PanelLink.Configured(_config.PanelUrl);

    void PostState(string phase, string? message = null, long? downloadBytes = null)
    {
        _phase = phase;
        _post(new { type = "state", phase, installed = _installed?.Version, remote = _channel?.Version, message, downloadBytes });
    }

    async Task CheckAsync()
    {
        PostState("checking");
        try
        {
            var updater = new Updater(_layout, _repo);
            var check = await updater.CheckAsync(_config.Channel, _life.Token);
            _channel = check.Channel;
            _installed = check.Installed;
            if (await SelfUpdateAsync()) return;
            RememberInstalledVersion();
            if (check.Installed is null) await PostInstallAsync();
            else if (check.UpdateRequired) PostState("update", $"Version {_channel.Version} is available.");
            else if (updater.QuickVerify(check.Installed) is { Count: > 0 } changed) PostState("repair", $"{changed.Count} game files differ from the release.");
            else
            {
                PostState("ready");
                await ShowNotesIfNewAsync();
            }
        }
        catch (Exception e) when (e is PatcherException or HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            _installed = new Updater(_layout, _repo).LoadState();
            // The patcher's own words say why (a repository that is not https, a missing channel); a network error has none.
            string? reason = e is PatcherException ? e.Message : null;
            // Offline but installed: the game can still start (the update server and the game server are separate).
            if (_installed is not null) PostState("ready", (reason ?? "Can't reach the update server.") + " Playing the installed version.");
            else PostState("offline", reason);
        }
        PostSettings();
    }

    /// <summary>A newer launcher in the channel: download it, restart into it and return true.</summary>
    async Task<bool> SelfUpdateAsync()
    {
        if (_channel?.Launcher is not { } release || !SelfUpdate.IsNewer(release.Version, _version)) return false;
        PostState("checking", $"Updating the launcher to {release.Version}…");
        if (!await SelfUpdate.ApplyAsync(_repo, release, _version, _exePath, _life.Token)) return false;
        SelfUpdate.Restart(_exePath);
        System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
        return true;
    }

    /// <summary>Not installed: into a folder that already has the client only the changed files come; else everything.</summary>
    async Task PostInstallAsync()
    {
        if (File.Exists(Path.Combine(_layout.GameDirectory, "ro3.exe")))
        {
            PostState("install", "Found an RO3 client in this folder: only the changed files will be downloaded.");
            return;
        }
        long? bytes = null;
        try
        {
            // A fresh install downloads every distinct chunk once.
            var (manifest, _) = await _repo.GetManifestAsync(_channel!, _life.Token);
            bytes = manifest.Files.SelectMany(f => f.Chunks).DistinctBy(c => c.Hash).Sum(c => (long)c.StoredSize);
        }
        catch (Exception e) when (e is PatcherException or HttpRequestException or TaskCanceledException or IOException or JsonException) { }
        PostState("install", downloadBytes: bytes);
    }

    async Task UpdateAsync(bool verifyAll)
    {
        if (_channel is null)
        {
            await CheckAsync();
            if (_channel is null) return;
        }
        if (IsGameRunning())
        {
            PostState("error", "Close the game before updating.");
            return;
        }
        ClaimFreshDirectory();
        Directory.CreateDirectory(_layout.GameDirectory);
        _work = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        PostState("updating");
        try
        {
            var updater = new Updater(_layout, _repo);
            var channel = _channel;
            var token = _work.Token;
            var progress = ProgressReporter();
            await Task.Run(() => updater.UpdateAsync(channel, verifyAll, progress, token));
            EndWork();
            await CheckAsync();
        }
        catch (OperationCanceledException)
        {
            EndWork();
            PostState(_installed is null ? "install" : "update", "Paused. Downloaded parts are kept; continue any time.");
        }
        catch (Exception e)
        {
            EndWork();
            PostState("error", e.Message);
        }
    }

    void EndWork()
    {
        _work?.Dispose();
        _work = null;
    }

    ThrottledProgress ProgressReporter()
    {
        var meter = new SpeedMeter();
        return new ThrottledProgress(p =>
        {
            var (rate, eta) = p.Stage == UpdateStage.Downloading ? meter.Sample(p.BytesDone, p.BytesTotal) : (0, 0);
            _post(new { type = "progress", stage = p.Stage.ToString().ToLowerInvariant(), done = p.BytesDone, total = p.BytesTotal, rate, eta, file = p.File });
        });
    }

    async Task PlayAsync()
    {
        PostState("launching");
        try
        {
            var start = await GameStarter.StartAsync(_layout.GameDirectory);
            if (start.Game is not { } game)
            {
                PostState("repair", $"The game did not start: {start.Failure}.");
                return;
            }
            _game = game;
            game.EnableRaisingEvents = true;
            game.Exited += (_, _) => System.Windows.Application.Current.Dispatcher.InvokeAsync(() => OnGameExitedAsync(game));
            PostState("running");
            _gameRunningChanged(true);
        }
        catch (Exception e)
        {
            PostState("error", e.Message);
        }
    }

    /// <summary>The game closed: the launcher comes back from the tray and looks for updates again.</summary>
    async Task OnGameExitedAsync(Process game)
    {
        if (_game == game) _game = null;
        game.Dispose();
        _gameRunningChanged(false);
        await CheckAsync();
    }

    /// <summary>The game this launcher started, or any ro3.exe of the game folder (started before this launcher).</summary>
    bool IsGameRunning()
    {
        if (_game is { HasExited: false }) return true;
        string exe = Path.GetFullPath(Path.Combine(_layout.GameDirectory, "ro3.exe"));
        foreach (var process in Process.GetProcessesByName("ro3"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }

    async Task ChangeDirectoryAsync()
    {
        if (_work != null || _phase == "running") return;
        string? folder = _pickFolder("Game folder (an existing ROO_PC folder or an empty one)");
        if (folder is null) return;
        // An existing client (ro3.exe) or an empty folder is used as is; anything else gets its own subfolder.
        bool usable = File.Exists(Path.Combine(folder, "ro3.exe")) || !Directory.EnumerateFileSystemEntries(folder).Any();
        if (!usable) folder = Path.Combine(folder, "RO3Emu");
        _settings.ClientDirectory = folder;
        _settings.Save();
        _layout = new InstallLayout(folder);
        _installed = null;
        PostSettings();
        await CheckAsync();
    }

    void CreateShortcuts(bool desktop, bool startMenu)
    {
        var failed = desktop || startMenu ? Shortcuts.Create(_exePath, _config.ServerName, desktop, startMenu) : [];
        _settings.ShortcutsAsked = true;
        _settings.Save();
        _post(new { type = "shortcuts", status = failed.Count == 0 ? "done" : "error", created = desktop || startMenu, failed });
    }

    public void Dispose()
    {
        _life.Cancel();
        _work?.Cancel();
        _game?.Dispose();
    }
}
