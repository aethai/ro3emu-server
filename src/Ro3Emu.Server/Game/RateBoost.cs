using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The automatic rate boosts (inferred: an operator's knob, not client evidence). A boost is a stretch of the local day
/// during which every kill pays more EXP and drops more items, so the realm has a reason to keep farming; it is
/// announced in Discord when it opens and when it closes. The windows come from Ro3Emu:Boost:Windows, each written
/// "HHMM-HHMM:expPercent:dropPercent" ("2000-2200:100:50": +100 % EXP and +50 % drops from 20:00 to 22:00, local time;
/// a window may run past midnight). Ro3Emu:Boost:Enabled turns the feature on and Ro3Emu:Boost:DiscordWebhookUrl names
/// the channel webhook the alerts go to.
/// </summary>
public static class RateBoost
{
    /// <summary>EXP percent in force right now (0 = the plain rate).</summary>
    public static int ExpPercent { get; private set; }

    /// <summary>Drop percent in force right now (0 = the plain rate).</summary>
    public static int DropPercent { get; private set; }

    /// <summary>A boost is running.</summary>
    public static bool Active => ExpPercent > 0 || DropPercent > 0;

    internal static void Set(int exp, int drop)
    {
        ExpPercent = exp;
        DropPercent = drop;
    }

    /// <summary>A kill's EXP with the boost applied.</summary>
    public static long Exp(long amount) => amount <= 0 || ExpPercent <= 0 ? amount : amount + amount * ExpPercent / 100;

    /// <summary>One drop's count with the boost applied; a drop never shrinks.</summary>
    public static int Count(int count) => count <= 0 || DropPercent <= 0 ? count : Math.Max(count, count + count * DropPercent / 100);
}

/// <summary>One <see cref="RateBoost"/> window of the local day.</summary>
public sealed record BoostWindow(int StartMinute, int EndMinute, int ExpPercent, int DropPercent)
{
    /// <summary>"HHMM-HHMM:exp:drop" to a window; null when the text is malformed.</summary>
    public static BoostWindow? Parse(string? text)
    {
        string[] parts = (text ?? string.Empty).Split(':');
        if (parts.Length != 3) return null;
        string[] span = parts[0].Split('-');
        if (span.Length != 2 || !Minute(span[0], out int start) || !Minute(span[1], out int end)) return null;
        if (!int.TryParse(parts[1], out int exp) || !int.TryParse(parts[2], out int drop)) return null;
        return exp < 0 || drop < 0 ? null : new BoostWindow(start, end, exp, drop);
    }

    /// <summary>"HHMM" to minutes after midnight; 2400 is the end of the day.</summary>
    private static bool Minute(string text, out int minute)
    {
        minute = 0;
        if (text.Length is not (3 or 4) || !int.TryParse(text, out int clock) || clock % 100 >= 60) return false;
        minute = clock / 100 * 60 + clock % 100;
        return minute <= 1440;
    }

    /// <summary>The window holds <paramref name="minute"/> of the day; it may run past midnight.</summary>
    public bool Holds(int minute) => StartMinute <= EndMinute
        ? minute >= StartMinute && minute < EndMinute
        : minute >= StartMinute || minute < EndMinute;

    /// <summary>"20:00-22:00".</summary>
    public string Describe() => $"{StartMinute / 60:D2}:{StartMinute % 60:D2}-{EndMinute / 60 % 24:D2}:{EndMinute % 60:D2}";
}

/// <summary>
/// <see cref="RateBoost"/>: reads the windows, sets the rates for the minute at hand and posts the Discord alert when a
/// window opens or closes. The rates are in memory, so the first tick after a restart announces the window that is
/// already running.
/// </summary>
public sealed class RateBoostService(IOptions<ServerOptions> options, ILogger<RateBoostService> log, TimeProvider time)
    : BackgroundService
{
    private static readonly HttpClient Discord = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly BoostOptions _settings = options.Value.Boost;
    private readonly BoostWindow[] _windows =
        [.. options.Value.Boost.Windows.Select(BoostWindow.Parse).OfType<BoostWindow>()];
    private string? _announced; // the window whose alert already went out, null when none runs

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled) return Task.CompletedTask;
        if (_windows.Length == 0)
        {
            log.LogWarning("Rate boost: no window in Ro3Emu:Boost:Windows, the boosts stay off.");
            return Task.CompletedTask;
        }

        log.LogInformation("Rate boost: {Count} window(s): {Windows}", _windows.Length, string.Join(", ",
            _windows.Select(w => $"{w.Describe()} exp +{w.ExpPercent} % drops +{w.DropPercent} %")));
        if (string.IsNullOrWhiteSpace(_settings.DiscordWebhookUrl))
            log.LogWarning("Rate boost: no Ro3Emu:Boost:DiscordWebhookUrl, the alerts stay in this log.");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled || _windows.Length == 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20), time);
        while (!stoppingToken.IsCancellationRequested)
        {
            await TickAsync();
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The window of the minute at hand, its rates and the alert when it differs from the last tick.</summary>
    internal async Task TickAsync()
    {
        DateTimeOffset now = time.GetLocalNow();
        int minute = now.Hour * 60 + now.Minute;
        BoostWindow? window = _windows.FirstOrDefault(w => w.Holds(minute));
        string? running = window?.Describe();
        if (running == _announced) return;
        string? previous = _announced;
        _announced = running;
        RateBoost.Set(window?.ExpPercent ?? 0, window?.DropPercent ?? 0);
        if (window == null)
        {
            log.LogInformation("Rate boost: no window now, the plain rates are back.");
            if (previous != null)
                await PostAsync("Rate boost ended", "The bonus EXP and drops are over: the plain rates are back.", 0x8f979d);
            return;
        }

        log.LogInformation("Rate boost: {Window} in force, EXP +{Exp} %, drops +{Drop} %.",
            window.Describe(), window.ExpPercent, window.DropPercent);
        await PostAsync($"🔥 Rate boost — EXP +{window.ExpPercent} %, drops +{window.DropPercent} %",
            $"**EXP +{window.ExpPercent} %** and **drops +{window.DropPercent} %** until **{window.Describe()[^5..]}**. "
            + "Take the field, hire a party and keep farming!", 0xff5a36);
    }

    private async Task PostAsync(string title, string body, int colour)
    {
        if (string.IsNullOrWhiteSpace(_settings.DiscordWebhookUrl)) return;
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                username = "RO3Emu",
                embeds = new[] { new { title, description = body, color = colour, timestamp = time.GetUtcNow() } },
            });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var reply = await Discord.PostAsync(_settings.DiscordWebhookUrl, content);
            if (!reply.IsSuccessStatusCode)
                log.LogWarning("Rate boost: the Discord alert was refused with {Status}.", (int)reply.StatusCode);
        }
        catch (Exception error)
        {
            log.LogWarning(error, "Rate boost: the Discord alert could not be posted.");
        }
    }
}
