namespace Ro3Emu.LoadTest;

public sealed record RunnerOptions(string Scenario, int Bots, int TargetBots, int DurationSeconds,
    bool ServerLocal, string? ServerUrl, string? PemPath, string OutputPrefix, string RepositoryRoot)
{
    public string WalkPhase { get; init; } = "realistic";

    /// <summary>--operator-token-file: the remote server's DataDir/operator.token (a copy), for its metrics and GM tool.</summary>
    public string? OperatorTokenPath { get; init; }

    /// <summary>--snapshot-minutes: also save the server's cumulative metrics every N minutes of the run (0 = off).</summary>
    public int SnapshotMinutes { get; init; }
    /// <summary>--cast-interval-ms: S10, the time between one farmer's casts (0 = the capture's 690 ms).</summary>
    public int CastIntervalMs { get; init; }
    /// <summary>--disk-delay-ms: with --server-local, every database commit waits this long (a slow VPS disk; 0 = off).</summary>
    public int DiskDelayMs { get; init; }
    public static RunnerOptions Parse(string[] args)
    {
        if (args.Contains("--help") || args.Length == 0)
            throw new ArgumentException("Usage: --scenario S1|S2|S3|S4|S5|S6|S7|S8|S9|S10 (--server-local | --server URL) [--pem PATH] [--operator-token-file PATH] [--bots N] [--duration-seconds N] [--walk-phase realistic|sync] [--snapshot-minutes N] [--cast-interval-ms N] [--disk-delay-ms N] [--output PREFIX]");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool local = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server-local") { local = true; continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) throw new ArgumentException($"Invalid argument {args[i]}");
            values[args[i]] = args[++i];
        }
        string scenario = values.GetValueOrDefault("--scenario")?.ToUpperInvariant() ?? throw new ArgumentException("--scenario required");
        if (scenario is not ("S1" or "S2" or "S3" or "S4" or "S5" or "S6" or "S7" or "S8" or "S9" or "S10")) throw new ArgumentException("Unknown scenario");
        int target = scenario switch { "S1" or "S3" => 500, "S2" => 300, "S4" => 200, "S5" => 160, "S6" or "S7" or "S8" or "S9" => 100, "S10" => 11, _ => 0 };
        int duration = scenario switch { "S1" or "S4" => 60, "S2" or "S5" => 600, "S6" or "S7" or "S8" or "S9" => 300, "S10" => 3600, _ => 1800 };
        if (values.TryGetValue("--bots", out var b)) target = int.Parse(b);
        int requested = target;
        int qualifiedTarget = scenario switch { "S1" or "S3" => 500, "S4" => 200, "S5" => 160, "S6" or "S7" or "S8" or "S9" => 100, "S10" => 11, _ => 300 };
        if (values.TryGetValue("--duration-seconds", out var d)) duration = int.Parse(d);
        if (requested < 1 || duration < 1) throw new ArgumentException("Bot count and duration must be positive");
        string? url = values.GetValueOrDefault("--server");
        if (local == (url != null)) throw new ArgumentException("Specify exactly one of --server-local and --server URL");
        var repo = FindRepoRoot();
        string phase = values.GetValueOrDefault("--walk-phase", "realistic").ToLowerInvariant();
        if (phase is not ("realistic" or "sync")) throw new ArgumentException("--walk-phase must be realistic or sync");
        var prefix = values.GetValueOrDefault("--output") ?? Path.Combine(repo, ".loadtest", "runs", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{scenario}-{requested}");
        return new(scenario, requested, qualifiedTarget, duration, local, url,
            values.GetValueOrDefault("--pem"), Path.GetFullPath(prefix), repo)
        {
            WalkPhase = phase, OperatorTokenPath = values.GetValueOrDefault("--operator-token-file"),
            SnapshotMinutes = values.TryGetValue("--snapshot-minutes", out var m) ? int.Parse(m) : scenario == "S10" ? 10 : 0,
            CastIntervalMs = values.TryGetValue("--cast-interval-ms", out var c) ? int.Parse(c) : 0,
            DiskDelayMs = values.TryGetValue("--disk-delay-ms", out var disk) ? int.Parse(disk) : 0,
        };
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Run from the repository root");
    }
}
