using System;
using System.Threading;
using System.Threading.Tasks;
using CsRepGuard.Configuration;
using CsRepGuard.Modules;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace CsRepGuard.Database;

/// <summary>
/// SqlSugar access to the PlayerAnalytics DB for the csrep_cache table.
/// Responsibilities:
///   - Ensure table exists on startup (CodeFirst).
///   - Cache-first reads: serve from DB if within TTL, else signal "stale" so caller hits API.
///   - Upsert after fresh API fetch.
///   - Hourly purge of rows older than 24h (ToS §9 — must actively delete, not just re-fetch).
///   - Purge any cached row for a redacted player (§4 Privacy Mode).
/// All calls run off the game thread.
/// </summary>
internal sealed class CsRepDatabase : IDisposable
{
    private readonly ILogger<CsRepDatabase> _logger;
    private SqlSugarScope?                  _db;
    private Timer?                          _purgeTimer;
    private bool                            _disposed;

    public bool IsConnected => _db is not null;

    public CsRepDatabase(ILogger<CsRepDatabase> logger) => _logger = logger;

    // -------------------------------------------------------------------------
    // Connection
    // -------------------------------------------------------------------------

    public bool Connect(DatabaseConfig cfg)
    {
        try
        {
            var dbType = cfg.Type.ToLowerInvariant() switch
            {
                "mysql"      => DbType.MySql,
                "postgresql" => DbType.PostgreSQL,
                _            => throw new NotSupportedException($"Unsupported DB type '{cfg.Type}' (mysql|postgresql)"),
            };

            // Cap pool size — many plugins share the same MySQL box; default (100) exhausts max_connections.
            var conn = dbType switch
            {
                DbType.MySql => $"Server={cfg.Host};Port={cfg.Port};Database={cfg.Database};User={cfg.User};Password={cfg.Password};AllowPublicKeyRetrieval=true;Maximum Pool Size=4;Minimum Pool Size=0;",
                _            => $"Host={cfg.Host};Port={cfg.Port};Database={cfg.Database};Username={cfg.User};Password={cfg.Password};Maximum Pool Size=4;Minimum Pool Size=0;",
            };

            _db = new SqlSugarScope(new ConnectionConfig
            {
                DbType                = dbType,
                ConnectionString      = conn,
                IsAutoCloseConnection = true,
                InitKeyType           = InitKeyType.Attribute,
            });

            // Probe — bad creds fail loudly at load, not on first player connect.
            _ = _db.Ado.GetInt("SELECT 1");
            _logger.LogInformation("[CsRepGuard] Connected to DB {Host}/{Database}", cfg.Host, cfg.Database);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[CsRepGuard] Failed to connect to DB — cache disabled");
            _db = null;
            return false;
        }
    }

    /// <summary>Create the csrep_cache table if it doesn't exist.</summary>
    public void EnsureCacheTable()
    {
        if (_db is null) return;
        try
        {
            _db.CodeFirst.InitTables<CsRepCacheRow>();
            _logger.LogInformation("[CsRepGuard] csrep_cache table ready");
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[CsRepGuard] Could not ensure csrep_cache table");
        }
    }

    // -------------------------------------------------------------------------
    // Purge timer (ToS §9 — must actively delete rows older than 24h)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Start the hourly purge timer. Must be called after EnsureCacheTable.
    /// </summary>
    public void StartPurgeTimer()
    {
        if (_db is null) return;

        // Run once immediately, then every hour.
        var period = TimeSpan.FromHours(1);
        _purgeTimer = new Timer(_ =>
        {
            if (!_disposed) _ = PurgeStaleRowsAsync();
        }, null, TimeSpan.Zero, period);
    }

    private async Task PurgeStaleRowsAsync()
    {
        if (_db is null) return;
        try
        {
            var cutoff  = DateTime.UtcNow.AddHours(-24);
            var deleted = await _db.Deleteable<CsRepCacheRow>()
                .Where(r => r.RefreshedAt < cutoff)
                .ExecuteCommandAsync()
                .ConfigureAwait(false);

            if (deleted > 0)
                _logger.LogInformation("[CsRepGuard] Purged {Count} stale cache rows (>24h old)", deleted);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[CsRepGuard] Cache purge failed");
        }
    }

    // -------------------------------------------------------------------------
    // Cache read / write
    // -------------------------------------------------------------------------

    /// <summary>
    /// Try to load a cached row for <paramref name="steamId64"/>. Returns null if not cached or stale.
    /// </summary>
    public async Task<CsRepCacheRow?> GetCachedAsync(long steamId64, TimeSpan ttl)
    {
        if (_db is null) return null;
        try
        {
            var row = await _db.Queryable<CsRepCacheRow>()
                .Where(r => r.SteamId == steamId64)
                .FirstAsync()
                .ConfigureAwait(false);

            if (row is null) return null;

            // Stale — caller should hit API and upsert.
            if (DateTime.UtcNow - row.RefreshedAt > ttl) return null;

            return row;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[CsRepGuard] Cache read failed for {Id}", steamId64);
            return null;
        }
    }

    /// <summary>
    /// Upsert a fresh row from the API. If the player is redacted, purge any existing row (§4).
    /// </summary>
    public async Task UpsertAsync(long steamId64, CsRepPlayer player)
    {
        if (_db is null) return;
        try
        {
            // Privacy Mode §4: if player is redacted/anonymous, purge and do NOT store.
            if (player.IsPrivate)
            {
                await PurgePlayerAsync(steamId64).ConfigureAwait(false);
                return;
            }

            var row = new CsRepCacheRow
            {
                SteamId          = steamId64,
                Vac              = player.Bans?.Vac       ?? false,
                Game             = player.Bans?.Game      ?? false,
                Faceit           = player.Bans?.Faceit    ?? false,
                Economy          = player.Bans?.Economy   ?? false,
                Community        = player.Bans?.Community ?? false,
                Overwatch        = player.Bans?.Overwatch ?? false,
                DaysSinceLastBan = player.Bans?.DaysSinceLastBan,
                TrustRating      = player.TrustRating,
                FaceitLevel      = player.FaceitLevel,
                FaceitElo        = player.FaceitElo,
                Cs2Hours         = player.Cs2Hours,
                SteamCreatedAt   = player.SteamCreatedAt,
                SteamLevel       = player.SteamLevel,
                Redacted         = false,
                RefreshedAt      = DateTime.UtcNow,
            };

            // Manual find-then-update/insert (SqlSugar compound-key upserts rejected — see memory note).
            var exists = await _db.Queryable<CsRepCacheRow>()
                .Where(r => r.SteamId == steamId64)
                .AnyAsync()
                .ConfigureAwait(false);

            if (exists)
                await _db.Updateable(row).ExecuteCommandAsync().ConfigureAwait(false);
            else
                await _db.Insertable(row).ExecuteCommandAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[CsRepGuard] Cache upsert failed for {Id}", steamId64);
        }
    }

    /// <summary>Delete any cached row for this player (called for redacted players per §4).</summary>
    public async Task PurgePlayerAsync(long steamId64)
    {
        if (_db is null) return;
        try
        {
            await _db.Deleteable<CsRepCacheRow>()
                .Where(r => r.SteamId == steamId64)
                .ExecuteCommandAsync()
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[CsRepGuard] Purge failed for {Id}", steamId64);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _purgeTimer?.Dispose();
        _db?.Dispose();
        _db = null;
    }
}
