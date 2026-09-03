# FlatTrade Spike

Pre-Phase-1 validation tool (plan `docs/PLAN.md` §15.5) — proves the FlatTrade auth flow and WebSocket message format hands-on before the real ingestion pipeline is built around assumptions from the docs. Throwaway by design: not part of the module graph, no production code depends on it.

## Setup

1. On FlatTrade's Wall dashboard: Pi > api(v2) > Create New API Key. Note the API key and API secret.
2. Create `appsettings.Local.json` next to this README (gitignored, never commit it):

   ```json
   { "UserId": "<flattrade client id>", "ApiKey": "<pi connect api key>", "ApiSecret": "<pi connect api secret>" }
   ```

## Run

```bash
dotnet run --project spikes/FlatTradeSpike -- 300
```

The trailing number is how many seconds to log ticks for (defaults to 300 = 5 minutes, matching the plan).

The tool will:

1. Print an authorize URL — open it in a real browser and log in.
2. Wait for you to paste back the redirect (the full URL, or just the `request_code` value).
3. Exchange that code for a session token.
4. Search NFO for a live NIFTY option and subscribe to its touchline + depth feed.
5. Log every raw WebSocket message (with local receive timestamp) to `spikes/FlatTradeSpike/output/ticks-<timestamp>.log` for the configured duration.
6. Print a summary of message counts by type (`ck`, `tk`, `tf`, `dk`, `df`, ...).

## What to check afterward

- Did `tk`/`dk` (initial snapshot) arrive once, followed by `tf`/`df` (incremental updates)? That's the acknowledgement-then-updates shape the plan's ingestion design assumes (§4.3).
- Do the touchline fields (`lp`, `v`, `oi`, bid/ask arrays) look sane for a real option?
- Any unexpected message types, or gaps longer than the ~45s heartbeat interval?
- Did the token-exchange call succeed as JSON, or did it need real `application/x-www-form-urlencoded` fields instead? See the comment in `FlatTradeRestClient.ExchangeRequestCodeForTokenAsync` — that's the one part of this flow that was never actually exercised in the reference code it's based on.

Findings here should get folded into the real `NiftySignal.Ingestion` design in Phase 1, not left only in this spike.
