using Ro3Emu.ClientPatching;

namespace Ro3Emu.Setup;

/// <summary>Shared CLI metadata and cooperative stop hooks between RO3Emu.exe and setup flows.</summary>
internal static class SetupRuntime
{
    public const string IssuesUrl = "https://github.com/aethai/ro3emu-server/issues";

    public const string Usage = """
        RO3Emu.exe [command] [options]

          start              (default) play on your own server on this PC: find the game, export the game data
                             (first run, a few minutes), start the server, patch and start the game
          host               like start, but friends on your network or the Internet can join
                               --public <ip>     the address friends connect to (default: this PC's LAN address)
                               --gm <account>    an account that gets GM commands (repeatable)
          join <address>     play on someone else's server: host, host:port or http://host:port
          panel [address]    open the server's web panel in your browser (and print its address): the
                             server on this PC (its --panel-port), or the server at the address
          install [folder]   download the game from Genesis' update servers (about 24 GB), every file
                             checked against Genesis' signature
          update [folder]    bring the game up to date from Genesis' update servers (only changed files)
                               --server-files    only what a server needs for its export (Linux hosts)
          restore            undo the patch (the game talks to Genesis' servers again)
          check              check that the game folder fits this RO3Emu, without changing anything
          export <game> <data>  export the game data only (server hosts, Linux)
          diag               write RO3Emu-diagnostics.zip for a bug report

        options:
          --client <folder>  the game folder (the one with ro3.exe); remembered for next time
          --reexport         export the game data again
          --no-icons         no pictures for the web panel (item, monster, class, map and skill images from
                             the game; about 20 seconds and 120 MB): the panel shows letters instead
          --no-launch        do not start the game (server only)
          --yes              no questions: take the defaults
          --http-port <n> --micro-port <n> --game-port <n>   server ports (default 5380, 20000, 33100)
          --voice-port <n>   turn on party voice chat on this TCP port, for example 33101 (off by default)
          --panel-port <n>   the server's web panel on this TCP port (default 5381)
          --no-panel         no web panel
          --home <folder>    where RO3Emu keeps data, logs and settings (default: next to RO3Emu.exe)
          --purchase-url <address>  the in-game purchase / recharge buttons open this http(s) address (start,
                             host, join; give it every time). Without it they open nothing, never Genesis' store
        """;

    /// <summary>Ctrl+C or Q while the server runs: the wait ends and the server is stopped cleanly.</summary>
    public static volatile bool StopRequested;

    /// <summary>True while RO3Emu waits with a running server (Ctrl+C then stops it cleanly instead of ending at once).</summary>
    public static volatile bool Waiting;

    /// <summary>Run when the console window is closed: the server gets a few seconds to save and stop.</summary>
    public static Action? OnClose;
}

/// <summary>The command line.</summary>
internal sealed class Options
{
    public string Command { get; private set; } = "start";
    public List<string> Positional { get; } = [];
    public string? ClientDir { get; private set; }
    public string? Home { get; private set; }
    public bool Reexport { get; private set; }
    public bool NoLaunch { get; private set; }
    public bool Yes { get; private set; }
    public string? PublicHost { get; private set; }
    public string? PurchaseUrl { get; private set; }
    public List<string> GmAccounts { get; } = [];
    public string? Only { get; private set; }
    public bool ListInputs { get; private set; }
    public bool ServerFiles { get; private set; }
    public bool NoIcons { get; private set; }
    public ServerProcess.Ports Ports { get; private set; } = new();

    private static readonly HashSet<string> Commands = ["start", "host", "join", "panel", "restore", "check", "export", "install", "update", "diag", "help"];

    public static Options Parse(string[] args)
    {
        var o = new Options();
        int i = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            var command = args[0].ToLowerInvariant();
            if (!Commands.Contains(command)) throw new SetupException($"Unknown command '{args[0]}'.", "Run RO3Emu.exe help.");
            o.Command = command;
            i = 1;
        }
        string Value(string name) => i + 1 < args.Length ? args[++i] : throw new SetupException($"{name} needs a value.", "Run RO3Emu.exe help.");
        int Port(string name) => int.TryParse(Value(name), out var p) && p is > 0 and < 65536 ? p : throw new SetupException($"{name} needs a port number.", "Run RO3Emu.exe help.");
        for (; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--client": o.ClientDir = Value("--client"); break;
                case "--home": o.Home = Value("--home"); break;
                case "--reexport": o.Reexport = true; break;
                case "--no-launch": o.NoLaunch = true; break;
                case "--yes": case "-y": o.Yes = true; break;
                case "--public": o.PublicHost = Value("--public"); break;
                case "--purchase-url":
                    o.PurchaseUrl = Value("--purchase-url") is var url && OpenUrlPatch.IsPurchaseUrl(url) ? url
                        : throw new SetupException($"--purchase-url needs an http or https address, not '{url}'.", "Run RO3Emu.exe help.");
                    break;
                case "--gm": o.GmAccounts.Add(Value("--gm")); break;
                case "--only": o.Only = Value("--only"); break;
                case "--list-inputs": o.ListInputs = true; break;
                case "--server-files": o.ServerFiles = true; break;
                case "--no-icons": o.NoIcons = true; break;
                case "--http-port": o.Ports = o.Ports with { Http = Port("--http-port") }; break;
                case "--micro-port": o.Ports = o.Ports with { MicroGate = Port("--micro-port") }; break;
                case "--game-port": o.Ports = o.Ports with { GameGate = Port("--game-port") }; break;
                case "--voice-port": o.Ports = o.Ports with { Voice = Port("--voice-port") }; break;
                case "--panel-port": o.Ports = o.Ports with { Panel = Port("--panel-port") }; break;
                case "--no-panel": o.Ports = o.Ports with { Panel = 0 }; break;
                case "-h": case "--help": o.Command = "help"; break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new SetupException($"Unknown option {args[i]}.", "Run RO3Emu.exe help.");
                    o.Positional.Add(args[i]);
                    break;
            }
        }
        return o;
    }
}
