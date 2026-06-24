<div align="center">
  <h1><strong>CsRepGuard</strong></h1>
  <p>CSRep.gg reputation &amp; ban lookups for CS2 — connect-time staff notify and an in-game admin lookup command. Notify-only, never auto-punishes.</p>
</div>

<p align="center">
  <a href="https://github.com/Kxnrl/modsharp-public"><img src="https://img.shields.io/badge/framework-ModSharp-5865F2?logo=github" alt="ModSharp"></a>
  <img src="https://img.shields.io/badge/game-CS2-orange" alt="CS2">
  <img src="https://img.shields.io/github/stars/yappershq/CsRepGuard?style=flat&logo=github" alt="Stars">
</p>

---

CsRepGuard cross-references connecting players against [CSRep.gg](https://csrep.gg) and alerts online staff (Discord webhook + in-game chat) when ban flags are detected. It is a **staff advisory** tool: per CSRep ToS it never bans or kicks — a human reviews and decides. Lookups are cached in the existing PlayerAnalytics database (24h TTL, hourly purge) so repeat connects don't re-hit the API.

## 🚀 Install

Copy the build output into your ModSharp install (`<sharp>` = your `sharp` directory):

| From | To |
|------|----|
| `.build/modules/CsRepGuard.Core/` | `<sharp>/modules/CsRepGuard.Core/` |
| `CsRepGuard.Core/.assets/configs/csrepguard.json` | `<sharp>/configs/csrepguard.json` |

Restart the server (or change map) to load. A default `configs/csrepguard.json` is written automatically on first run if absent.

**Requires** AdminManager (ships with ModSharp) — the `!csrep` / `!rep` command is permission-gated and is not registered without it. A CSRep.gg API key is required for any lookups. The connect-time DB cache reuses the **PlayerAnalytics** database config (`playeranalytics.database.jsonc`); without a reachable DB the plugin still works but makes a live API call on every connect. The optional in-game **AdminPanel** menu action is registered only if that plugin is installed.

## ⌨️ Commands

| Command | Aliases | Description | Permission |
|---------|---------|-------------|------------|
| `!csrep <#userid\|name\|steamid64>` | `!rep` | Look up a player's CSRep trust rating, ban flags, FACEIT level/ELO, CS2 hours and account age (cache-first). | `csrepguard:lookup` |

If AdminPanel is installed, a **CS Rep lookup** action is added to the per-player admin menu, gated by the same `csrepguard:lookup` permission.

## ⚙️ Configuration

`configs/csrepguard.json` (auto-generated on first run):

| Setting | Default | Meaning |
|---------|---------|---------|
| `enabled` | `true` | Master switch for connect-time checks. |
| `csrep_api_key` | `""` | CSRep.gg API key (`X-API-Key` header). Required for any lookups. |
| `csrep_notify_flags` | `["vac","game","overwatch"]` | Which ban flags trigger a staff alert. Valid: `vac`, `game`, `overwatch`, `faceit`, `economy`, `community`. |
| `csrep_cache_ttl_hours` | `24` | Cache lifetime in hours. Clamped to 1–24 (hard cap 24 per CSRep ToS). |
| `csrep_webhook_url` | `""` | Discord webhook for staff flag alerts. Empty disables the webhook. |
| `serverName` | `"CS2 Server"` | Server name included in webhook notifications. |
| `sharedBypassConfig` | `"bypass_steamids.json"` | SteamID64 bypass list (shared with AltGuard / AntiVpnGuard) — listed players skip the check. |
| `analyticsDatabaseConfig` | `"playeranalytics.database.jsonc"` | Config file whose `database` block supplies the cache DB credentials (reused, not duplicated). |

## 🔧 How it works

On `OnClientPostAdminCheck` (post-auth, fake clients / HLTV / bypass-listed / admins skipped) CsRepGuard runs a cache-first lookup off the game thread: it serves a fresh cached row if present, otherwise hits the CSRep.gg API and upserts the result. If any configured ban flag is set, it notifies online admins via in-game chat and posts to the Discord webhook — it never bans or kicks. Private/redacted profiles are skipped and any cached row is purged. The `!csrep` command runs the same cache-first lookup on demand against any online player or SteamID64. Cache rows are capped at 24h and an hourly timer deletes stale entries.

## 📦 Build

```bash
dotnet build -c Release
```

Outputs `.build/modules/CsRepGuard.Core/CsRepGuard.dll` (plus its bundled dependencies). Ship the `csrepguard.json` default from `CsRepGuard.Core/.assets/configs/`.

## 🙏 Credits

Reputation and ban data provided by [CSRep.gg](https://csrep.gg). All in-game and webhook output attributes data to CSRep.gg with a profile link; ratings are probabilistic and are not proof of cheating.

---

<div align="center">
  <p>Made with ❤️ by <a href="https://github.com/yappershq">yappershq</a></p>
  <p>⭐ Star this repo if you find it useful!</p>
</div>
