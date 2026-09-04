# FlatTrade Spike

Pre-Phase-1 validation tool (plan `docs/PLAN.md` §15.5) — proves the FlatTrade auth flow and WebSocket message format hands-on before the real ingestion pipeline is built around assumptions from the docs. Throwaway by design: not part of the module graph, no production code depends on it.

## Setup

1. On FlatTrade's Wall dashboard: Pi > api(v2) > Create New API Key. Note the API key and API secret.
2. Create `appsettings.Local.json` next to this README (gitignored, never commit it):

   ```json
   { "UserId": "<flattrade client id>", "ApiKey": "<pi connect api key>", "ApiSecret": "<pi connect api secret>" }
   ```

## Run

Two steps, run separately — the browser login in between is a password/OTP step only you should do, so this doesn't block on stdin waiting for it.

**Step 1** — print the authorize URL:

```bash
dotnet run --project spikes/FlatTradeSpike
```

Open the printed URL in a real browser and log in. FlatTrade redirects to your app's configured Redirect URL with `?request_code=...` in the query string — copy that URL (or just the code).

**Step 2** — run again, passing the pasted redirect (or just the code) as the first argument, and optionally a duration in seconds as the second (defaults to 300 = 5 minutes):

```bash
dotnet run --project spikes/FlatTradeSpike -- "<pasted redirect or code>" 300
```

This step:

1. Exchanges the code for a session token.
2. Searches NFO for a live NIFTY option and subscribes to its touchline + depth feed.
3. Logs every raw WebSocket message (with local receive timestamp) to `spikes/FlatTradeSpike/output/ticks-<timestamp>.log` for the configured duration.
4. Prints a summary of message counts by type (`ak`, `tk`, `tf`, `dk`, `df`, ...).

## What to check afterward

- Did the connect handshake actually ack with `t:"ak"` (confirmed against the live docs 2026-09-03 — see `docs/PLAN.md` §4.3)? If it's still `"ck"` after all, the docs' own changelog was wrong or this account is on an older API generation.
- Did `tk`/`dk` (initial snapshot) arrive once, followed by `tf`/`df` (incremental updates)? That's the acknowledgement-then-updates shape the plan's ingestion design assumes (§4.3).
- Do the touchline fields (`lp`, `v`, `oi`, bid/ask arrays) look sane for a real option?
- Any unexpected message types, or gaps longer than the 25s heartbeat interval?
- Did the token-exchange call succeed as JSON with a `"status"` field in the response? That's the one part of this flow confirmed against docs but never exercised against a real account until now.

Findings here should get folded into the real `NiftySignal.Ingestion` design in Phase 1, not left only in this spike.
