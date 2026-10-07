# Configurable market data: FlatTrade / Upstox

Implementation branch: `feature/configurable-upstox-market-data`, based on master
`4d2bf93673926bf786effc9808c494b06ae4eb66`. This integration collects market data only.
The adaptive Host remains watch-only; no real order API is added or enabled.

## Select the provider

In **both** deployed Host and Dashboard `appsettings.Local.json`, merge
`"MarketData": { "Provider": "Upstox" }`. Add the OAuth credentials in **Dashboard only**:

```json
{
  "MarketData": { "Provider": "Upstox" },
  "Upstox": {
    "ApiKey": "YOUR_API_KEY",
    "ApiSecret": "YOUR_API_SECRET",
    "RedirectUri": "EXACT_REDIRECT_URI_REGISTERED_IN_UPSTOX"
  }
}
```

Keep existing connection strings and other local settings. Never commit local settings.
`FlatTrade` is still the default when the section is absent. Both providers retain
independent saved credentials. Host/Dashboard selection is captured at startup, so
restart both services when changing it. Changing config does not hot-switch an active socket.

The API key and secret are used by **Dashboard** for code exchange; the market-data Host
needs only the provider setting and consumes the saved token. Existing registered static IP is relevant to eventual trading,
but this integration does not place orders.

## Authenticate

Start the updated Host to apply the migration, then the Dashboard. The Upstox panel on Home
and Legacy offers two alternatives:

1. **API key/secret:** open Upstox login, complete authentication, paste the final redirect
   URL or authorization code, then click Exchange & Save. `RedirectUri` must exactly match
   the app registration. Token expiry is 03:30 IST; the code is single-use. The token is
   validated against a read-only market quote before being saved.
2. **Analytics Token:** generate the read-only token on Upstox Developer Apps, paste it
   and the displayed expiry date, then Validate & Save. This is preferable for unattended
   market-data collection. The date is treated conservatively as midnight IST at the start
   of that date. Actual rejection/revocation can happen earlier and is surfaced as a feed outage.

Tokens are stored in the operational database, following the existing FlatTrade session
pattern. API secrets stay in local configuration. Credential input is masked; neither tokens,
response bodies nor authorized WebSocket URLs are logged by the new adapter. Restrict database
and local-config access to the service administrators, as with the existing setup.

Host with missing credentials alerts and retries every 30 seconds; saving a valid token
resumes startup. Replacing/revoking an already active token requires a Host restart.
Never paste credentials into chat, GitHub, terminal command arguments, or source files.

## Identity and switching rules

- Existing FlatTrade instrument/tick tokens stay unchanged; migrated rows default to FlatTrade.
- Upstox uses internal `UP:<instrument_key>` identifiers and separately stores the native
  subscription key. Only the native key is sent to Upstox. Contract descriptors retain exchange,
  underlying, expiry, strike, CE/PE, lot size and tick size; active trade contracts do not roll.
- `market_data_days` locks the provider for an IST trading date. Existing instrument/raw history
  is checked before adopting an older day. A different provider for that day is rejected, even
  after a restart. Select a different provider on a fresh trading day; do not delete the lock
  or relabel old data to bypass it.
- No automatic fallback between providers. Reconnection stays within the selected provider.
- Today's persisted universe is reused across restart. Missing secondary underlying groups
  can be resolved separately without replacing existing descriptors.

## Feed policy

Official V3 `full` subscriptions use binary JSON requests and Protobuf responses, with five-level
depth. Incoming cumulative `vtt` is stored in contracts, without multiplying by lot size.
Index feeds have no fabricated volume, OI or depth. Partial depth is unavailable rather than padded.
The JSON master tick size is converted from paise to rupees (5.0 -> 0.05).

`ExchangeTimestamp` holds Upstox **message** `currentTs` for update ordering; this is provider time,
not a guaranteed exchange-origin timestamp. `LastTradeTimestamp` separately preserves `ltpc.ltt`.
`ReceivedAt` records local UTC receipt, and the adaptive causal-availability clock remains
max(message, receipt). A quote update therefore does not masquerade as having occurred at an
older last-trade time. Frames are mapped in stable instrument-key order.

The first full payload for each instrument on each connection is persisted with `IsSnapshot=true`.
The collector uses bounded backpressure, fragmented-message assembly, serialized WebSocket sends,
re-authorization on reconnect, subscription replay and outage recording. Current standard full-feed
limit is enforced at 2,000 keys. Subscription requests remain queued requests, not broker acknowledgements.

**V1 outage restriction:** raw collection reconnects, but adaptive observations stop for that day
when an in-market Upstox outage is recorded or an in-market snapshot follows an earlier connection's snapshot.
This guard applies to both live reads and replay. It preserves source evidence without assigning
missed cumulative volume to invented trades. Reconnects completed before market open share one
pre-open baseline and do not block the day's observations. Resuming calculations after an in-market outage would
need explicit baseline/window recovery and independent replay validation; it is deferred.
Legacy volume-bar projections are retained for compatibility and are not evidence of Upstox signal
parity. Legacy paper executors remain unhosted.

## Read-only account acceptance

On the machine/account that will collect data:

```powershell
dotnet run --project tools/UpstoxMarketDataSmoke --configuration Release
```

Paste the access token into the hidden interactive prompt. The two-minute probe performs only
public catalogue download, read-only quotes, WebSocket authorization and streaming; it writes no
DB rows and places no orders. It prints contract counts and first two updates per instrument,
with price, cumulative volume, OI, depth, snapshot/message/trade timestamps. Run during market hours.
It reports total updates and instrument coverage; zero received instruments fails the probe.
The probe deliberately suppresses HTTP/log output that could expose credentials.

Before selecting Upstox as primary, verify:

- NIFTY spot, India VIX, nearest future and two option expiries resolve to intended contracts.
- Quotes/premiums, lot/tick sizes, volume units and five-level depth match the broker UI.
- Futures and CE/PE update counts/freshness remain adequate during active trading.
- Induced disconnect reconnects raw collection, retains subscriptions, records a gap and pauses
  adaptive observations visibly. Intraday restart preserves provider/universe and does not silently
  recompute the frozen adaptive session with a different source.
- Fresh-day provider switch succeeds; same-day switch is rejected; returning to FlatTrade on a
  fresh day preserves its login/feed behavior.
- Isolated recordings are compared across providers for bar timing, trade-update count,
  volume/second, flow coverage and residual observations. Existing research edge is not assumed
  to transfer from one broker feed to another.

Build/unit tests establish implementation behavior, not authenticated broker quality or a trading edge.
VM migration/deployment and authenticated acceptance are separate checks and are not automatic.

## Schema and rollout

`AddMarketDataProviders` adds provider metadata to instruments/ticks, native keys, snapshot/trade
clock columns, `upstox_session`, and `market_data_days`. Generated migration and model snapshot are
included. Existing rows retain FlatTrade semantics. Apply to a disposable PostgreSQL copy first,
then deploy outside market hours. Host self-migrates at startup; Dashboard must not run against
an old schema. Take an operational DB backup before deployment as usual.
DataSync destinations must also have the updated schema before copying the new provenance fields.

Rollback selection: set `MarketData.Provider` to `FlatTrade` in both local configs and restart
on a fresh trading day. Do not roll back the migration after Upstox data has been collected:
its Down method drops provenance columns/tables. Old data is never rewritten by provider switching.

## Verification checkpoint — 2026-10-07

- Full Debug solution build: zero warnings and zero errors.
- Full test suite: 1,176 passed, zero failed or skipped, including provider locks, credentials,
  redirect decoding, feed mapping, pre-open/in-market continuity and migration-model consistency.
- Idempotent PostgreSQL migration SQL generated successfully, including the partial snapshot index.
- C# catalogue parser exercised against the public complete JSON asset: 5,860 tracked descriptors,
  including spot/VIX, futures and options for the three tracked indices. Sample paired option-band
  selection and lot/tick-size mapping passed; these catalogue checks use no account credentials.
- Not yet exercised: actual PostgreSQL migration application, authenticated account streaming,
  browser login interaction, VM deployment or real disconnect/restart acceptance.

## Official references

- https://upstox.com/developer/api-documentation/v3/get-market-data-feed/
- https://upstox.com/developer/api-documentation/get-market-data-feed-authorize-v3/
- https://assets.upstox.com/feed/market-data-feed/v3/MarketDataFeed.proto
- https://upstox.com/developer/api-documentation/instruments/
- https://upstox.com/developer/api-documentation/get-token/
- https://upstox.com/developer/api-documentation/analytics-token/
