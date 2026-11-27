# Recovery comparison harnesses

Bench-only files. They are under `docs`, belong to no project and are not part of the certified source tree.

| File | Used for | SHA-256 |
|---|---|---|
| `RecoveryComparisonBenchmark.cs` | pooled run (readiness report 12.10) | `43E31BE070BF1EB427DCC670583B4F8742DEB17E8746D7BE6C32A9DE862DD7CA` |
| `RecoveryTimeoutBenchmark.cs` | pooled run (12.10) | `BA571AE03BC05DCC5C7BC9F66C6D6C1F25C53EBBD5F8EF3D1276804F65818B67` |
| `first-run/RecoveryComparisonBenchmark.cs` | first run, pooling off (12.7-12.9) | `70C26F238266F7973EB4A8DD6D896DFEB730EAD33D6F2AF47B63B712B5C5BC16` |
| `first-run/RecoveryTimeoutBenchmark.cs` | first run (12.7-12.9) | `903A87F97F3DA47887C8D3E7A14C406466C80C5C6C4F9F7502DB339A532F8576` |
| `tree-45008e3-from-aa906ab.patch` | difference between the benchmarked and the released source | `1ED5983E52D52261256B892280402C85D3A01594369CD047F2A03CC2369AF763` |

## Reproducing

`..\Run-RecoveryComparison.ps1` and `..\Run-OvernightCertification.ps1` are pinned to source tree `aa906ab302ce7e770d03c1c4002aa40e5a15808e` (without `docs`), which no commit contains. Rebuild it from commit `17ce4c79ab819ef4466ed90a8de59c338e040fcb` in a **separate throw-away clone**:

1. `git switch --detach 17ce4c79ab819ef4466ed90a8de59c338e040fcb`
2. Copy this folder outside the clone, then `git apply -R <copy>/tree-45008e3-from-aa906ab.patch`
3. The A1 command of the deployment checklist must print the tree above. It also stores the tree object the script exports.

Also needed, outside the repository: a worktree of `78d9b8bd` at `D:\perf-scratch\nse-78d9b8b`; a loopback scratch PostgreSQL 18 cluster at `D:\perf-scratch\pg` (port 54340); the recorded source database `niftysignal_vm_copy` read as `research_ro` (password from `pgpass.conf`); the repository path `D:\Projects\NSE-release-20261124` (edit `$Repo` for another location). The result files are in `D:\perf-scratch\out` and are not committed.

The script copies the two files in this folder, not those in `first-run`. To repeat the first run with its exact files, copy them over these in the throw-away clone.
