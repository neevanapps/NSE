# Running Market Commentary – removal (branch `feature/market-summary-paper-engine-v2`)

Not deployed. The commentary feature (08-Oct plan sections 44-69) is removed from generation, persistence, display, notification and screenshot scheduling. Nothing historical is deleted.

## Removed
* **Generation:** `CommentaryEvaluator`, `CommentaryRenderer`, `CommentaryNotificationPolicy` (NiftySignal.AdaptiveObserver), `AdaptiveCommentaryService`, `AdaptiveCommentaryFrameLoader`, `AdaptiveCommentaryNotificationGate` (Host). The observer worker no longer calls commentary per bar or at start-up; Host DI no longer registers it.
* **Persistence:** no new rows are written to `adaptive_commentary_events`, `adaptive_commentary_runtime`, `adaptive_commentary_notification_jobs` or the commentary `adaptive_projection_health` markers.
* **Dashboard:** `CommentaryPanel` (Home), `AdaptiveCommentaryDataService`, the commentary toast (`ToastKind.Commentary`), the commentary part of the RECENT EVENTS strip and the "commentary degraded" data-quality item.
* **Notifications:** `AdaptiveCommentaryNotificationProcessor` / `Worker` (outbox delivery), `AdaptiveCommentaryTelegramOptions` (`AdaptiveCommentaryTelegram:*` configuration is now ignored).
* **Screenshots:** the commentary Confirmed/Flipped event trigger of `AdaptiveScreenshotSchedule`. The normal completed-bar schedule (first bar, then every fifth) and the NPT1 paper trade open/close event captures are unchanged.

## Kept
* **History (no DDL, no deletes, no migration):** the commentary tables, their EF entity classes (`AdaptiveCommentaryEventRow`, `...RuntimeRow`, `...NotificationJobRow`), the enums in `CommentaryModels.cs`, and every existing row. A pinned capture and the Dashboard ignore them (a test seeds historical rows and proves they are neither shown nor deleted and trigger no capture).
* **`TelegramTextSender` / `ITelegramTextSender`** (acknowledged text delivery) stay in NiftySignal.Notifications, with their tests moved to `TelegramTextSenderTests`, for the paper-trade alerts.
* **Paper trading and its alerts are untouched:** the dashboard TRADE OPENED / TRADE CLOSED toasts (durable position identity, per-source baseline so refresh/reconnect never replays) and the NPT1 ledger-event screenshots do not depend on commentary and keep working.

## The paper hand-off gate (NPT1) - explicit note
`AdaptiveObserverWorker` handed a bar to the paper path as verified only when its own sidecars were verified AND it lay before `CommentaryCapBeforeBar`. That cap was derived ONLY from the two sidecar-integrity sets (live unverified packages, recovery-found unverified bars) - never from commentary state - so it is a paper-path fail-closed rule, not a commentary one. It is **preserved exactly** (renamed `PaperVerifiedCapBeforeBar`, condition extracted to `IsBarVerifiedForPaper`); a randomized test proves the new function equals the old formula for every scenario. The approved instruction ("drop the commentary cap, keep only the sidecar-verified gate") is therefore implemented as: commentary no longer takes part, the sidecar-integrity gate is the only input, and no NPT1 behaviour changes. If a looser rule (each bar judged only by its own sidecar) is wanted, that is a separate change to an existing trading contract and needs an explicit decision.

## Pending (not part of this removal)
New paper-alert types (Telegram trade opened/closed, order rejected, partial fill, execution failure) with a durable outbox are not built yet: they need new persistence and are scheduled with the Paper Engine V2 migration set.
