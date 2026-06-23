using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using CsRepGuard.Configuration;
using CsRepGuard.Database;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace CsRepGuard.Modules;

/// <summary>
/// Connect-time CSRep.gg check. Fires on OnClientPostAdminCheck (post-auth, IP known).
/// All network/DB work is off the game thread; InvokeFrameAction marshals back for any game-thread
/// operations. On flag match: NOTIFY ONLY (webhook + in-game admin chat).
///
/// ToS §6.B: NO automated bans or kicks. A human staff member decides any action.
/// ToS §4: Redacted/anonymous players are skipped and any cached data purged immediately.
/// ToS §9: Cache max 24h; hourly purge timer in CsRepDatabase.
/// ToS §8: All outputs attribute data to CSRep.gg with profile link.
/// </summary>
internal sealed class CsRepCheckModule : IClientListener
{
    public int ListenerVersion  => IClientListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge              _bridge;
    private readonly CsRepConfig                  _config;
    private readonly CsRepDatabase                _db;
    private readonly CsRepApiClient               _api;
    private readonly CsRepWebhook                 _webhook;
    private readonly ILogger<CsRepCheckModule>    _logger;
    private readonly HashSet<string>              _bypass;

    private bool _installed;

    public CsRepCheckModule(
        InterfaceBridge           bridge,
        CsRepConfig               config,
        CsRepDatabase             db,
        CsRepApiClient            api,
        CsRepWebhook              webhook,
        HashSet<string>           bypass,
        ILogger<CsRepCheckModule> logger)
    {
        _bridge  = bridge;
        _config  = config;
        _db      = db;
        _api     = api;
        _webhook = webhook;
        _bypass  = bypass;
        _logger  = logger;
    }

    public void Start()
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("[CsRepGuard] Disabled by config");
            return;
        }
        if (!_api.HasKey)
        {
            _logger.LogWarning("[CsRepGuard] No API key configured (csrep_api_key) — detection inactive");
            return;
        }

        _bridge.ClientManager.InstallClientListener(this);
        _installed = true;
        _logger.LogInformation("[CsRepGuard] Active — notify flags=[{Flags}], cache TTL={Ttl}h",
            string.Join(",", _config.NotifyFlags), (int) _config.EffectiveCacheTtl.TotalHours);
    }

    public void Stop()
    {
        if (_installed)
            _bridge.ClientManager.RemoveClientListener(this);
        _installed = false;
    }

    // -------------------------------------------------------------------------
    // IClientListener
    // -------------------------------------------------------------------------

    public void OnClientPostAdminCheck(IGameClient client)
    {
        if (client.IsFakeClient || client.IsHltv)
            return;

        var steamId  = client.SteamId;
        var steamStr = ((ulong) steamId).ToString();

        // Bypass list check (shared with AltGuard / AntiVpnGuard).
        if (_bypass.Contains(steamStr))
            return;

        // Admin bypass — admins skip the check to avoid false alerts from staff members.
        if (_bridge.AdminManager?.GetAdmin(steamId) is not null)
            return;

        var name = client.Name ?? "?";
        _ = Task.Run(() => CheckAsync(steamId, steamStr, name));
    }

    // -------------------------------------------------------------------------
    // Async check (runs on thread pool)
    // -------------------------------------------------------------------------

    private async Task CheckAsync(SteamID steamId, string steamStr, string name)
    {
        try
        {
            var steamId64 = (long) (ulong) steamId;
            var ttl       = _config.EffectiveCacheTtl;

            // 1. Cache-first lookup.
            CsRepPlayer? player  = null;
            var          cached  = await _db.GetCachedAsync(steamId64, ttl).ConfigureAwait(false);

            if (cached is not null)
            {
                // Serve from cache.
                player = CachedToPlayer(cached);
            }
            else
            {
                // Cache miss or stale — hit the API.
                player = await _api.GetPlayerAsync(steamStr).ConfigureAwait(false);

                if (player is not null)
                {
                    // §4 Privacy Mode: if redacted, purge any existing cached row and skip.
                    if (player.IsPrivate)
                    {
                        await _db.PurgePlayerAsync(steamId64).ConfigureAwait(false);
                        _logger.LogDebug("[CsRepGuard] {Id} is redacted/anonymous — skipping", steamStr);
                        return;
                    }

                    // Persist to cache.
                    await _db.UpsertAsync(steamId64, player).ConfigureAwait(false);
                }
                else
                {
                    // API returned null (not yet indexed, timeout, etc.) — treat as no signal.
                    _logger.LogDebug("[CsRepGuard] No data for {Id} yet (null API response)", steamStr);
                    return;
                }
            }

            // §4: If cached row says redacted (from a prior fetch), purge and skip.
            if (cached?.Redacted == true)
            {
                await _db.PurgePlayerAsync(steamId64).ConfigureAwait(false);
                return;
            }

            // 2. Evaluate flags.
            var triggeredFlags = EvalFlags(player, _config.NotifyFlagSet);
            if (triggeredFlags.Count == 0)
                return; // No matching flags — clean or no signal yet.

            // 3. Marshal notify to game thread.
            var flagStr    = string.Join(", ", triggeredFlags);
            var trustRating = player.TrustRating;
            _bridge.ModSharp.InvokeFrameAction(() => NotifyStaff(steamId, steamStr, name, flagStr, trustRating));

            // 4. Webhook post (already off game thread — fire async).
            await _webhook.PostFlaggedAsync(name, steamStr, flagStr, trustRating, _config.ServerName)
                           .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[CsRepGuard] Check failed for {Id}", steamStr);
        }
    }

    // -------------------------------------------------------------------------
    // Game-thread: notify online admins via in-game chat
    // -------------------------------------------------------------------------

    private void NotifyStaff(SteamID steamId, string steamStr, string name, string flags, int? trustRating)
    {
        // Re-validate: player may have left while we queried.
        // Webhook was already sent async — in-game chat is best-effort; skip if disconnected.
        var client = _bridge.ClientManager.GetGameClient(steamId);
        var playerStillOnServer = client is not null && client.IsValid && !client.IsFakeClient;

        // §8 Attribution + §6.A disclaimer in every output.
        var profileLink = $"https://csrep.gg/players/{steamStr}";
        var trust       = trustRating.HasValue ? trustRating.Value.ToString() : "N/A";
        var msg = $" [CsRepGuard] {name} <{steamStr}> flagged: {flags} | Trust: {trust} | {profileLink} | (csrep is probabilistic — not proof of cheating)";

        _logger.LogWarning("[CsRepGuard] Flagged player: {Name} ({Id}) flags={Flags} trust={Trust} stillOnServer={OnServer}",
            name, steamStr, flags, trust, playerStillOnServer);

        foreach (var c in _bridge.ClientManager.GetGameClients(inGame: true))
        {
            if (c.IsFakeClient || c.IsHltv) continue;
            if (_bridge.AdminManager?.GetAdmin(c.SteamId) is null) continue;
            c.Print(HudPrintChannel.Chat, msg);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the triggered flag names given the player's ban data and the configured notify set.
    /// Empty = no match. Null bans = no signal yet, return empty.
    /// </summary>
    private static List<string> EvalFlags(CsRepPlayer player, HashSet<string> notifySet)
    {
        var triggered = new List<string>();
        if (player.Bans is not { } bans) return triggered;

        if (notifySet.Contains("vac")       && bans.Vac)       triggered.Add("VAC");
        if (notifySet.Contains("game")      && bans.Game)       triggered.Add("Game ban");
        if (notifySet.Contains("overwatch") && bans.Overwatch)  triggered.Add("Overwatch ban");
        if (notifySet.Contains("faceit")    && bans.Faceit)     triggered.Add("FACEIT ban");
        if (notifySet.Contains("economy")   && bans.Economy)    triggered.Add("Economy ban");
        if (notifySet.Contains("community") && bans.Community)  triggered.Add("Community ban");

        return triggered;
    }

    /// <summary>Convert a DB cache row back into a CsRepPlayer (to reuse flag evaluation).</summary>
    private static CsRepPlayer CachedToPlayer(CsRepCacheRow row) => new()
    {
        Bans = new CsRepBans
        {
            Vac              = row.Vac,
            Game             = row.Game,
            Faceit           = row.Faceit,
            Economy          = row.Economy,
            Community        = row.Community,
            Overwatch        = row.Overwatch,
            DaysSinceLastBan = row.DaysSinceLastBan,
        },
        TrustRating    = row.TrustRating,
        FaceitLevel    = row.FaceitLevel,
        FaceitElo      = row.FaceitElo,
        Cs2Hours       = row.Cs2Hours,
        SteamCreatedAt = row.SteamCreatedAt,
        SteamLevel     = row.SteamLevel,
        Redacted       = row.Redacted,
    };
}
