namespace Ro3Emu.Server.Game;

/// <summary>
/// Explicit startup initialization phase executed after DI construction and before the game loop
/// and network gates begin accepting traffic. Keeps SQLite reads and migrations out of constructors.
/// </summary>
public interface IStartupInitializer
{
    void Initialize();
}
