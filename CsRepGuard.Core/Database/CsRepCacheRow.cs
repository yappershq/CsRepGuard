using System;
using SqlSugar;

namespace CsRepGuard.Database;

/// <summary>
/// Transient 24h cache of CSRep.gg player data. One row per SteamID64.
/// Per CSRep ToS §9 this is a PERFORMANCE cache only — rows older than 24h MUST be purged.
/// We do NOT store raw JSON or any long-term archive (§6.C).
/// </summary>
[SugarTable("csrep_cache")]
internal sealed class CsRepCacheRow
{
    /// <summary>SteamID64 as long (ulong doesn't map cleanly to BSON/SQL — store as long, cast at boundary).</summary>
    [SugarColumn(ColumnName = "SteamId", IsPrimaryKey = true)]
    public long SteamId { get; set; }

    // Ban flags
    [SugarColumn(ColumnName = "Vac")]       public bool Vac       { get; set; }
    [SugarColumn(ColumnName = "Game")]      public bool Game      { get; set; }
    [SugarColumn(ColumnName = "Faceit")]    public bool Faceit    { get; set; }
    [SugarColumn(ColumnName = "Economy")]   public bool Economy   { get; set; }
    [SugarColumn(ColumnName = "Community")] public bool Community { get; set; }
    [SugarColumn(ColumnName = "Overwatch")] public bool Overwatch { get; set; }

    [SugarColumn(ColumnName = "DaysSinceLastBan", IsNullable = true)]
    public int? DaysSinceLastBan { get; set; }

    [SugarColumn(ColumnName = "TrustRating", IsNullable = true)]
    public int? TrustRating { get; set; }

    [SugarColumn(ColumnName = "FaceitLevel", IsNullable = true)]
    public int? FaceitLevel { get; set; }

    [SugarColumn(ColumnName = "FaceitElo", IsNullable = true)]
    public int? FaceitElo { get; set; }

    [SugarColumn(ColumnName = "Cs2Hours", IsNullable = true)]
    public int? Cs2Hours { get; set; }

    /// <summary>Steam account creation date string as returned by CSRep (ISO-8601 or similar).</summary>
    [SugarColumn(ColumnName = "SteamCreatedAt", Length = 32, IsNullable = true)]
    public string? SteamCreatedAt { get; set; }

    [SugarColumn(ColumnName = "SteamLevel", IsNullable = true)]
    public int? SteamLevel { get; set; }

    /// <summary>True if the player has opted out (redacted/anonymous). We skip display and purge immediately.</summary>
    [SugarColumn(ColumnName = "Redacted")]
    public bool Redacted { get; set; }

    /// <summary>When the CSRep data was last fetched. Used for TTL enforcement and purge.</summary>
    [SugarColumn(ColumnName = "RefreshedAt")]
    public DateTime RefreshedAt { get; set; }
}
