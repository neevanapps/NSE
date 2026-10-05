# Adaptive Dashboard → Telegram screenshots

## Contract

- Fresh feature branch `feature/adaptive-telegram-screenshots`, based on master `704bc1b50f128baa9e18d6fb5436aa5d072f32f8`.
- One full-resolution PNG document containing all three Dashboard grids (10 rows, BOTH / NOTIONAL / ATM±2), expanded horizontally to include every column. Original PNG avoids Telegram photo resizing. Operational login/token controls are not captured.
- First trigger is 09:30 IST once the nonhistorical adaptive session is initialized/recovered and runtime is Live. Explicit unavailable option diagnostics are valid output, not a reason to block all screenshots indefinitely.
- Initial boundary is the last completed adaptive bar at or before the immutable 09:30 cutoff (zero allowed). Subsequent triggers are boundary +5, +10, …; opening replay bars do not generate a burst of separate five-bar messages.
- Jobs are derived from committed state, including missed boundaries after restart. Capture is pinned to that session/sequence, never the latest bar at delayed delivery time. Completed-bar images exclude the unrecorded live partial bar; actual trigger/capture times and build SHA are shown.
- Persistent outbox identities and PostgreSQL connection-scoped worker lease prevent duplicate scheduling/concurrent uploads. Sent jobs never automatically resend. Safe capture failures and explicit Telegram rejections retry the same image. A crash/timeout after upload begins becomes **DeliveryUncertain**, not an automatic retry. Telegram acknowledgment and database commit cannot be made atomic; exactly-once delivery is not claimed.
- PNGs and hashes are retained under Dashboard `screenshots/yyyy-MM-dd`; nothing deletes historical images automatically. Monitor disk space.
- No changes to adaptive math, trading rules, feeds or paper/order execution. Screenshot work is in the Dashboard service, not the ingestion Host.

## VM configuration (local secrets only)

Merge only after build/tests/generated migration/real browser capture tests pass. Use the final master SHA reported in the release PR, not the original base above.

In the **VM Dashboard** `appsettings.Local.json`, merge these sections without replacing existing credentials/settings:

```json
{
  "AdaptiveScreenshots": {
    "Enabled": true,
    "BaseUrl": "http://localhost:5210",
    "OutputDirectory": "screenshots"
  },
  "Telegram": {
    "BotToken": "YOUR_EXISTING_BOT_TOKEN",
    "ChatId": "YOUR_EXISTING_CHAT_ID"
  }
}
```

Use the existing bot/chat already configured for Host; no new Telegram bot is required. Dashboard and Host have different local files: Host Telegram settings alone do not enable Dashboard delivery. Never commit these values or paste secrets into chat. BaseUrl must match the Dashboard's local listening origin and must be loopback. Existing DashboardAuth must be configured; the worker uses a short-lived locally protected authentication ticket, not your plaintext password. Restart Dashboard after changing options.

`deploy.ps1` retains VM local configuration, runs tests and publishes the exact expected master SHA. It now invokes `NiftySignal.Dashboard.dll --install-screenshot-browser` on the target before starting Dashboard. Chromium is installed under the application `.playwright-browsers` directory for service-account access, not under the deployer's profile. Browser installation failure leaves Dashboard stopped. Capture files/browser caches are excluded from remote deployment hashing and are not erased by deployment.

## Pre-live acceptance — morning of 2026-10-06 IST

1. Back up existing binaries/config/databases. Stop the desktop Host; deploy both services outside market hours using the existing script with the final `-ExpectedCommitSha`. Keep tests enabled.
2. Confirm Host and Dashboard are Running. Host startup log must identify `master` and the exact release SHA. Dashboard's **Dashboard build** must match; immutable **Session source** can legitimately remain older.
3. Confirm Host's automatic adaptive database migration applied `AddAdaptiveScreenshotOutbox`. In `niftysignal_adaptive_observer`, inspect `__EFMigrationsHistory` and `adaptive_screenshot_jobs`. Dashboard's database role needs read access to adaptive projections and INSERT/UPDATE on this notification table; it never writes strategy rows.
4. Verify complete mandatory discovery history using ADAPTIVE_VM_HANDOFF.md. Check migration/source/recovery logs, bot configuration and browser install; do not treat service Running alone as readiness.
5. Log into `/adaptive-screenshots`. Click **Send pre-live test** once; refresh until its row reads **Sent** with a Telegram message ID. Check Telegram receives the full PNG marked **PRE-LIVE DELIVERY TEST**, readable when zoomed. This tests browser/auth/DB write/upload before 09:30 without creating a market observation.
6. At 09:30 initialization, check exactly one Initialization job/image, date, cutoff boundary and three grids. After five new completed bars, check the first FiveBars job and matching pinned sequence. Check every column and both CE/PE values in the PNG; missing data stays unavailable.
7. Verify restart does not resend Sent jobs. Inspect Pending/DeliveryUncertain and Dashboard logs; for uncertain delivery, check Telegram manually before any one-row administrative reset to Pending. Do not bulk-reset or delete jobs.
8. Verify normal Dashboard latency while captures run and monitor browser memory/disk/Telegram failures. VM screenshot delivery and live timing remain **unvalidated until you perform these checks**.

## Implementation validation status

Implementation and focused scheduling/retry tests exist. Generated migration review, full regression, Chromium capture/upload emulator and exact merged-master gate are pending. Release evidence and final SHA will be recorded in the feature PR; no VM success is inferred from CI.

Official API references: https://core.telegram.org/bots/api#senddocument ; https://playwright.dev/dotnet/docs/browsers ; https://playwright.dev/dotnet/docs/screenshots .
