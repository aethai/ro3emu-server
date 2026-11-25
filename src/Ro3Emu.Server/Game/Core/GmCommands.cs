using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// GM commands, two ways in: "!command" lines typed in any chat channel (replies go to the typist only, as the
/// client's marquee, BROADCAST_NOTIFY 83270 with a raw string) and the client's own GM panel (GENERAL_PROC_GM 30300,
/// ). ServerOptions.GmAccounts limits who may use them; empty means everyone unless
/// ServerOptions.GmOpenWhenEmpty is false (a public, proxied or Production host, docs/guide/gm-access.md). The
/// loopback HTTP /ro3emu/gm keeps "empty = everyone" (<see cref="Allowed(ServerOptions, string?, bool)"/>).
/// </summary>
public sealed partial class GmCommands(
    IOptions<ServerOptions> options, GameData data, GameLoop loop, TimeProvider clock,
    SessionRegistry sessions, CharacterService character, SceneService scene, TaskService tasks, ILogger<GmCommands> log)
{
    public const uint BroadcastNotify = 83270;
    public const uint GeneralProcGm = 30300;

    /// <summary>The HUD asks with this prefix + a counter whether to show the GM button; "&lt;request&gt;:1" = yes.</summary>
    private const string PanelProbe = "ro3-gm-panel-capability-v1:";

    /// <summary>GmOpEnum values the emulator implements.</summary>
    private const int OpNone = 0, OpAddItem = 1, OpRemoveItem = 2, OpAddAttrPnt = 8, OpSetLevel = 24, OpAddExp = 25, OpJumpMap = 31,
        OpClearPack = 66, OpFullHpMp = 78, OpSetJob = 101;

    private delegate Task Command(GameSession session, string[] args);

    private Dictionary<string, (string Usage, Command Run)>? _commands;
    private readonly Dictionary<int, Func<GameSession, string[], Task<string?>>> _ops = [];

    /// <summary>Hook for feature modules and systems to add their !commands.</summary>
    public void Register(string name, string usage, Func<GameSession, string[], Task> run)
    {
        Commands()[name] = (usage, (s, a) => run(s, a));
    }

    /// <summary>Hook for feature modules to register a GM panel op handler (GENERAL_PROC_GM 30300).</summary>
    public void RegisterOp(int op, Func<GameSession, string[], Task<string>> run)
    {
        _ops[op] = async (s, a) => await run(s, a);
    }

    /// <summary>Hook for feature modules to register multiple GM panel ops dispatched to one handler.</summary>
    public void RegisterOps(IEnumerable<int> ops, Func<GameSession, int, string[], Task<string>> run)
    {
        foreach (int op in ops) _ops[op] = async (s, a) => await run(s, op, a);
    }

    /// <summary>Hook for feature modules whose GM panel op dispatcher may decline an op by returning null.</summary>
    public void RegisterOptionalOps(IEnumerable<int> ops, Func<GameSession, int, string[], Task<string?>> run)
    {
        foreach (int op in ops) _ops[op] = (s, a) => run(s, op, a);
    }

    public bool IsCommand(string text) => text.StartsWith('!') && text.Length > 1;

    /// <summary>Shared account allowlist for every GM wire entry point.</summary>
    public bool Allowed(GameSession session) => Allowed(options.Value, session.Account, loopbackTool: false);

    /// <summary>
    /// A listed account always may. An empty list means everyone for the loopback HTTP tool (unchanged), and in game
    /// whatever <see cref="ServerOptions.GmOpenWhenEmpty"/> says (unset = everyone, as for tests and local play).
    /// </summary>
    internal static bool Allowed(ServerOptions options, string? account, bool loopbackTool)
    {
        var allowed = options.GmAccounts;
        if (allowed.Length == 0) return loopbackTool || options.GmOpenWhenEmpty != false;
        return account != null && allowed.Contains(account, StringComparer.Ordinal);
    }

    internal static bool IsEventOwnerCommand(string line)
    {
        if (!line.StartsWith('!')) return false;
        var parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && (parts[0].Equals("siege", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("twpoint", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("mvp", StringComparison.OrdinalIgnoreCase) &&
            (parts.Length < 2 || !parts[1].Equals("goto", StringComparison.OrdinalIgnoreCase)));
    }

    /// <param name="loopbackTool">The loopback HTTP /ro3emu/gm: an empty GmAccounts still means everyone.</param>
    public Task RunAsync(GameSession session, string line, bool loopbackTool = false)
    {
        // A GM line typed in chat arrives inside the chat message's role scope (C/D 16). GM admits its own scope (Realm
        // or an economic request), so it runs as its own operation right after instead of expanding the chat's.
        if (session.Owners is { Current: { RuntimeOnly: false } })
        {
            loop.TrackRuntimeDetached("GM", () => RunAsync(session, line, loopbackTool));
            return Task.CompletedTask;
        }
        return RunAdmittedAsync(session, line, loopbackTool, panelAdmin: false);
    }

    /// <summary>
    /// The web panel's GM console (docs/guide/panel.md): an admin the panel signed in (an account of GmAccounts) runs
    /// <paramref name="line"/> for <paramref name="session"/>, which may be any player's, so the session's own account
    /// is not checked. Call it on the loop.
    /// </summary>
    internal Task RunForPanelAdminAsync(GameSession session, string line) =>
        RunAdmittedAsync(session, line, loopbackTool: false, panelAdmin: true);

    private Task RunAdmittedAsync(GameSession session, string line, bool loopbackTool, bool panelAdmin)
        => session.Owners is { } owners
            ? IsEventOwnerCommand(line)
                ? owners.RunEconomicRequestAsync(session, () => RunCoreAsync(session, line, loopbackTool, panelAdmin))
                : owners.RunSessionAsync("GM", [Ownership.ReservationKey.Realm], session, () => RunCoreAsync(session, line, loopbackTool, panelAdmin))
            : RunCoreAsync(session, line, loopbackTool, panelAdmin);

    private async Task RunCoreAsync(GameSession session, string line, bool loopbackTool, bool panelAdmin)
    {
        if (!panelAdmin && !Allowed(options.Value, session.Account, loopbackTool))
        {
            await ReplyAsync(session, "GM commands are not enabled for this account.");
            return;
        }
        var parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        log.LogInformation("GM {Name}: {Line}", session.Role?.Name, line);
        if (parts.Length == 0 || !Commands().TryGetValue(parts[0].ToLowerInvariant(), out var command))
        {
            await ReplyAsync(session, $"Unknown command. {string.Join(" ", Commands().Keys.Select(k => "!" + k))}");
            return;
        }
        try
        {
            await command.Run(session, parts[1..]);
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            await ReplyAsync(session, $"Usage: !{parts[0]} {command.Usage}");
        }
    }

    /// <summary>30300 from the client's GM panel: capability probe, preset buttons (GMCombinationConfig) or one op.</summary>
    public async Task PanelAsync(GameSession session, GeneralGMProcReq req)
    {
        string param = req.ParamString ?? "";
        if (param.StartsWith(PanelProbe, StringComparison.Ordinal))
        {
            await session.SendGameAsync(GeneralProcGm, new GeneralGMProcRsp { Result = 0, ErrInfo = param + (Allowed(session) ? ":1" : ":0") });
            return;
        }
        string reply;
        if (!Allowed(session))
        {
            reply = "GM commands are not enabled for this account.";
        }
        else
        {
            log.LogInformation("GM panel {Name}: op {Op} '{Param}' list {List}", session.Role?.Name, (int)req.GmType, param, req.GmListId);
            try
            {
                reply = (int)req.GmType == OpNone && req.GmListId > 0
                    ? await CombinationAsync(session, req.GmListId)
                    : await OpAsync(session, (int)req.GmType, param.Split(',', StringSplitOptions.TrimEntries));
            }
            catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                reply = $"Bad parameters for op {OpLabel((int)req.GmType)}: {e.Message}";
            }
        }
        await session.SendGameAsync(GeneralProcGm, new GeneralGMProcRsp { Result = 0, ErrInfo = reply });
    }

    /// <summary>A preset button: GMCombinationConfig._kCombinationScript = [[opName, p1, ...], ...].</summary>
    private async Task<string> CombinationAsync(GameSession session, int listId)
    {
        if (data.Row("GMCombinationConfig", listId) is not { } row || !row.TryGetProperty("_kCombinationScript", out var script)
            || script.ValueKind != JsonValueKind.Array)
            return $"No preset {listId}.";
        var results = new List<string>();
        var skipped = new List<string>();
        int steps = 0;
        foreach (var step in script.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Array || step.GetArrayLength() == 0) continue;
            steps++;
            string name = step[0].ValueKind == JsonValueKind.String ? step[0].GetString()! : step[0].ToString();
            var args = step.EnumerateArray().Skip(1).Select(a => a.ValueKind == JsonValueKind.String ? a.GetString()! : a.ToString()).ToArray();
            if (OpNumber(name) is not { } op)
            {
                skipped.Add($"{name} (unknown op)");
                continue;
            }
            try
            {
                if (await HandledOpAsync(session, op, args) is { } reply) results.Add(reply);
                else skipped.Add(OpLabel(op));
            }
            catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                results.Add($"{OpLabel(op)} {string.Join(',', args)}: {e.Message}");
            }
        }
        if (skipped.Count > 0)
            results.Add($"Preset {listId}: {steps - skipped.Count} of {steps} steps run. Not implemented: "
                + string.Join(", ", skipped.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key)) + ".");
        return string.Join("\n", results);
    }

    /// <summary>
    /// GmOpEnum value by its proto name, case-insensitively ("GmOpAddItem", "GmSetLevel"...), or by its number: one
    /// preset step names its op "251".
    /// </summary>
    internal static int? OpNumber(string name)
    {
        var values = GeneralGMProcReq.Descriptor.FindFieldByName("gm_type").EnumType.Values;
        return int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            ? values.Any(v => v.Number == number) ? number : null
            : values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))?.Number;
    }

    /// <summary>"4 GmOpStrengthen": the op number with its GmOpEnum proto name, for replies.</summary>
    internal static string OpLabel(int op) =>
        GeneralGMProcReq.Descriptor.FindFieldByName("gm_type").EnumType.FindValueByNumber(op) is { } value ? $"{op} {value.Name}" : $"{op}";

    /// <summary>One GM operation with the panel's comma-separated parameters.</summary>
    private async Task<string> OpAsync(GameSession s, int op, string[] args) =>
        await HandledOpAsync(s, op, args) ?? $"GM op {OpLabel(op)} is not implemented by the emulator.";

    /// <summary>The reply of an op the emulator implements, or null when it does not.</summary>
    private async Task<string?> HandledOpAsync(GameSession s, int op, string[] args)
    {
        if (_ops.TryGetValue(op, out var registered)) return await registered(s, args);
        switch (op)
        {
            case OpAddItem:
                int count = args.Length > 1 ? I(args[1]) : 1;
                return await character.AddItemAsync(s, I(args[0]), count <= 0 ? 1 : count);
            case OpRemoveItem:
                return await character.RemoveItemAsync(s, I(args[0]), args.Length > 1 && I(args[1]) > 0 ? I(args[1]) : int.MaxValue);
            case OpAddAttrPnt:
                return await character.AddStatPointsAsync(s, I(args[0]));
            case OpSetLevel:
                return await character.SetLevelAsync(s, I(args[0]), args.Length > 1 && I(args[1]) > 0 ? I(args[1]) : null);
            case OpAddExp:
                return await character.AddExpAsync(s, L(args[0]), args.Length > 1 ? L(args[1]) : 0);
            case OpJumpMap:
                long scene = L(args[0]);
                bool hasPoint = args.Length >= 3 && (F(args[1]) != 0 || F(args[2]) != 0);
                return await TryWarpAsync(s, scene, hasPoint ? F(args[1]) : null, hasPoint ? F(args[2]) : null)
                    ?? $"Warp to {scene} {data.MapName(scene)}";
            case OpClearPack:
                return await character.ClearBagAsync(s);
            case OpFullHpMp:
                await character.SetHpSpAsync(s, long.MaxValue, long.MaxValue);
                return "HP and SP full.";
            case OpSetJob:
                return await character.SetJobAsync(s, I(args[0]));
            default:
                return await CharacterOpAsync(s, op, args) ?? await PresetOpAsync(s, op, args);
        }
    }

    public static Task ReplyAsync(GameSession session, string text)
    {
        session.GmReplies?.Add(text);
        return session.SendGameAsync(BroadcastNotify, new BroadCastNotify { Content = text });
    }

    private Dictionary<string, (string Usage, Command Run)> Commands() => _commands ??= new()
    {
        ["help"] = ("", (s, _) => ReplyAsync(s, "Commands: " + string.Join("  ", Commands().Select(c => $"!{c.Key} {c.Value.Usage}".Trim())))),
        ["where"] = ("", Where),
        ["online"] = ("", Online),
        ["warp"] = ("<sceneEntry> [x z]", Warp),
        ["jump"] = ("<x> <z>", Jump),
        ["finditem"] = ("<name>", FindItem),
        ["item"] = ("<id|name> [count]", Item),
        ["delitem"] = ("<id> [count]", (s, a) => Op(s, OpRemoveItem, a)),
        ["zeny"] = ("<amount>", Zeny),
        ["lvl"] = ("<base> [job]", (s, a) => Op(s, OpSetLevel, a)),
        ["exp"] = ("<base> [job]", (s, a) => Op(s, OpAddExp, a)),
        ["points"] = ("<n>", (s, a) => Op(s, OpAddAttrPnt, a)),
        // Skill points come from the job level (JobLvConfig._iSkillPoint, SkillService.SkillPoints); this is the GM panel's 110.
        ["skillsmax"] = ("[0 clears]", (s, a) => Op(s, OpAllSkillsMax, a.Length == 0 ? ["1"] : a)),
        ["job"] = ("<jobId>", (s, a) => Op(s, OpSetJob, a)),
        ["clearbag"] = ("", (s, a) => Op(s, OpClearPack, a)),
        ["client"] = ("<find name>", ClientDiagnostics),
        ["day"] = ("", (s, a) => NightLook(s, "day")),
        ["dusk"] = ("", (s, a) => NightLook(s, "dusk")),
        ["night"] = ("", (s, a) => NightLook(s, "night")),
        ["dawn"] = ("", (s, a) => NightLook(s, "dawn")),
        ["time"] = ("<auto|day|dusk|night|dawn|hour 0-24>", (s, a) => NightLook(s, a.Length == 0 ? "auto" : a[0])),
        ["speed"] = ("<speed, 0 = normal>", async (s, a) => await ReplyAsync(s, await character.SetSpeedAsync(s, L(a[0])))),
        ["quest"] = ("[accept|finish|done <taskId> | reset]", async (s, a) => await ReplyAsync(s, a.Length == 0
            ? "Quests: " + tasks.Describe(s)
            : await tasks.GmAsync(s, a[0].ToLowerInvariant(), a.Length > 1 ? L(a[1]) : 0))),
        ["hp"] = ("<hp> [sp]", async (s, a) => { await character.SetHpSpAsync(s, L(a[0]), a.Length > 1 ? L(a[1]) : null); await ReplyAsync(s, $"HP {a[0]}"); }
        ),
    };

    /// <summary>Asks the client hook (Ro3Emu.ClientHooks Diagnostics, msg 77012) to inspect itself into Player.log.</summary>
    private async Task ClientDiagnostics(GameSession s, string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("command");
        await s.SendGameRawAsync(ClientDiagnosticsMsg, System.Text.Encoding.UTF8.GetBytes(string.Join(' ', args)));
        await ReplyAsync(s, "Sent to the client; see Player.log [Ro3Emu.Diag].");
    }

    internal const uint ClientDiagnosticsMsg = 77012;

    private static readonly string[] NightLooks = ["auto", "day", "dusk", "night", "dawn"];

    /// <summary>The night look on this GM's own screen only (client hook NightCycle, over the same 77012 message as
    /// <c>!client</c>): day, dusk, night, dawn, an hour 0-24, or auto (the PC clock again). It works whether or not
    /// <see cref="ServerOptions.NightCycle"/> is on, and changes nothing on the server or for other players.</summary>
    private static async Task NightLook(GameSession s, string setting)
    {
        string look = setting.ToLowerInvariant();
        bool hour = float.TryParse(look, NumberStyles.Float, CultureInfo.InvariantCulture, out float h) && h is >= 0 and <= 24;
        if (!hour && !NightLooks.Contains(look)) throw new ArgumentException("<auto|day|dusk|night|dawn|hour 0-24>");
        await s.SendGameRawAsync(ClientDiagnosticsMsg, System.Text.Encoding.UTF8.GetBytes("time " + look));
        await ReplyAsync(s, look == "auto"
            ? "Night look: your PC clock again (towns and fields only, your screen only)."
            : $"Night look: {look} on your screen only (towns and fields only). !time auto goes back to the clock.");
    }

    private async Task Op(GameSession s, int op, string[] args)
    {
        if (op != OpClearPack && args.Length == 0) throw new ArgumentException("parameters");
        await ReplyAsync(s, await OpAsync(s, op, args));
    }

    /// <summary>"!item 2010121", "!item 1000000 20", "!item beginner potion 20".</summary>
    private Task Item(GameSession s, string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("item");
        if (int.TryParse(args[0], out _)) return Op(s, OpAddItem, args);
        bool counted = args.Length > 1 && int.TryParse(args[^1], out _);
        string name = string.Join(' ', counted ? args[..^1] : args);
        var hit = data.FindItems(name).FirstOrDefault();
        if (hit.Id == 0) return ReplyAsync(s, $"No item called '{name}'.");
        return Op(s, OpAddItem, [hit.Id.ToString(CultureInfo.InvariantCulture), counted ? args[^1] : "1"]);
    }

    private Task Zeny(GameSession s, string[] args)
    {
        long amount = L(args[0]);
        return amount >= 0
            ? Op(s, OpAddItem, ["1", amount.ToString(CultureInfo.InvariantCulture)])
            : Op(s, OpRemoveItem, ["1", (-amount).ToString(CultureInfo.InvariantCulture)]);
    }

    private Task Where(GameSession s, string[] _) =>
        ReplyAsync(s, string.Create(CultureInfo.InvariantCulture,
            $"Scene {s.SceneEntry} {data.MapName(s.SceneEntry)} at {s.Position.X:F1} {s.Position.Y:F1} {s.Position.Z:F1}"));

    private Task Online(GameSession s, string[] _) =>
        ReplyAsync(s, "Online: " + string.Join(", ", sessions.All().Select(o => $"{o.Role?.Name ?? o.Account} ({o.SceneEntry})")));

    private Task FindItem(GameSession s, string[] args)
    {
        var hits = data.FindItems(string.Join(' ', args)).Take(8).ToList();
        return ReplyAsync(s, hits.Count == 0 ? "No such item." : string.Join("  ", hits.Select(h => $"{h.Id} {h.Name}")));
    }

    /// <summary>Same-map teleport to x,z (y from the nav grid).</summary>
    private Task Jump(GameSession s, string[] args) =>
        scene.TeleportInMapAsync(s, new MapPoint(F(args[0]), s.Position.Y, F(args[1]), s.Face));

    private async Task Warp(GameSession s, string[] args)
    {
        if (await TryWarpAsync(s, L(args[0]), args.Length >= 3 ? F(args[1]) : null, args.Length >= 3 ? F(args[2]) : null) is { } refused)
            await ReplyAsync(s, refused);
    }

    /// <summary>Starts every reply of a warp that did not go (the web panel's teleport looks for it).</summary>
    public const string WarpRefused = "Cannot warp";

    /// <summary>Map change to x,z, or to the map's born point; throws with the reason when it does not go.</summary>
    internal async Task WarpAsync(GameSession s, long target, float? x, float? z)
    {
        if (await TryWarpAsync(s, target, x, z) is { } refused) throw new ArgumentException(refused);
    }

    /// <summary>Map change to x,z, or to the map's born point: null when it went, else why not.</summary>
    private async Task<string?> TryWarpAsync(GameSession s, long target, float? x, float? z)
    {
        string label = data.MapName(target) is { Length: > 0 } name ? $"{target} {name}" : $"{target}";
        if (scene.Unplaceable(target) is { } why) return $"{WarpRefused} to {label}: {why}.";
        var at = x is { } px && z is { } pz ? new MapPoint(px, s.Position.Y, pz, s.Face) : (MapPoint?)null;
        return await scene.EnterSceneAsync(s, target, at) ? null : $"{WarpRefused} to {label} now: the character is changing maps.";
    }

    internal List<GameSession> OnlineRoles() => [.. sessions.All().Where(o => o.Role != null && o.Progress != null)];

    internal string FormatGoods(IEnumerable<(int Entry, int Count)> goods) =>
        string.Join(", ", goods.Select(g => $"{g.Count} {data.ItemName(g.Entry)} ({g.Entry})"));

    internal DateTimeOffset? ParseMailExpiry(string text)
    {
        if (text.Length == 0 || text == "0") return null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int days)) return clock.GetUtcNow().AddDays(days);
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
    }

    internal static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    internal static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);
    internal static long L(string s) => long.Parse(s, CultureInfo.InvariantCulture);
}
