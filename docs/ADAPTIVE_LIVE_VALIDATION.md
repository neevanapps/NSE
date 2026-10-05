# Adaptive V1 release validation — 2026-10-05

Feature code checkpoint: `22592b07fb48f2c81a27e65bf407e7f78b44d9e3`.
Feature gate: https://github.com/neevanapps/NSE/actions/runs/37335634158 .
Final documentation checkpoint changes no passed production or test code. Exact master CI is mandatory before release handoff.

| Gate | Result |
|---|---|
| Complete Release solution | Passed, zero compiler warnings/errors; all root projects included |
| Tests | 48 adaptive; 1,123 total; zero skipped/failed |
| EF/PostgreSQL 17 | Both adaptive migrations applied; no pending model changes |
| PostgreSQL recovery | Seven process-state reconstructions; 24 bars; partial/Strong/Weak1/Weak2/H5/band-roll; completed corruption rejected without overwrite |
| Dashboard | 36 distinguishable mode combinations; synchronized sequences/counts; explicit unavailable rows; blocked-read selector race; reload defaults; legacy route |
| Latency | 20 commit-to-three-grids samples including two animation frames; P95 189.0 ms; max 224.4 ms; all below one second in CI |
| Publish | Host and Dashboard both published with source metadata; Linux-runner evidence, not Windows deployment binaries |
| Deployment script | PowerShell syntax, dirty-checkout and incorrect-SHA rejection passed |
| Frozen history | Private run 37330577623 at a43ca3c; original research e768b8e; all 13 discovery sessions passed |
| Core/threshold history | 2,034 complete futures bars; 3,905,218 checked fields |
| Options/source history | 235,344 independent band/rolling fields; 97,767 actual source/bootstrap persisted rolling fields |
| Residual/observation history | 3,641 residual rows / 40,051 fields; 125 executable overlapping H5 observations and one unavailable selection |
| Historical restart | 87 forced engine reconstructions with complete package/partial-state equality |

Historical price/OI/timing tolerances remain explicit; no sealed dates used. Comparison from a43ca3c to final feature code changes only workflow, Dashboard panel, browser fixture and docs, not historical-tested Host/domain/calculation/persistence code.

## Diff review

Reviewed feature scope against original master cf0decbe: 82 files before this report, isolated adaptive domain/schema/source/recovery/UI, focused tests and validation tools, build metadata, deployment guards and docs. Existing operational header/access-token/Live Quote components are retained unchanged on home. Legacy panels are preserved under /legacy. Existing migrations and source/tables are retained. Host starts only ingestion, legacy volume-bar persistence and adaptive observer; both legacy paper-executing workers are unhosted. No adaptive order or paper-execution path is introduced.

Audit findings F63–F71 are repaired with historical/unit/relational/browser evidence in REVIEW_FINDINGS.md. Broader original Dashboard design inventory (chart, separate observation list and quality panels) is not part of the frozen three-grid delivery and is not claimed complete.

## Manual release boundary

Merge SHA and its master workflow must be verified after merge; PR #2 records the final immutable SHA/run. User performs VM deployment via ADAPTIVE_VM_HANDOFF.md and existing deploy.ps1. VM DB permissions, required discovery/source history, services, broker login/live quotes, deployed SHA, controlled Windows-service restart, VM latency and prospective watch-only sessions remain **pending manual validation**. CI does not imply VM measurements or profitable prospective behavior.
