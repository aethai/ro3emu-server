using System.Text.Json;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game.Persistence;

/// <summary>
/// The running Bonus Time clock is a lazy part (see <see cref="LazyRolePart"/>): while it runs, the saved state changes every
/// second, and a durable commit per player per second stalled the loop. It is written at most 30 s after the first unsaved
/// change (inferred; server tuning), so a crash can hand back up to that much consumed time. Every reconcile of a clock
/// that keeps running is lazy, a field kill's included; a field kill's record goes with its reward, which also writes the
/// clock, and while that reward is pending no lazy write is submitted, so none can change the bytes it guards. A pause and
/// its resumption are lazy as well. The other AFK saves (toggles, snapshots, the daily reset, the end of Bonus Time,
/// leaving the world) stay synchronous and act as barriers.
/// </summary>
public sealed class WildAfkPersistence : LazyRolePart
{
    /// <summary>(inferred) A staged kill whose reward has neither committed nor been reset by now is abandoned: lazy
    /// writes resume.</summary>
    internal const long KillStageHoldMs = 10_000;

    private readonly RoleStore _roles;
    private readonly GameLoop _loop;

    public WildAfkPersistence(RoleStore roles, GameLoop loop, ILogger<WildAfkPersistence> log) : base(loop, roles, log)
    {
        this._roles = roles;
        this._loop = loop;
        Interval = TimeSpan.FromSeconds(30);
    }

    protected override string RevisionPart => "wild_afk";
    protected override State? Get(GameSession s) => s.WildAfkPart;
    protected override void Set(GameSession s, State state) => s.WildAfkPart = state;
    protected override bool Eligible(GameSession s) => s.Role != null && s.Activities.WildAfk != null;
    protected override bool Held(GameSession s) => s.Activities.WildAfk is { KillsStaged: > 0 } state
        && _loop.NowMs - state.KillStagedAtMs < KillStageHoldMs;

    protected override Func<long, long, long, Task<bool>> Capture(GameSession s)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(s.Activities.WildAfk!);
        return (role, epoch, revision) => _roles.SaveBlobLazyAsync(role, epoch, revision, "wild_afk", bytes);
    }
}
