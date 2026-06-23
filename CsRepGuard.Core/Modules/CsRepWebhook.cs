using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CsRepGuard.Modules;

/// <summary>
/// Discord webhook poster for CSRep flag-detection events. Best-effort, off game thread, never throws.
/// Per ToS §6.B: notification is STAFF-ONLY advisory — no automated action. Human decides.
/// Per ToS §8: every post attributes data to CSRep.gg with a profile link.
/// Per ToS §6.A: every post includes the probabilistic disclaimer.
/// </summary>
internal sealed class CsRepWebhook
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string  _url;
    private readonly ILogger _logger;

    public bool Enabled => !string.IsNullOrWhiteSpace(_url);

    public CsRepWebhook(string url, ILogger logger)
    {
        _url    = url ?? string.Empty;
        _logger = logger;
    }

    /// <summary>
    /// Post a staff-advisory embed for a connecting player whose CSRep flags matched the notify list.
    /// NOTIFY ONLY — no automated action is taken; a human staff member must decide how to respond.
    /// </summary>
    public async Task PostFlaggedAsync(
        string name,
        string steamId64,
        string flagsTriggered,
        int?   trustRating,
        string serverName)
    {
        if (!Enabled) return;

        try
        {
            var profileUrl = $"https://csrep.gg/players/{steamId64}";
            var steamUrl   = $"https://steamcommunity.com/profiles/{steamId64}";

            var description = $"**{Sanitize(name)}** connected with CSRep ban flags detected.\n" +
                              $"**Staff advisory only** — a human must review and decide any action.\n" +
                              $"_(csrep is probabilistic — not proof of cheating)_";

            var payload = new
            {
                username = "CsRepGuard",
                embeds = new[]
                {
                    new
                    {
                        title       = "CSRep Flag Alert",
                        color       = 0xE67E22, // orange — advisory, not definitive
                        description,
                        fields = new object[]
                        {
                            new { name = "Player",      value = $"[{Sanitize(name)}]({steamUrl})",        inline = true  },
                            new { name = "SteamID",     value = $"`{steamId64}`",                         inline = true  },
                            new { name = "Server",      value = string.IsNullOrEmpty(serverName) ? "—" : serverName, inline = true },
                            new { name = "Flags",       value = string.IsNullOrEmpty(flagsTriggered) ? "—" : flagsTriggered, inline = true },
                            new { name = "Trust Rating",value = trustRating.HasValue ? trustRating.Value.ToString() : "N/A (not yet indexed)", inline = true },
                            new { name = "CSRep Profile", value = $"[View on CSRep.gg]({profileUrl})", inline = false },
                        },
                        footer = new { text = "Data via CSRep.gg — probabilistic, not proof. Human review required before any action." },
                        timestamp = DateTime.UtcNow.ToString("o"),
                    },
                },
            };

            var json     = JsonSerializer.Serialize(payload);
            using var c  = new StringContent(json, Encoding.UTF8, "application/json");
            using var rsp = await Http.PostAsync(_url, c).ConfigureAwait(false);
            if (!rsp.IsSuccessStatusCode)
                _logger.LogWarning("[CsRepGuard] Webhook POST returned {Code}", (int) rsp.StatusCode);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[CsRepGuard] Webhook POST failed");
        }
    }

    // Discord markdown-escape + length clamp for player names.
    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        s = s.Replace("`", "'").Replace("*", "").Replace("_", "").Replace("@", "@​");
        return s.Length > 64 ? s[..64] : s;
    }
}
