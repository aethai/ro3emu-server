namespace Ro3Emu.Server.Game;

/// <summary>
/// Typed per-session state key owned by a service. Indexed by an integer slot for O(1) array lookup on
/// <see cref="GameSession"/>, with optional automatic clearing in <see cref="GameSession.ClearRoleLoadState"/>.
/// </summary>
public sealed class SessionSlot<T>(bool clearOnRoleReset = true) where T : class
{
    internal int Index { get; } = SessionSlotRegistry.Register(clearOnRoleReset);
    internal bool ClearOnRoleReset { get; } = clearOnRoleReset;
}

internal static class SessionSlotRegistry
{
    private static readonly object Sync = new();
    private static bool[] _clearOnRoleReset = [];
    private static int _count;

    internal static int Register(bool clearOnRoleReset)
    {
        lock (Sync)
        {
            int index = _count++;
            if (index >= _clearOnRoleReset.Length)
                Array.Resize(ref _clearOnRoleReset, Math.Max(index + 1, _clearOnRoleReset.Length == 0 ? 16 : _clearOnRoleReset.Length * 2));
            _clearOnRoleReset[index] = clearOnRoleReset;
            return index;
        }
    }

    internal static void ClearRoleSlots(object?[] slots)
    {
        if (slots.Length == 0) return;
        bool[] flags;
        int count;
        lock (Sync)
        {
            flags = _clearOnRoleReset;
            count = Math.Min(_count, slots.Length);
        }
        for (int i = 0; i < count; i++)
            if (flags[i]) slots[i] = null;
    }
}
