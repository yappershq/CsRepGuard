using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CsRepGuard.Modules;

// -------------------------------------------------------------------------
// API response models
// -------------------------------------------------------------------------

/// <summary>Ban flags from CSRep.gg PlayerEntity.</summary>
public sealed class CsRepBans
{
    [JsonPropertyName("vac")]                 public bool Vac               { get; set; }
    [JsonPropertyName("game")]                public bool Game              { get; set; }
    [JsonPropertyName("faceit")]              public bool Faceit            { get; set; }
    [JsonPropertyName("economy")]             public bool Economy           { get; set; }
    [JsonPropertyName("community")]           public bool Community         { get; set; }
    [JsonPropertyName("overwatch")]           public bool Overwatch         { get; set; }
    [JsonPropertyName("days_since_last_ban")] public int? DaysSinceLastBan { get; set; }
}

/// <summary>Relevant fields from CSRep.gg PlayerEntity.</summary>
public sealed class CsRepPlayer
{
    [JsonPropertyName("bans")]           public CsRepBans? Bans         { get; set; }
    [JsonPropertyName("trust_rating")]   public int?       TrustRating  { get; set; }
    [JsonPropertyName("faceit_level")]   public int?       FaceitLevel  { get; set; }
    [JsonPropertyName("faceit_elo")]     public int?       FaceitElo    { get; set; }
    [JsonPropertyName("premier_elo")]    public int?       PremierElo   { get; set; }
    [JsonPropertyName("cs2_hours")]      public int?       Cs2Hours     { get; set; }
    [JsonPropertyName("steam_created_at")] public string?  SteamCreatedAt { get; set; }
    [JsonPropertyName("steam_level")]    public int?       SteamLevel   { get; set; }
    [JsonPropertyName("name")]           public string?    Name         { get; set; }
    [JsonPropertyName("redacted")]       public bool       Redacted     { get; set; }
    [JsonPropertyName("anonymous")]      public bool       Anonymous    { get; set; }

    /// <summary>True if the player has opted out of data display per CSRep ToS §4.</summary>
    [JsonIgnore]
    public bool IsPrivate => Redacted || Anonymous;
}

internal sealed class CsRepResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("result")] public CsRepPlayer? Result { get; set; }
}

internal sealed class CsRepBatchResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("result")] public List<CsRepPlayer>? Result { get; set; }
}

// -------------------------------------------------------------------------
// API Client
// -------------------------------------------------------------------------

/// <summary>
/// Minimal CSRep.gg API client. GET /players/{id} and batch GET /players?ids=.
/// All calls are best-effort: returns null on error/timeout/non-OK status.
/// </summary>
internal sealed class CsRepApiClient
{
    private const string BaseUrl = "https://csrep.gg/api";

    // Shared across all calls — HttpClient is thread-safe; one instance per plugin lifetime.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string  _apiKey;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public CsRepApiClient(string apiKey, ILogger logger)
    {
        _apiKey = apiKey;
        _logger = logger;
    }

    public bool HasKey => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>
    /// GET /players/{steamId64}. Returns null if the API is unreachable, returns ERROR, or the
    /// key tier returns 403. Treat null as "no signal yet" per CSRep docs.
    /// </summary>
    public async Task<CsRepPlayer?> GetPlayerAsync(string steamId64)
    {
        if (!HasKey) return null;
        try
        {
            using var req = BuildRequest(HttpMethod.Get, $"{BaseUrl}/players/{steamId64}");
            using var rsp = await Http.SendAsync(req).ConfigureAwait(false);

            if (!rsp.IsSuccessStatusCode)
            {
                _logger.LogDebug("[CsRepGuard] API GET /players/{Id} returned {Code}", steamId64, (int) rsp.StatusCode);
                return null;
            }

            var body     = await rsp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<CsRepResponse>(body, JsonOpts);
            if (envelope?.Status != "OK" || envelope.Result is null)
            {
                _logger.LogDebug("[CsRepGuard] API /players/{Id} status={Status}", steamId64, envelope?.Status);
                return null;
            }

            return envelope.Result;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("[CsRepGuard] API timeout for {Id}", steamId64);
            return null;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[CsRepGuard] API error for {Id}", steamId64);
            return null;
        }
    }

    /// <summary>
    /// Batch GET /players?ids=&amp;ids=. Returns empty list on failure.
    /// CSRep supports multi-id via repeated ?ids= query params.
    /// </summary>
    public async Task<List<CsRepPlayer>> GetPlayersAsync(IEnumerable<string> steamId64s)
    {
        if (!HasKey) return [];
        try
        {
            var ids = string.Join("&ids=", steamId64s);
            if (string.IsNullOrEmpty(ids)) return [];

            using var req = BuildRequest(HttpMethod.Get, $"{BaseUrl}/players?ids={ids}");
            using var rsp = await Http.SendAsync(req).ConfigureAwait(false);

            if (!rsp.IsSuccessStatusCode)
            {
                _logger.LogDebug("[CsRepGuard] API batch GET /players returned {Code}", (int) rsp.StatusCode);
                return [];
            }

            var body     = await rsp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<CsRepBatchResponse>(body, JsonOpts);
            return envelope?.Status == "OK" ? (envelope.Result ?? []) : [];
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("[CsRepGuard] API batch timeout");
            return [];
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[CsRepGuard] API batch error");
            return [];
        }
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-API-Key", _apiKey);
        return req;
    }
}
