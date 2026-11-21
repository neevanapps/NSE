# A4 — positive per-strike verification attestation

Replaces the session "fence" experiment. Rule for every reader: **a current-version (`option-strike-v3`) per-strike row is served only if a durable attestation proves it was verified; absence of proof means unverified.**

## Identity
Table `adaptive_strike_attestation` (additive): unique `(SessionId, BarSeq, MetricsVersion)`, columns `TokenCount`, `Digests` (`token=hash;token=hash`, sorted), `VerifiedAtUtc`.
* The attested **contract set** = the tokens of the bar's verified rows. A row whose token is not in the set is unverified.
* `hash` = first 16 hex chars of SHA-256 over every value column of the stored row (all columns except `Id` and `EndAvailableAtUtc`, doubles round-trip format). Readers recompute it from the row they are about to serve, so a row changed after attestation is withheld **without any write**.
* Legacy versions (`option-strike-v1/v2`) are never written or re-verified by the current Host; they stay governed by the existing UNVERIFIED markers only (listed as a residual).

## Host write ordering (`PersistOrVerifyStrikesAsync`)
1. **No stored rows for the bar:** rows + attestation are added and committed in **one SaveChanges** (atomic). Failure -> nothing durable, outcome `Failed`, bar queued for retry.
2. **Stored rows exist:** compare with the recomputed rows.
   * equal -> upsert the attestation from the **stored** rows' digests (one SaveChanges). Attestation write fails -> `Failed`, queued (rows stay unattested = withheld).
   * different -> **one SaveChanges**: union the UNVERIFIED marker **and** remove the bar's attestation. Fails -> `Failed`, queued. Original rows are never touched.
3. The strike queue (cap 200) and the close-ATM queue are separate. Overflow of the strike queue only means attestation arrives at the next restart replay; unattested rows are already withheld, so nothing needs fencing.

## Reader ordering (`UnverifiedStrikeBars.LoadAsync`, used by fixed-strike history, day average, pinned capture, summary, agent API and V2 `DbFrameInputSource`)
Read markers + attestations for the session (bars <= requested), then per row: marker -> withheld; else current version needs attestation with matching token + hash. Either table missing/unreadable -> everything of the current version withheld (`TablePending`).

## Migration / rollout
* `20261121...AddStrikeAttestation` + `Sql/adaptive-strike-attestation.idempotent.sql`: create table only. No data change, no backfill SQL.
* Order: apply migration -> deploy Host (restart replay verifies and attests the whole current session) -> Dashboard. Until the replay finishes, current-day rows without attestation are withheld (V2 does not run during replay: bar notices are produced only by the live loop, after recovery).
* **Historical rows are not attested and are never silently trusted.** Attestation is created only by a Host replay comparison (or the atomic live insert). Past days stay withheld until a Host replay of that session attests them.

## Remaining ways an unverified row could be served
* A row attested at write time (digest matches) that a later replay finds disagreeing with recomputation, while the marker+revoke write also fails: the row is served until a retry/restart succeeds. The DB cannot accept the required state; no reader-only fix exists.
* Legacy-version (v1/v2) rows (no attestation concept).
* Hash collision (64-bit) or an edit that keeps every value column identical.
