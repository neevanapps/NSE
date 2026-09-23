namespace NiftySignal.VolumeBarData;

/// <summary>
/// PROVISIONAL, RESEARCH-ONLY (Experiment 2, 2026-09-22 "Call/Put premium momentum" task -- see
/// docs/VOLUME_BAR_FINDINGS.md's dated section). Combines a Call-side and Put-side
/// <see cref="MaSpreadRelationshipAnalyzer.Sample"/> list (same Date/BarIndex join key the
/// ma-spread-research CLI's own Part 13 dual-state split already uses) into ONE per-bar
/// "Call-minus-Put" differenced signal: <c>CallSpread - PutSpread</c>.
///
/// Rationale for this exact formula (stated before use, per this project's "explain before
/// changing" discipline -- this is a NEW derived signal, not an existing formula, but the same
/// discipline applies): the prior SMA/EMA task (docs/VOLUME_BAR_FINDINGS.md, Section 6/E10)
/// established Call Spread correlates POSITIVELY with NiftyFwd and Put Spread correlates
/// NEGATIVELY, with Put consistently 1.5-2x Call's magnitude. Negating Put and adding it to Call
/// (equivalently, subtracting) points both legs' contributions in the same direction before
/// combining them, so a genuinely-related pair of legs should combine into a stronger, less noisy
/// directional read than either leg alone -- the same "both legs agree" reasoning the existing
/// Part 13 dual-state split (BullishConfirmation/BearishConfirmation) already uses, just as a
/// continuous signal instead of a thresholded state. This is an unweighted 1:1 difference, not a
/// magnitude-weighted combination (e.g. weighting Put 1.5-2x higher per its own larger
/// correlation) -- an equal-weight combination is the simplest, least assumption-laden starting
/// point, and any weighted variant would itself need its own justification and evaluation cycle
/// before being trusted (this project's metric-evaluation-process rule); not attempted here.
///
/// Pure function, no DB access, no look-ahead risk of its own: it only combines two same-bar
/// <c>Spread</c> readings that were themselves already computed from past-and-current prices only
/// by <see cref="MaSpreadEngine.Observe"/>.
/// </summary>
public static class CallPutDiffSignal
{
    public readonly record struct Row(
        DateOnly Date, DateTimeOffset Timestamp, int BarIndex, double CallSpread, double PutSpread, double Diff,
        double? NiftyFwd5, double? NiftyFwd10, double? NiftyFwd20);

    /// <summary>
    /// Inner-joins Call and Put samples on (Date, BarIndex) -- bars present on only one side (e.g.
    /// one leg's ATM price series had a gap) are dropped, matching Part 13's existing join
    /// behavior. NiftyFwd fields are taken from the Call sample; they are computed purely from the
    /// underlying's own close prices (see <see cref="MaSpreadRelationshipAnalyzer.CollectDayAsync"/>),
    /// so they are identical between a Call and Put sample sharing the same (Date, BarIndex) --
    /// order doesn't matter for that field.
    /// </summary>
    public static List<Row> Combine(
        IEnumerable<MaSpreadRelationshipAnalyzer.Sample> callSamples,
        IEnumerable<MaSpreadRelationshipAnalyzer.Sample> putSamples)
    {
        return callSamples
            .Join(
                putSamples,
                c => (c.Date, c.BarIndex),
                p => (p.Date, p.BarIndex),
                (c, p) => new Row(
                    c.Date, c.Timestamp, c.BarIndex, c.Spread, p.Spread, c.Spread - p.Spread,
                    c.NiftyFwd5, c.NiftyFwd10, c.NiftyFwd20))
            .ToList();
    }
}
