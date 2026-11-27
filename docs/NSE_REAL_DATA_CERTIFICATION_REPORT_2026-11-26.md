# Real-data certification report, 26 November 2026

Directive: `NSE_AUTONOMOUS_REAL_DATA_CERTIFICATION.md`. **Result: no real-data certification was performed. Paper release: NO-GO. Real money: NO-GO.**

## Blocker

The first step, a non-interactive read-only login check on `niftysignal_vm_copy` as `research_ro` (`psql -w`, `default_transaction_read_only=on`), was refused by this session's tool permission control, although the owner's directive authorises it (and says that authorisation does not override tool-level controls). It was requested once through the normal mechanism and not retried, and no other tool was used to read that database. Whether `pgpass.conf` permits the login was therefore not checked; the file was not opened. Every step that reads recorded data depends on this (readiness blocker B2).

## Done

| Step | Result |
|---|---|
| Scratch folders `D:\perf-scratch`, `D:\perf-scratch\out` | created |
| Scratch cluster `D:\perf-scratch\pg` | initialised (PostgreSQL 18.4), started, verified from its log and `postmaster.pid`: port 54340, that data directory, listening on 127.0.0.1 only; then **stopped**. No database was created. No command targeted the port-5432 server other than the refused login |
| Baseline worktree `D:\perf-scratch\nse-78d9b8b` | detached at `78d9b8bd84d0b9bb38dd91a80ae89f0dad8fd5fe`, clean |
| Release builds of `AdaptiveLivePerformance` | A: 0 errors, 5 warnings. B / C: 0 errors, 2 warnings |
| Candidate source identity (temporary Git index) | without `docs`: `25e7f9643f206881b7fc84145e26c198f555c9a7`, **equal to the verified release candidate**. With `docs` at that moment: `ccc371025cfd88008e0a12cdeb063ea020b67f24` (changes with every document edit). Normal staging area untouched |
| Owner run sheet and coverage query | `NSE_CERTIFICATION_RUNSHEET_2026-11-23.md`, `D:\perf-scratch\coverage-20261123.sql` (written from the repository's mappings, **never executed**) |

## Requested report items

| Item | Status |
|---|---|
| Machine and database environment | owner-reported, not re-verified: C 340.49 GB and D 167.45 GB free; 31.78 GB RAM (15.87 available); database 87 GB, about 290,626,048 ticks, 4 September to 23 November 2026 |
| Session dates | 23 November chosen by the directive; busiest and typical sessions **not identified** |
| Futures and options depth coverage | owner-reported for all ticks of 23 November: 3,390,839 of 3,965,994 with positive fifth level (85.50 %). **Per instrument: not verified** |
| Certify at 14:30 and 09:45; benchmarks A, B, C; cold and warm recovery; L1 / Top-5, sidecar, attestation and publication parity; tick accounting; query timings; memory; checkpoint timings; strategy replay | **BLOCKED**, none run |
| Recovery under 300 s | **NOT CERTIFIED** |
| Code changes in this pass | none |
| Evidence | `D:\perf-scratch\out\` (`initdb.log`, `pgstart.log`, `build-A.log`, `build-BC.log`), `D:\perf-scratch\pg.log` |
| Elapsed | from the directive at 20:59 IST to this report at 21:06 IST; certification time none |
| VM-specific validation (shadow latency, Windows service operation) | not possible while the VM is off |

## Next

Run the run sheet in your own PowerShell, steps 1-4 first (their duration is unmeasured). Activation depends on both certify runs printing `CRITERIA MET` and on the remaining pass conditions of the procedure. Deployment must start before 08:30 IST on Friday or move after 15:45.
