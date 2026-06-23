using System;
using System.Collections.Immutable;
using System.Text;
using System.Threading.Tasks;
using CsRepGuard.Configuration;
using CsRepGuard.Database;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace CsRepGuard.Modules;

/// <summary>
/// In-game admin commands: `csrep` and alias `rep`.
/// Registered with MountAdminManifest so the "csrepguard:lookup" perm is visible to the root flag.
/// Falls back to permission-less InstallCommandCallback if AdminManager is unavailable.
///
/// ToS §8: output always attributes data to CSRep.gg with profile link.
/// ToS §6.A: output always includes probabilistic disclaimer.
/// ToS §4: redacted players are shown only "profile is private (redacted)" — no data.
/// </summary>
internal sealed class CsRepCommandModule
{
    private const string ModuleId    = "CsRepGuard";
    private const string PermLookup  = "csrepguard:lookup";

    private readonly InterfaceBridge               _bridge;
    private readonly CsRepConfig                   _config;
    private readonly CsRepDatabase                 _db;
    private readonly CsRepApiClient                _api;
    private readonly ILogger<CsRepCommandModule>   _logger;

    private IClientManager.DelegateClientCommand? _fallbackCsrep;
    private IClientManager.DelegateClientCommand? _fallbackRep;
    private bool                                  _usedRegistry;

    public CsRepCommandModule(
        InterfaceBridge              bridge,
        CsRepConfig                  config,
        CsRepDatabase                db,
        CsRepApiClient               api,
        ILogger<CsRepCommandModule>  logger)
    {
        _bridge = bridge;
        _config = config;
        _db     = db;
        _api    = api;
        _logger = logger;
    }

    public void Start()
    {
        if (_bridge.AdminManager is { } am)
        {
            // Register custom permission so "*" root flag expands to cover it.
            am.MountAdminManifest(ModuleId, () => new AdminTableManifest(
                new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>
                    { ["csrepguard"] = [PermLookup] },
                [],
                []));

            am.GetCommandRegistry(ModuleId)
              .RegisterAdminCommand("csrep", OnCsRepCommand, ImmutableArray.Create(PermLookup));
            am.GetCommandRegistry(ModuleId)
              .RegisterAdminCommand("rep",   OnCsRepCommand, ImmutableArray.Create(PermLookup));

            _usedRegistry = true;
            _logger.LogInformation("[CsRepGuard] !csrep / !rep registered (perm {Perm})", PermLookup);
        }
        else
        {
            // AdminManager unavailable — install raw callbacks but guard manually via GetAdmin.
            // If AdminManager isn't present either, deny access entirely so non-admins can't use the command.
            _fallbackCsrep = (client, cmd) =>
            {
                if (client is null || client.IsFakeClient) return ECommandAction.Handled;
                if (_bridge.AdminManager?.GetAdmin(client.SteamId) is null)
                {
                    client.Print(HudPrintChannel.Chat, " [CsRepGuard] You do not have permission to use this command.");
                    return ECommandAction.Handled;
                }
                OnCsRepCommand(client, cmd);
                return ECommandAction.Handled;
            };
            _fallbackRep = (client, cmd) =>
            {
                if (client is null || client.IsFakeClient) return ECommandAction.Handled;
                if (_bridge.AdminManager?.GetAdmin(client.SteamId) is null)
                {
                    client.Print(HudPrintChannel.Chat, " [CsRepGuard] You do not have permission to use this command.");
                    return ECommandAction.Handled;
                }
                OnCsRepCommand(client, cmd);
                return ECommandAction.Handled;
            };
            _bridge.ClientManager.InstallCommandCallback("csrep", _fallbackCsrep);
            _bridge.ClientManager.InstallCommandCallback("rep",   _fallbackRep);
            _logger.LogWarning("[CsRepGuard] AdminManager unavailable — !csrep registered with GetAdmin() guard only (no perm manifest)");
        }
    }

    public void Stop()
    {
        if (!_usedRegistry)
        {
            if (_fallbackCsrep is not null) _bridge.ClientManager.RemoveCommandCallback("csrep", _fallbackCsrep);
            if (_fallbackRep   is not null) _bridge.ClientManager.RemoveCommandCallback("rep",   _fallbackRep);
        }
    }

    /// <summary>
    /// Permission flag required to perform a CSRep lookup. Exposed so the AdminPanel
    /// integration can gate its menu action against the same perm as the !csrep command.
    /// </summary>
    public static string LookupPermission => PermLookup;

    /// <summary>
    /// Cross-plugin entry point used by the AdminPanel menu integration. Resolves the
    /// acting admin and target by slot on the game thread, then runs the SAME cache-first
    /// async lookup as the !csrep command, printing the result back to the admin.
    /// <para>Must be called on the game thread (AdminPanel guarantees this in OnSelected).</para>
    /// </summary>
    /// <param name="adminSlot">Slot of the admin who selected the action (lookup output goes here).</param>
    /// <param name="targetSlot">Slot of the targeted player to look up.</param>
    public void LookupFromPanel(int adminSlot, int targetSlot)
    {
        var admin  = _bridge.ClientManager.GetGameClient((PlayerSlot) (byte) adminSlot);
        var target = _bridge.ClientManager.GetGameClient((PlayerSlot) (byte) targetSlot);

        if (admin is not { IsInGame: true } || admin.IsFakeClient)
            return;

        if (target is not { IsInGame: true } || target.IsFakeClient)
        {
            admin.Print(HudPrintChannel.Chat, " [CsRepGuard] Target is no longer available.");
            return;
        }

        var targetSteam = ((ulong) target.SteamId).ToString();
        var targetName  = target.Name ?? targetSteam;

        admin.Print(HudPrintChannel.Chat, $" [CsRepGuard] Looking up {targetName} ({targetSteam})...");

        var invokerSteam = admin.SteamId;
        _ = Task.Run(() => LookupAndPrintAsync(invokerSteam, targetSteam, targetName));
    }

    // -------------------------------------------------------------------------
    // Command handler
    // -------------------------------------------------------------------------

    private void OnCsRepCommand(IGameClient? invoker, StringCommand command)
    {
        if (invoker is null || invoker.IsFakeClient)
            return;

        // Usage: csrep <#userid|name|steamid>
        if (command.ArgCount < 1)
        {
            invoker.Print(HudPrintChannel.Chat, " [CsRepGuard] Usage: !csrep <#userid|name|steamid64>");
            return;
        }

        var targetArg = command.GetArg(1);

        // Resolve target: try userid (#N), then steamid64, then name match.
        IGameClient? target = null;
        string?      targetSteam = null;

        if (targetArg.StartsWith('#') && ushort.TryParse(targetArg[1..], out var uid))
        {
            UserID userId = uid;
            target        = _bridge.ClientManager.GetGameClient(userId);
            targetSteam   = target is not null ? ((ulong) target.SteamId).ToString() : null;
        }
        else if (ulong.TryParse(targetArg, out var sid64))
        {
            targetSteam = sid64.ToString();
            target      = _bridge.ClientManager.GetGameClient((SteamID) sid64);
        }
        else
        {
            // Name search — find first online player whose name contains the arg.
            foreach (var c in _bridge.ClientManager.GetGameClients(inGame: true))
            {
                if (c.IsFakeClient || c.IsHltv) continue;
                if (c.Name?.Contains(targetArg, StringComparison.OrdinalIgnoreCase) == true)
                {
                    target      = c;
                    targetSteam = ((ulong) c.SteamId).ToString();
                    break;
                }
            }
        }

        if (targetSteam is null)
        {
            invoker.Print(HudPrintChannel.Chat, $" [CsRepGuard] Player not found: {targetArg}");
            return;
        }

        var targetName = target?.Name ?? targetSteam;
        invoker.Print(HudPrintChannel.Chat, $" [CsRepGuard] Looking up {targetName} ({targetSteam})...");

        // Fire async lookup off game thread, print result back via InvokeFrameAction.
        var invokerSteam = invoker.SteamId;
        _ = Task.Run(() => LookupAndPrintAsync(invokerSteam, targetSteam, targetName));
    }

    // -------------------------------------------------------------------------
    // Async lookup (off game thread)
    // -------------------------------------------------------------------------

    private async Task LookupAndPrintAsync(SteamID invokerSteam, string targetSteam, string targetName)
    {
        try
        {
            if (!ulong.TryParse(targetSteam, out var ulong64))
            {
                PrintToInvoker(invokerSteam, " [CsRepGuard] Invalid SteamID64.");
                return;
            }

            var steamId64 = (long) ulong64;
            var ttl       = _config.EffectiveCacheTtl;

            // Cache-first.
            CsRepPlayer? player = null;
            var          cached = await _db.GetCachedAsync(steamId64, ttl).ConfigureAwait(false);

            if (cached is not null)
            {
                player = cached.ToPlayer();
            }
            else
            {
                player = await _api.GetPlayerAsync(targetSteam).ConfigureAwait(false);
                if (player is not null)
                {
                    if (player.IsPrivate)
                    {
                        await _db.PurgePlayerAsync(steamId64).ConfigureAwait(false);
                    }
                    else
                    {
                        await _db.UpsertAsync(steamId64, player).ConfigureAwait(false);
                    }
                }
            }

            // §4: redacted check on the cached row too.
            if (cached?.Redacted == true)
            {
                await _db.PurgePlayerAsync(steamId64).ConfigureAwait(false);
                player = null;
                // Treat as private.
                var privMsg = " [CsRepGuard] Profile is private (redacted) — no data available. | via CSRep.gg";
                PrintToInvoker(invokerSteam, privMsg);
                return;
            }

            // §4: redacted from fresh API fetch.
            if (player?.IsPrivate == true)
            {
                PrintToInvoker(invokerSteam, " [CsRepGuard] Profile is private (redacted) — no data available. | via CSRep.gg");
                return;
            }

            if (player is null)
            {
                PrintToInvoker(invokerSteam, $" [CsRepGuard] No CSRep data for {targetName} yet (not yet indexed). | via CSRep.gg");
                return;
            }

            // Build output.
            var profileUrl = $"https://csrep.gg/players/{targetSteam}";
            var sb         = new StringBuilder();

            sb.AppendLine($" [CsRepGuard] === {targetName} ({targetSteam}) via CSRep.gg ===");
            sb.AppendLine($" Trust Rating : {(player.TrustRating.HasValue ? player.TrustRating.Value.ToString() : "N/A (not yet indexed)")}");

            if (player.Bans is { } bans)
            {
                sb.AppendLine($" VAC ban      : {BoolStr(bans.Vac)}");
                sb.AppendLine($" Game ban     : {BoolStr(bans.Game)}");
                sb.AppendLine($" Overwatch    : {BoolStr(bans.Overwatch)}");
                sb.AppendLine($" FACEIT ban   : {BoolStr(bans.Faceit)}");
                sb.AppendLine($" Economy ban  : {BoolStr(bans.Economy)}");
                sb.AppendLine($" Community ban: {BoolStr(bans.Community)}");
                if (bans.DaysSinceLastBan.HasValue)
                    sb.AppendLine($" Days since ban: {bans.DaysSinceLastBan.Value}");
            }
            else
            {
                sb.AppendLine(" Bans: no data yet");
            }

            sb.AppendLine($" FACEIT level: {(player.FaceitLevel.HasValue ? player.FaceitLevel.Value.ToString() : "N/A")}  ELO: {(player.FaceitElo.HasValue ? player.FaceitElo.Value.ToString() : "N/A")}");
            sb.AppendLine($" CS2 hours   : {(player.Cs2Hours.HasValue ? player.Cs2Hours.Value.ToString() : "N/A")}");
            sb.AppendLine($" Account age : {(string.IsNullOrEmpty(player.SteamCreatedAt) ? "N/A" : player.SteamCreatedAt)}");
            sb.AppendLine($" Profile     : {profileUrl}");
            sb.AppendLine(" (csrep is probabilistic — not proof of cheating)");

            // Print line-by-line (chat has line limits).
            var lines = sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _bridge.ModSharp.InvokeFrameAction(() =>
            {
                // Re-validate after the async hop: IsInGame is the only safe gate (IsConnected/IsValid
                // can be true during loading/limbo and yield a half-valid client).
                var inv = _bridge.ClientManager.GetGameClient(invokerSteam);
                if (inv is not { IsInGame: true }) return;
                foreach (var line in lines)
                    inv.Print(HudPrintChannel.Chat, line);
            });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[CsRepGuard] Command lookup failed for {Id}", targetSteam);
            PrintToInvoker(invokerSteam, " [CsRepGuard] Lookup failed — see server log.");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void PrintToInvoker(SteamID invokerSteam, string msg)
    {
        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            // IsInGame is the only safe gate after an async hop.
            var inv = _bridge.ClientManager.GetGameClient(invokerSteam);
            if (inv is not { IsInGame: true }) return;
            inv.Print(HudPrintChannel.Chat, msg);
        });
    }

    private static string BoolStr(bool v) => v ? "YES" : "No";
}
