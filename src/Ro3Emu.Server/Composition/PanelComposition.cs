using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Http;

namespace Ro3Emu.Server;

/// <summary>Web admin panel, staff roles, maintenance mode, and auto-update services.</summary>
internal static class PanelComposition
{
    public static void AddPanel(IServiceCollection services, ServerOptions options)
    {
        services.AddSingleton<CredentialLimiter>();
        services.AddSingleton(options.Panel);
        services.AddSingleton<PanelBackend>();
        services.AddSingleton<Ro3Emu.Panel.IPanelBackend>(sp => sp.GetRequiredService<PanelBackend>());
        services.AddSingleton<Ro3Emu.Panel.IPanelAdminTools, PanelAdminTools>();
        services.AddSingleton<StaffRoles>();
        services.AddSingleton<MaintenanceMode>();
        services.AddSingleton<PanelOperations>();
        services.AddSingleton<Ro3Emu.Panel.IPanelOperations>(sp => sp.GetRequiredService<PanelOperations>());
        services.AddSingleton<Updates.UpdateManager>();
        services.AddSingleton<Ro3Emu.Panel.IPanelUpdates>(sp => sp.GetRequiredService<Updates.UpdateManager>());
        services.AddHostedService(sp => sp.GetRequiredService<Updates.UpdateManager>());
        services.AddSingleton<Ro3Emu.Panel.PanelSite>();
    }

    public static void MapPanel(WebApplication app, ServerOptions options)
    {
        // First: the panel's port serves only the panel, without the server's request log and endpoints.
        if (options.Panel.Enabled) Ro3Emu.Panel.PanelSite.Map(app, options.Panel.Port);
    }

    public static void WirePanel(WebApplication app, ServerOptions options)
    {
        // Staff roles and maintenance from the database before the gates take a login (the game gate asks both).
        app.Services.GetRequiredService<StaffRoles>().Load();
        app.Services.GetRequiredService<MaintenanceMode>().Load();
        if (options.Panel.Enabled)
            app.Services.GetRequiredService<Ro3Emu.Panel.PanelSite>(); // built now, after every hook above
    }

    /// <summary>The panel's listen URL: its own bind address, else the server's; a port the server already uses is refused.</summary>
    public static string PanelUrl(ServerOptions options)
    {
        var panel = options.Panel;
        if (panel.Port is <= 0 or > 65535 || panel.Port == options.HttpPort || panel.Port == options.MicroGatePort
            || panel.Port == options.GameGatePort || panel.Port == options.Voice.Port)
            throw new InvalidOperationException($"Ro3Emu:Panel:Port {panel.Port} must be a free port of its own (docs/guide/panel.md).");
        var address = PanelAddress(options);
        string host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return $"http://{host}:{panel.Port}";
    }

    private static System.Net.IPAddress PanelAddress(ServerOptions options) => options.PanelListenAddress();
}
