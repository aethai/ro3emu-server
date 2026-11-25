using Ro3Emu.Server.Game;

namespace Ro3Emu.Server;

/// <summary>Combat, buffs, monsters, public lines, DI-registered combat rules, and combat GM ops.</summary>
internal static class CombatComposition
{
    public static void AddCombat(IServiceCollection services)
    {
        services.AddSingleton<MonsterService>();
        services.AddSingleton<PublicLineManager>();
        services.AddSingleton<BuffService>();
        services.AddSingleton<CombatService>();
        services.AddSingleton<CombatGmCommands>();
        services.AddSingleton<IRoleLifecycle>(sp => sp.GetRequiredService<CombatService>());
        services.AddSingleton<IRoleLifecycle>(sp => sp.GetRequiredService<BuffService>());
    }

    public static void WireCombat(WebApplication app)
    {
        app.Services.GetRequiredService<CombatService>().BindRules(app.Services.GetServices<ICombatRule>());
    }

    public static void RegisterCombatGm(WebApplication app)
    {
        app.Services.GetRequiredService<CombatGmCommands>().Register(app.Services.GetRequiredService<GmCommands>());
    }
}
