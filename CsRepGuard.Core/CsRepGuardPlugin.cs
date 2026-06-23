using System;
using AdminPanel.Shared;
using CsRepGuard.Configuration;
using CsRepGuard.Database;
using CsRepGuard.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;

namespace CsRepGuard;

/// <summary>
/// CsRepGuard — staff advisory plugin that cross-references connecting players against CSRep.gg
/// and notifies online admins (Discord webhook + in-game chat) when ban flags are detected.
///
/// POLICY: NOTIFY ONLY. Per CSRep ToS §6.B, csrep signals must NOT be the sole/primary basis
/// for automated punitive actions. This plugin never bans or kicks. A human staff member reviews
/// and decides all actions via existing admin tools.
///
/// ToS compliance summary:
///   §4  — Redacted/anonymous players: no data stored or displayed; any cached row purged.
///   §6.A — Probabilistic disclaimer included in all outputs.
///   §6.B — No automated ban/kick path. Staff advisory only.
///   §6.C — No mass archival; cache is transient performance only.
///   §8   — All outputs attribute data to CSRep.gg with profile link.
///   §9   — 24h cache TTL hard-capped; hourly DELETE purge of stale rows.
///
/// Lifecycle: all cross-plugin wiring in OnAllModulesLoaded (ModSharp guarantee).
/// </summary>
public sealed class CsRepGuardPlugin : IModSharpModule
{
    public string DisplayName   => "CsRepGuard";
    public string DisplayAuthor => "yappershq";

    private readonly ILogger<CsRepGuardPlugin> _logger;
    private readonly InterfaceBridge            _bridge;
    private readonly CsRepConfig                _config;
    private readonly CsRepDatabase              _db;
    private readonly CsRepApiClient             _api;
    private readonly CsRepWebhook               _webhook;
    private          CsRepCheckModule?          _checkModule;
    private          CsRepCommandModule?        _commandModule;

    public CsRepGuardPlugin(
        ISharedSystem  sharedSystem,
        string         dllPath,
        string         sharpPath,
        Version        version,
        IConfiguration configuration,
        bool           hotReload)
    {
        var loggerFactory = sharedSystem.GetLoggerFactory();
        _logger  = loggerFactory.CreateLogger<CsRepGuardPlugin>();

        _bridge  = new InterfaceBridge(sharpPath, sharedSystem);
        _config  = CsRepConfig.Load(sharpPath, loggerFactory.CreateLogger<CsRepConfig>());
        _db      = new CsRepDatabase(loggerFactory.CreateLogger<CsRepDatabase>());
        _api     = new CsRepApiClient(_config.ApiKey, loggerFactory.CreateLogger<CsRepApiClient>());
        _webhook = new CsRepWebhook(_config.WebhookUrl, loggerFactory.CreateLogger<CsRepWebhook>());
    }

    public bool Init() => true;

    public void PostInit() { }

    public void OnAllModulesLoaded()
    {
        _bridge.ResolveModules();

        // Connect to the PlayerAnalytics DB (reusing its credentials file — no duplicate creds).
        var dbConfig = DatabaseConfig.LoadShared(
            _bridge.SharpPath, _config.AnalyticsDatabaseConfig,
            _logger);

        if (dbConfig is not null && _db.Connect(dbConfig))
        {
            _db.EnsureCacheTable();
            _db.StartPurgeTimer(); // hourly DELETE of rows > 24h (ToS §9)
        }
        else
        {
            _logger.LogWarning("[CsRepGuard] DB not connected — cache disabled; live API calls on every connect");
        }

        // Load shared bypass list (JSON offline fallback; same file as AltGuard/AntiVpnGuard).
        var bypass = SharedBypass.Load(_bridge.SharpPath, _config.SharedBypassConfig, _logger);

        // Wire up the connect-time check module.
        _checkModule = new CsRepCheckModule(
            _bridge, _config, _db, _api, _webhook, bypass,
            _bridge.LoggerFactory.CreateLogger<CsRepCheckModule>());
        _checkModule.Start();

        // Wire up the admin !csrep / !rep command module.
        _commandModule = new CsRepCommandModule(
            _bridge, _config, _db, _api,
            _bridge.LoggerFactory.CreateLogger<CsRepCommandModule>());
        _commandModule.Start();

        // Optional: inject a "CS Rep lookup" action into the in-game AdminPanel per-player menu.
        // Null-guarded — CsRepGuard runs standalone when AdminPanel is not installed.
        RegisterAdminPanelAction();

        _logger.LogInformation(
            "[CsRepGuard] Loaded — ApiKey={HasKey}, DB={Db}, Webhook={Wh}, AdminManager={Mgr}, AdminPanel={Panel}",
            _api.HasKey, _db.IsConnected, _webhook.Enabled,
            _bridge.AdminManager is not null, _bridge.AdminPanel is not null);
    }

    public void Shutdown()
    {
        // Remove our action from the AdminPanel menu if it was registered.
        if (_bridge.AdminPanel is { } panel)
            panel.Unregister(AdminPanelActionId);

        _checkModule?.Stop();
        _commandModule?.Stop();
        _db.Dispose();
    }

    // -------------------------------------------------------------------------
    // AdminPanel integration
    // -------------------------------------------------------------------------

    private const string AdminPanelActionId = "csrep.lookup";

    /// <summary>
    /// Registers a per-player "CS Rep lookup" action with AdminPanel (if installed). Selecting it
    /// against a target runs the existing cache-first CSRep lookup and prints the result to the
    /// admin — same behavior and permission (@csrepguard/lookup) as the !csrep command.
    /// </summary>
    private void RegisterAdminPanelAction()
    {
        if (_bridge.AdminPanel is not { } panel || _commandModule is not { } cmd)
            return;

        panel.RegisterPlayerAction(new AdminPanelPlayerAction
        {
            Id         = AdminPanelActionId,
            Label      = "CS Rep lookup",
            Permission = CsRepCommandModule.LookupPermission, // "@csrepguard/lookup"
            SortOrder  = 100,
            // Fires on the game thread with validated admin/target slots (AdminPanel contract).
            OnSelected = (adminSlot, targetSlot) => cmd.LookupFromPanel(adminSlot, targetSlot),
        });

        _logger.LogInformation("[CsRepGuard] Registered AdminPanel action '{Id}'", AdminPanelActionId);
    }
}
