# Backtest Rules

These rules apply to **every research experiment and trade simulation** in this project — read
this file before starting any backtest, entry/exit experiment, or trade simulation, and apply it
alongside `CLAUDE.md`'s quant/scoring principles (that section covers scoring-formula discipline;
this file covers the mechanics of running a backtest itself).

1. **Define the hypothesis before running the test.**
   Clearly state what is being tested, why it may work, what should improve, and how success/failure will be measured.

2. **Change only one logical concept at a time.**
   Do not simultaneously change signal, candle size, lookback, entry, strike selection, exit, or filters. Keep other rules frozen so the reason for any change in results is identifiable.

3. **Prevent all look-ahead bias.**
   Every signal, strike selection, entry, exit and calculation must use only information available at that exact timestamp. Never use future ticks, future ATM strikes, MFE/MAE, or eventual outcomes to make a decision.

4. **Use the exact same option contract when comparing prices through time.**
   Once an option token is selected for a calculation, use that same token throughout the required historical window. Never splice changing ATM contracts into one artificial series.

5. **Enforce sufficient-history and data-quality rules.**
   Do not generate signals during warm-up or when required bars/quotes are missing, invalid or stale. Never fabricate data or silently substitute another contract.

6. **Separate the research layers.**
   Always distinguish between:
   * underlying directional signal quality,
   * option-price translation,
   * entry quality,
   * exit lifecycle,
   * execution and transaction costs.

   A winning or losing trade alone does not prove the underlying signal is good or bad.

7. **Evaluate the signal before evaluating P&L.**
   First measure forward Nifty/Futures direction, movement magnitude and matched-control separation. Then examine option behavior and finally actual trade P&L.

8. **Use realistic option selection in trade simulations.**
   For actual BUY trade simulations, select the eligible CE/PE whose entry premium is in the **₹100–₹150 range** using only information available at entry. Do not automatically trade ATM if it falls outside this range. If no valid contract exists, record the trade as unavailable rather than inventing a fallback.

9. **Use realistic execution.**
   Use actual available entry/exit quotes, frozen bid/ask/LTP fallback rules, lot size, transaction costs, cutoff time, mandatory close and position-management rules. Keep diagnostic MTM results separate from executable P&L.

10. **Prefer fewer, higher-quality trades.**
    More trades are not automatically better. Prefer architectures that naturally remove noise, repeated entries and marginal signals while preserving the underlying statistical edge. Always report trades/day and investigate excessive trading.

11. **Target favorable MFE/MAE characteristics.**
    A good entry should generally produce **higher MFE and lower MAE**. Track MFE, MAE, time-to-MFE and MFE giveback for every strategy. Do not use hindsight MFE/MAE directly as an entry or exit rule unless a separate forward-valid hypothesis is defined.

12. **Treat DTE as a real-time trading condition.**
    Always report results by DTE and think like a live option buyer. Consider how remaining time, theta decay, volatility, option premium, Greeks and expiry-day behavior can change option translation. Do not blindly pool very different DTE environments.

13. **Use appropriate controls and session-level validation.**
    Compare signals with direction-held and movement-magnitude-matched controls wherever applicable. Treat each calendar trading session as an independence unit and report session-by-session results, not pooled totals alone.

14. **Separate same-entry effects from opportunity-set effects.**
    When testing a new exit, first apply it to the exact same historical entries. Only then run the complete sequential simulation. This separates the pure exit improvement from changes caused by earlier capital availability and additional trades.

15. **Protect validation and OOS integrity.**
    Keep Design, Validation and OOS sessions separate. Once OOS data is viewed, it is permanently consumed and must never be reused to tune the modified strategy. Accept negative experiments without repeatedly changing parameters until historical P&L becomes positive.

## Notes specific to this codebase (2026-09-25)

- Rule 8 (₹100–₹150 selection) is the *default* for real trade simulations, but several completed
  experiments in `docs/VolumeCandle_0DTE_Findings.md` deliberately used ATM execution instead and
  said so explicitly (e.g. the frozen `13K_180S_FULLSURFACE_V1` baseline) after the ₹100–₹150
  band was found to perform worse at that architecture. When ATM execution is used instead of the
  ₹100–₹150 band, state that choice and the reason for it up front, per Rule 2.
- Rule 15's frozen strategy version and forward-validation framework are implemented in
  `NiftySignal.VolumeBarData/ForwardValidationRunner.cs` (see its own doc comment for why this
  logic lives in its own class rather than inline in `Program.cs`).
