# NPT1 M1 producer — implementation plan (checkpoint before the strategy rules)

Branch `feature/npt1-paper-three-live-v1` from master `4a0c9ff5a16b020bdef1127357a569574ab0cd99`. Decision (user): the three NPT1 strategies run on the research **M1** clock, produced by a NEW isolated parallel producer; the production Adaptive Observer (`adaptive-v1`) stays byte-for-byte unchanged; `NiftySignal.Trading` never builds bars.

## A. Research sources being ported (exact, not re-derived)
From `research/bar-scale` (same content on `research/bar-size-v2`):
* `NiftySignal.AdaptiveObserver/OpeningVolumeProjectionV2.cs` — `PriorSession`, `BarSizeDecision`, `OpeningVolumeProjectionV2.Compute/Resolve/Median` (M1 = beta 0.5, `TargetWindowBars=110`, `Lookback=20`, `MinPrior=5`, clamp [0.75, 1.33], `RoundingLots=1`, V1 fallback when history is insufficient). `ApplyScale` (the research `--bar-scale` experiment) is NOT ported: scale 1.0 is the identity and a scale knob must not exist in a live path.
* `NiftySignal.Tests/AdaptiveObserver/OpeningVolumeProjectionV2Tests.cs` — ported with the code (parity proof).
* `NiftySignal.Trading.Llm/VolumeHistory.cs` (`Compute`, `BaselineEligible`, `Eligible`) — the prior-session opening / window volume with the observer's own volume definition.
* `NiftySignal.Trading.Llm/Dataset.cs` `FreezeBuilder.Build` — the exact session freeze of the research export (09:15 / 09:30 filters, opening volume and coverage, `baseBarSizer`, 09:30 residual anchor, `StrongThreshold = null`, signal start 09:30).
* `tools/DayCsvExport/BarSizing.cs` — how the sizer is wired (`covered == 15`, `Eligible(history)`).
The M1 bars of the research are therefore: `AdaptiveObserverEngine` (unchanged, from master) over that freeze with the M1 base bar volume. The exports in `NSE-phase1/artifacts/exports/**/research_bars_M1.jsonl` are the reference for parity (authorized dates only, <= 2026-10-01).

## B. New projects (nothing existing is edited)
* `NiftySignal.Npt1M1` (library): M1 sizing port, volume history, freeze factory, `M1BarProducer` (pure, deterministic: ordered availability groups in, finalized `Npt1Frame` out), frame model.
* `NiftySignal.Npt1M1.Data` (EF Core, schema `npt1m1`): own tables and migration.
* Host: `NiftySignal.Host/Npt1M1/Npt1M1Worker` — a separate hosted worker (its own thread, loop and database work); registered only when configured.

## C. Raw tick source
The same authoritative raw tick table, through a read-only reader that mirrors `AdaptiveSourceTickReader.ReadRawAfterIdAsync` (own implementation in the new project; the existing class is not touched). Tokens: the M1 stream's own futures + weekly-option universe read from the persisted `adaptive_sessions` row of the day (read-only; no write, no dependency on its runtime state).

## D. Independent identity
Model/version `npt1-m1-v1`; session id `npt1-m1-v1:{trade date}`; completed-bar identity `(session id, BarSeq)` in its own tables; own runtime row and cursor. PaperLive run id `paper-live-npt1m1-v1:{trade date}:{session id}`. No key or table shared with `adaptive-v1`.

## E. Persistence (`npt1m1` schema in the trading database; own role grants)
`sessions` (frozen session + sizing provenance + hash), `volume_history` (prior-session opening/window volumes, append-only per date), `frames` (finalized immutable frames, hash-protected, one per `(session, bar_seq)`), `runtime` (heartbeat, last processed tick id, last completed bar, status). Reviewed idempotent SQL like the trading schema; not applied anywhere by this work.

## F. M1 calculation reuse
Ported code as in A; `M1BarProducer` computes the freeze at 09:30 + 5 s from the stored rows exactly as `FreezeBuilder.Build`, sizes with `OpeningVolumeProjectionV2.Resolve(... beta 0.5 ...)` over `VolumeHistory.Eligible(prior sessions)`, then runs the unchanged `AdaptiveObserverEngine`.

## G. Metrics over M1 bars
Because the engine is the unchanged `AdaptiveObserverEngine` over M1 boundaries, the packages carry the exact metrics: `Microstructure.MicroDevTimeWeighted`, `Microstructure.MicroDevUsableSeconds`, `OptionBand.Put.ContractStrictDelta`, `OptionBand.Selection.CenterStrike`, `OptionBand.Selection.ExpiryDate`, plus readiness from `BarReadiness`. Nothing is copied from adaptive-v1 bars.

## H. Frames into PaperLive
`Npt1Frame` (immutable, nullable-aware) is attached to the bar event (`BarCompletedEvent.Frame`), journaled in the canonical input payload (codec bump) and handed to `IStrategyFamily.OnBar`. The Npt1M1 worker is the sole feeder of the NPT1 PaperLive coordinator (quotes of the same availability groups + finalized M1 frames); the existing V1 bridge hooks are unchanged and stay off for NPT1 (a configuration check refuses running both).

## I. Restart / recovery
The producer is deterministic over the ordered raw ticks: after a restart it re-reads the ticks from 09:15, rebuilds the identical engine state and the identical frames (verified against the stored `frames` rows by hash); frames the Trading journal already has are skipped by the durable cursors; bars that completed while the Host was down are NOT replayed as decision input (a gap is declared, strategies re-warm); open positions and pending exits come from the Trading ledger.

## J. Expected overhead
A second read of the same tokens (about 2x tick reads, the same polling cadence) and a second `AdaptiveObserverEngine` in the Host process; DB writes: about one frame row + one runtime row per M1 bar and per second. It runs in its own worker so a slow database cannot delay the V1 poll loop; measured locally and reported.

## K. adaptive-v1 untouched
No file under `NiftySignal.AdaptiveObserver/`, `NiftySignal.AdaptiveObserverData/` or `NiftySignal.Host/Adaptive*.cs` is modified; proved at the end with `git diff 4a0c9ff --stat -- <those paths>` (empty) and the unchanged V1 tests.

## Blocker check
Implementing M1 does **not** require changing adaptive-v1 behaviour. Continuing.
