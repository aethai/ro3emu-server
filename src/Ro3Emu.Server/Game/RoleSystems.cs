using System.Globalization;
using Microsoft.Extensions.Logging;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Orchestrates every DI-registered <see cref="IRoleLifecycle"/> participant in explicit
/// <see cref="RoleLifecycleOrder"/> sequence when a role loads, logs in, moves, or leaves.
/// </summary>
public sealed partial class RoleSystems
{
    private readonly RoleLoadStep[] _load;
    private readonly RoleLoginStep[] _login;
    private readonly RoleLoginKeysStep[] _loginKeys;
    private readonly RoleLeaveKeysStep[] _leaveKeys;
    private readonly RoleMoveStep[] _move;
    private readonly RoleLeaveStep[] _leave;
    private readonly ILogger<RoleSystems> _log;

    public RoleSystems(IEnumerable<IRoleLifecycle> lifecycles, ILogger<RoleSystems> log)
    {
        ArgumentNullException.ThrowIfNull(lifecycles);
        _log = log;

        var builder = new RoleLifecycleBuilder();
        builder.OnLeaveContext(RoleLifecycleOrder.LeavePosition, "position", "RoleSession.SavePosition",
            ctx => { ctx.SavePosition(); return Task.CompletedTask; },
            condition: ctx => ctx.Session.RolePositionReady);

        foreach (var lifecycle in lifecycles)
            lifecycle.RegisterLifecycle(builder);

        _load = SortUnique(builder.Load, static s => s.Order, static s => s.Name, "Load");
        _login = SortUnique(builder.Login, static s => s.Order, static s => s.Name, "Login");
        _loginKeys = SortUnique(builder.LoginKeys, static s => s.Order, static s => s.Name, "LoginKeys");
        _leaveKeys = SortUnique(builder.LeaveKeys, static s => s.Order, static s => s.Name, "LeaveKeys");
        _move = SortUnique(builder.Move, static s => s.Order, static s => s.Name, "Move");
        _leave = SortUnique(builder.Leave, static s => s.Order, static s => s.Label, "Leave");
    }

    internal async Task LoadAsync(GameSession session)
    {
        foreach (var step in _load)
            await step.Work(session);
    }

    internal async Task LoginAsync(GameSession session, Action checkpoint)
    {
        foreach (var step in _login)
        {
            await step.Work(session);
            if (step.CheckpointAfter) checkpoint();
        }
    }

    /// <summary>The keys the login steps write besides the role, its account and lifecycle.</summary>
    internal async Task<Ownership.ReservationKey[]> LoginKeysAsync(long roleId)
    {
        List<Ownership.ReservationKey> keys = [];
        foreach (var provider in _loginKeys)
            keys.AddRange(await provider.Keys(roleId));
        return [.. keys];
    }

    /// <summary>The shared keys the leave steps write: the own guild (offline publication) and the timed battle.</summary>
    internal Ownership.ReservationKey[] LeaveKeys(GameSession session)
    {
        List<Ownership.ReservationKey> keys = [];
        foreach (var provider in _leaveKeys)
            keys.AddRange(provider.Keys(session));
        return [.. keys];
    }

    internal async Task MovedAsync(GameSession session, WalkReq walk)
    {
        foreach (var step in _move)
            await step.Work(session, walk);
    }

    /// <summary>Each essential step runs through <paramref name="step"/>, which collects its failure.</summary>
    internal async Task LeaveAsync(GameSession session, Func<string, Func<Task>, Task> step, Action savePosition)
    {
        var context = new RoleLeaveContext(session, savePosition);
        foreach (var entry in _leave)
        {
            if (entry.Condition != null && !entry.Condition(context)) continue;
            switch (entry.Kind)
            {
                case LeaveStepKind.Essential:
                    await step(entry.StepName, () => entry.Work(context));
                    break;
                case LeaveStepKind.Optional:
                    try { await entry.Work(context); }
                    catch (Exception e) { LogOptionalLeaveFailed(_log, entry.StepName, session.Role?.Id, e); }
                    break;
                case LeaveStepKind.StateReset:
                    await entry.Work(context);
                    break;
            }
        }
    }

    /// <summary>Deterministic description of all lifecycle phases in execution order for snapshot verification.</summary>
    internal List<string> DescribeOrder()
    {
        List<string> lines = [];
        lines.Add(string.Format(CultureInfo.InvariantCulture, "Load ({0})", _load.Length));
        foreach (var s in _load)
            lines.Add(string.Format(CultureInfo.InvariantCulture, "  {0}: {1}", s.Order, s.Name));

        lines.Add(string.Format(CultureInfo.InvariantCulture, "Login ({0})", _login.Length));
        foreach (var s in _login)
            lines.Add(string.Format(CultureInfo.InvariantCulture, "  {0}: {1}{2}", s.Order, s.Name, s.CheckpointAfter ? " [checkpoint]" : ""));

        lines.Add(string.Format(CultureInfo.InvariantCulture, "LoginKeys ({0})", _loginKeys.Length));
        foreach (var s in _loginKeys)
            lines.Add(string.Format(CultureInfo.InvariantCulture, "  {0}: {1}", s.Order, s.Name));

        lines.Add(string.Format(CultureInfo.InvariantCulture, "LeaveKeys ({0})", _leaveKeys.Length));
        foreach (var s in _leaveKeys)
            lines.Add(string.Format(CultureInfo.InvariantCulture, "  {0}: {1}", s.Order, s.Name));

        lines.Add(string.Format(CultureInfo.InvariantCulture, "Move ({0})", _move.Length));
        foreach (var s in _move)
            lines.Add(string.Format(CultureInfo.InvariantCulture, "  {0}: {1}", s.Order, s.Name));

        lines.Add(string.Format(CultureInfo.InvariantCulture, "Leave ({0})", _leave.Length));
        foreach (var s in _leave)
            lines.Add(string.Format(CultureInfo.InvariantCulture, "  {0}: [{1}] {2} ({3})", s.Order, s.StepName, s.Label, s.Kind));

        return lines;
    }

    private static T[] SortUnique<T>(List<T> items, Func<T, int> orderOf, Func<T, string> nameOf, string phase)
    {
        var sorted = items.OrderBy(orderOf).ToArray();
        for (int i = 1; i < sorted.Length; i++)
        {
            if (orderOf(sorted[i - 1]) == orderOf(sorted[i]))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "Duplicate RoleLifecycle {0} order {1}: {2} and {3}.",
                    phase, orderOf(sorted[i]), nameOf(sorted[i - 1]), nameOf(sorted[i])));
            }
        }
        return sorted;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optional leave step {Step} failed for role {Role}")]
    private static partial void LogOptionalLeaveFailed(ILogger logger, string step, long? role, Exception exception);
}
