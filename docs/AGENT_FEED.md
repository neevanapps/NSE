# Agent feed ("agent-v1")

A read-only, machine-readable view of the adaptive fixed-volume bars for an external agent. It lives in the Dashboard process only; the Host, the trading engine and every existing Dashboard page/calculation are untouched.

## API

`GET /api/agent/snapshot?bars=N` on the Dashboard host (default `http://localhost:5210`).

| | |
|---|---|
| `bars` | integer 1–300, default 20. Anything else (0, 301, `abc`, `1.5`) is `400` — never silently clamped. |
| Method | `GET` only (`405` otherwise). |
| Response | `200 application/json`, `Cache-Control: no-store`. `meta` plus `bars` (oldest first). |
| Bars | **Completed bars only.** The range is bounded by the runtime's `LastCompletedBarSeq`; the provisional bar (live partial volume) is never read. |

### Access control

* The check uses the TCP peer address (`HttpContext.Connection.RemoteIpAddress`; IPv4-mapped IPv6 is unwrapped). Only `127.0.0.1` / `::1` pass. Tailscale (`100.x`), LAN and public callers get `403`.
* Any `Forwarded`, `X-Forwarded-For/Host/Proto` or `X-Real-IP` header → `403` (a proxied request's peer is the proxy, so it cannot be trusted).
* The `Host` header must be `localhost`, `127.0.0.1` or `[::1]` (blocks DNS rebinding from a browser on the same machine).
* **Interaction with the Dashboard login:** the cookie login is enforced per page (`[Authorize]` + `AuthorizeRouteView`); there is no global fallback policy. The endpoint is mapped `AllowAnonymous()`, so it neither needs nor reads the login cookie and never sets one. The loopback check *is* its access control. The payload contains no credential or token.

## Payload

Raw values: MicroDev is **not** inverted (the Dashboard's "(Inv)" is display only); Delta/Vega are **not** ×100. Units are in field names (`ivPoints`, `vegaPer1Vol`, `…Sec`, `…Pct`). A value that is missing is JSON `null`, never `0`.

* `meta`: `schema` (`agent-v1`), `buildSha`, `sessionSource`, `clock` (fixed volume, `baseBarVolume` 13650), `expiry`, `selectedStrike`, `centerStrike` (of the latest bar), `latestCompletedBarSeq`, `barsReturned`, `dataOk`, `dataIssues`, `notes`.
* per bar: `barSeq`, `startIst`, `endIst`, `isCompleted`, `durationSec`, `dataOk`, `dataIssues`.
* `futures`: `price`, `priceDelta`, `rollDeltaPx`, `volume`, `strictDelta`, `enrichedDelta`, `microDevRaw`, `ofi`, `rollOiDelta`, `basisDelta`, `rollEfficiency`, `state`, `evolution`.
* `options` (fixed strike = the strike nearest that bar's own completed centre, same rule as the pinned Telegram view): `ce` / `pe` with `price`, `bid`, `ask`, `delta`, `vegaPer1Vol`, `ivPoints`, `strictDelta`, `enrichedDelta`, `oiChange`, `microDevRaw`, `ofi`, `activityPerSec` (+ start/change/age fields), and `dir` — the Dir columns as `{ce, pe, dir}` each `{raw, ratioPct}`. Direction is CE − PE for every metric except OI (PE − CE), as on the Dashboard.
* `residual` (band variant `AtmPlusMinus2`): `ceResPct`, `peResPct`, `directionalResPct`, `straddleResPct`, `direction`, `relation`, `quoteAgeSec`.
* `market` at the bar boundary: `spot`, `spotAgeSec`, `vix`, `vixChange`, `vixAgeSec`.

`dataOk` is false (with the reason in `dataIssues`) when futures microstructure, basis (missing or not usable), the fixed-strike CE/PE row, the post-09:30 residual, spot (missing or older than 5 s) or VIX (missing or older than 60 s) is missing or stale for that bar. `meta.dataOk` is the AND over the returned bars.

### Known gaps (not fabricated)

* Option **bid/ask** are not part of the stored per-bar option row. They are the top of book of the last tick at or before the bar boundary and are `null` when that tick carries no depth.
* No futures OHLC / trade-update count is exposed (not requested).
* The strike is per bar; for older rows it can differ from the single current strike shown in the Dashboard grid.

## JSONL file

On every completed bar the Dashboard appends one line — exactly one bar object of the API — to `<AgentFeed:RootDirectory>/agent-feed/yyyy-MM-dd.jsonl` (trade date, IST).

```json
"AgentFeed": { "JsonlEnabled": true, "RootDirectory": "data", "PollSeconds": 3, "SettleSeconds": 5 }
```

A relative `RootDirectory` resolves against the Dashboard install folder. Writer guarantees: only bars ≤ `LastCompletedBarSeq`; idempotent on `barSeq` per file (existing barSeq values are read under an exclusive write lock, so restarts, reconnects and a second process never duplicate; first line wins); a restart back-fills missing bars at the end without touching existing lines; a torn last line is left behind a newline and its bar rewritten; the newest bar waits `SettleSeconds` so the Host's sidecar rows are in place. Set `JsonlEnabled=false` to turn the writer off (the API stays).
