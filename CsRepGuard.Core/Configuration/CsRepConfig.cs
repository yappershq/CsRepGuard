using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CsRepGuard.Configuration;

/// <summary>
/// Main configuration for CsRepGuard. Loaded from configs/csrepguard.json on startup;
/// default file is written if absent.
/// </summary>
public sealed class CsRepConfig
{
    /// <summary>Master switch. When false no checks are performed.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>
    /// CSRep.gg API key (X-API-Key header). Set in config — never hardcoded.
    /// </summary>
    [JsonPropertyName("csrep_api_key")] public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Which ban flags trigger a staff Discord notification.
    /// Valid values: vac, game, overwatch, faceit, economy, community.
    /// Default: vac, game, overwatch.
    /// </summary>
    [JsonPropertyName("csrep_notify_flags")] public List<string> NotifyFlags { get; set; } = ["vac", "game", "overwatch"];

    /// <summary>
    /// Cache TTL in hours. Hard-capped at 24 per CSRep ToS §9.
    /// </summary>
    [JsonPropertyName("csrep_cache_ttl_hours")] public int CacheTtlHours { get; set; } = 24;

    /// <summary>
    /// Discord webhook URL for staff notifications. Empty = no webhook.
    /// </summary>
    [JsonPropertyName("csrep_webhook_url")] public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Shared bypass file (configs/) listing SteamID64s that skip CSRep checks.
    /// Shared with AltGuard / AntiVpnGuard by default.
    /// </summary>
    [JsonPropertyName("sharedBypassConfig")] public string SharedBypassConfig { get; set; } = "bypass_steamids.json";

    /// <summary>
    /// Config file (configs/) containing the PlayerAnalytics DB credentials block.
    /// Defaults to PlayerAnalytics' own file so we never duplicate credentials.
    /// </summary>
    [JsonPropertyName("analyticsDatabaseConfig")] public string AnalyticsDatabaseConfig { get; set; } = "playeranalytics.database.jsonc";

    /// <summary>Server display name included in webhook notifications.</summary>
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = "CS2 Server";

    // -------------------------------------------------------------------------
    // Derived
    // -------------------------------------------------------------------------

    /// <summary>Effective TTL capped at 24 hours per ToS §9.</summary>
    [JsonIgnore]
    public TimeSpan EffectiveCacheTtl => TimeSpan.FromHours(Math.Min(24, Math.Max(1, CacheTtlHours)));

    /// <summary>Normalised notify flag set (lowercase).</summary>
    [JsonIgnore]
    public HashSet<string> NotifyFlagSet
    {
        get
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in NotifyFlags) set.Add(f.Trim().ToLowerInvariant());
            return set;
        }
    }

    // -------------------------------------------------------------------------
    // Load / Save
    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        WriteIndented               = true,
    };

    public static CsRepConfig Load(string sharpPath, ILogger logger)
    {
        var path = Path.Combine(sharpPath, "configs", "csrepguard.json");
        try
        {
            if (!File.Exists(path))
            {
                var def = new CsRepConfig();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(def, JsonOpts));
                logger.LogInformation("[CsRepGuard] Wrote default config to {Path}", path);
                return def;
            }

            var cfg = JsonSerializer.Deserialize<CsRepConfig>(File.ReadAllText(path), JsonOpts);
            if (cfg is null)
            {
                logger.LogError("[CsRepGuard] csrepguard.json deserialized to null — using defaults");
                return new CsRepConfig();
            }
            return cfg;
        }
        catch (Exception e)
        {
            logger.LogError(e, "[CsRepGuard] Failed to load csrepguard.json — using defaults");
            return new CsRepConfig();
        }
    }
}

/// <summary>
/// Database connection config — mirrors PlayerAnalytics' { "database": { ... } } schema.
/// </summary>
public sealed class DatabaseConfig
{
    [JsonPropertyName("type")]     public string Type     { get; set; } = "mysql";
    [JsonPropertyName("host")]     public string Host     { get; set; } = "localhost";
    [JsonPropertyName("port")]     public int    Port     { get; set; } = 3306;
    [JsonPropertyName("database")] public string Database { get; set; } = "player_analytics";
    [JsonPropertyName("user")]     public string User     { get; set; } = "root";
    [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;

    private sealed class AnalyticsDbFile
    {
        [JsonPropertyName("database")] public DatabaseConfig? Database { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    /// <summary>
    /// Reuse an existing DB config already on the server so CsRepGuard never duplicates credentials.
    /// Returns null if the file is missing or unparseable.
    /// </summary>
    public static DatabaseConfig? LoadShared(string sharpPath, string fileName, ILogger logger)
    {
        var path = Path.Combine(sharpPath, "configs", fileName);
        try
        {
            if (!File.Exists(path))
            {
                logger.LogError("[CsRepGuard] Shared DB config '{Path}' not found — set 'analyticsDatabaseConfig' or create it", path);
                return null;
            }

            var db = JsonSerializer.Deserialize<AnalyticsDbFile>(File.ReadAllText(path), JsonOpts)?.Database;
            if (db is null || string.IsNullOrWhiteSpace(db.Host))
            {
                logger.LogError("[CsRepGuard] '{Path}' has no usable Database section", path);
                return null;
            }

            logger.LogInformation("[CsRepGuard] Using DB config from {File}", fileName);
            return db;
        }
        catch (Exception e)
        {
            logger.LogError(e, "[CsRepGuard] Failed to read shared DB config '{Path}'", path);
            return null;
        }
    }
}
