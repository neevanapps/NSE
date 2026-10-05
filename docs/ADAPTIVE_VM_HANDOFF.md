# Adaptive observer V1 — manual VM release

The user deploys both services using the existing `deploy.ps1`. Do not deploy a feature-branch build. The release is watch-only; the Host no longer starts the two legacy paper executors. Existing paper records and `/legacy` are retained.

## Before deployment

1. Use the merge SHA reported by the completed release handoff. Confirm its **Adaptive Observer Validation** workflow passed, including publishing both applications.
2. Back up the VM database and existing installed binaries/configuration using the usual operational procedure. The new observer uses `niftysignal_adaptive_observer` on the server from `ConnectionStrings:NiftySignalDb`; it does not replace the raw tick or legacy volume-bar databases. The configured PostgreSQL role must be able to create/migrate that database.
3. Keep VM `appsettings.Local.json` intact. The script excludes the desktop copy; do not copy desktop credentials/connection strings onto the VM.
4. Verify raw NIFTY futures source history exists for all frozen discovery sessions: 2026-09-08, 09, 10, 11, 15, 16, 17, 18, 21, 22, 23, 24, 25. Bootstrap now fails closed if mandatory history is missing. These dates are discovery in this experiment's frozen reference; no sealed research dates were used. Complete replay source and instrument descriptors must remain available for every persisted live session that may need recovery.
5. Deploy outside market hours, with the desktop Host stopped, so only the VM owns the FlatTrade feed session. Ensure .NET 10 runtime, Tailscale/WinRM and your existing deployment credentials are available.

## Run from a clean desktop checkout

Replace the placeholder with the 40-character merge SHA from the release handoff:

```powershell
git fetch origin
git switch master
git pull --ff-only origin master
$releaseSha = '<VERIFIED_MASTER_SHA>'
if ((git rev-parse HEAD).Trim() -ne $releaseSha) { throw 'Checkout differs from reviewed release SHA.' }
.\deploy.ps1 -Target Vm -Service Both -ExpectedCommitSha $releaseSha
```

The script verifies the clean source checkout and SHA, creates an immutable Git archive, runs tests, publishes with branch/SHA/build UTC, preserves VM local settings, and uses the registered service installation directories. Keep tests enabled for the release. If master advances, the mismatch deliberately stops deployment; do not substitute a different SHA without verifying its release gate.

## Verify after deployment

The screenshot release additionally requires Dashboard-local Telegram settings, capture Chromium and the outbox migration. Follow [ADAPTIVE_TELEGRAM_SCREENSHOTS.md](ADAPTIVE_TELEGRAM_SCREENSHOTS.md), including **Send pre-live test** before the 09:30 initialization. Host's bot settings alone do not enable Dashboard delivery.

- Both `NiftySignalHost` and `NiftySignalDashboard` services are running. Check their installed binary timestamps and startup logs; confirm `master`, the exact release SHA and build UTC in the Dashboard. An existing immutable daily session may correctly retain the SHA that originally created it; compare the current **runtime build** metadata too.
- Host migration/startup succeeds without permission errors. Check logs for missing discovery history, source-history truncation or persisted-row reconciliation errors. Do not delete adaptive rows to conceal a failed recovery.
- Access-token controls and live quotes work with the VM broker session. Before 09:30 the observer waits; after 09:30 it initializes the frozen session and then consumes persisted raw ticks.
- All three grids show the same completed sequence. Check 5/10/15, BOTH/CE/PE, NOTIONAL/CONTRACT and ATM±2/ATM. Missing option data remains explicitly unavailable. The current incomplete bar stays outside completed grids.
- Perform one controlled Host restart outside an active market first; compare daily config, completed sequences and persisted observation identities before/after. Later perform the agreed live restart observation without inventing a second feed owner.
- CI demonstrated sub-second database-commit-to-painted-grid timing. Measure this again on the VM under actual load; the two-second source ordering lag is separate from the Dashboard commit latency target.
- Begin watch-only prospective observation only after these checks. No paper orders, real orders or new trading filter is enabled by this release.

VM deployment, runtime restart and prospective results are **pending manual validation**. Record date, deployed SHA, service/log checks and measured VM latency in the roadmap after performing them.
