using System.Text;

namespace Ro3Emu.Setup;

/// <summary>
/// RO3Emu.exe. Double-clicked it finds (or downloads) the game, exports the game data once, starts the bundled server,
/// patches the client and starts it. See <see cref="SetupRuntime.Usage"/> for the other modes.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }
        Ui.OwnsConsole();
        Console.CancelKeyPress += (_, e) =>
        {
            // While the server runs, the first Ctrl+C stops it cleanly; a second one (or one at any other time) ends RO3Emu.
            if (!SetupRuntime.Waiting || SetupRuntime.StopRequested) return;
            SetupRuntime.StopRequested = true;
            e.Cancel = true;
        };
        // Closing the window (SIGHUP is CTRL_CLOSE_EVENT on Windows): Windows allows about five seconds.
        using var close = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, _ =>
        {
            SetupLog.Write("window closed");
            try { SetupRuntime.OnClose?.Invoke(); } catch (Exception e) { SetupLog.Write("close: " + e.Message); }
        });

        Options options;
        try { options = Options.Parse(args); }
        catch (SetupException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(SetupRuntime.Usage);
            return 64;
        }
        if (options.Home != null) SetupHome.Root = options.Home;
        if (options.Yes) Ui.Interactive = false;
        if (options.Language != null)
        {
            try
            {
                var settings = Settings.Load();
                settings.Language = options.Language;
                settings.Save();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SetupLog.Write("settings: " + e.Message);
                Console.Error.WriteLine($"--lang {options.Language} could not be saved ({e.Message}).");
            }
        }
        SetupLog.Write($"RO3Emu {typeof(Program).Assembly.GetName().Version} {string.Join(' ', args)} (home {SetupHome.Root})");

        try
        {
            return options.Command switch
            {
                "help" => Help(),
                "start" => await Flows.PlayLocalAsync(options, host: false),
                "host" => await Flows.PlayLocalAsync(options, host: true),
                "join" => await Flows.JoinAsync(options),
                "panel" => await Flows.PanelAsync(options),
                "restore" => Flows.Restore(options),
                "check" => Flows.Check(options),
                "export" => Flows.Export(options),
                "install" => await InstallFlow.RunAsync(options, update: false),
                "update" => await InstallFlow.RunAsync(options, update: true),
                "diag" => Flows.Diag(options),
                _ => throw new SetupException($"Unknown command '{options.Command}'.", "Run RO3Emu.exe help."),
            };
        }
        catch (SetupException e)
        {
            SetupLog.Write("failed: " + e);
            Ui.Error(e.Message);
            Ui.Info("What to do: " + e.Fix);
            ReportFailure(options);
            return 1;
        }
        catch (Exception e)
        {
            SetupLog.Write("unexpected: " + e);
            Ui.Error("Something unexpected went wrong: " + e.Message);
            ReportFailure(options);
            return 2;
        }
    }

    private static int Help()
    {
        Console.WriteLine(SetupRuntime.Usage);
        return 0;
    }

    /// <summary>On any failure: the diagnostics zip, where to send it, and a pause so a double-click window stays readable.</summary>
    private static void ReportFailure(Options options)
    {
        try
        {
            var zip = DiagnosticsZip.Write(Flows.KnownClient(options));
            Ui.Info($"Details are in {zip}.");
            Ui.Info($"If you need help, open an issue at {SetupRuntime.IssuesUrl} and attach that file.");
            Ui.Reveal(zip);
        }
        catch (Exception e) { SetupLog.Write("diagnostics: " + e); }
        Ui.PauseIfOwnConsole();
    }
}
