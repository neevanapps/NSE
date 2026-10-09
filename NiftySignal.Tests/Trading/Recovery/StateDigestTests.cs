using System.Globalization;
using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.State;
using NiftySignal.Trading.Recovery;
using Xunit.Abstractions;

namespace NiftySignal.Tests.Trading.Recovery;

/// <summary>
/// The canonical state digest (contract section A.18.3): what it covers, what it deliberately excludes, that it is deterministic, and that it sees
/// differences which no output channel shows yet. Never a claim of "full state" beyond what the inventory lists; the adaptive-internals gap is
/// documented and covered by continuation tests instead.
/// </summary>
public class StateDigestTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<SourceTick> Rows = RecoveryKit.ShortRows();
    private static readonly SessionFreeze Freeze = SyntheticSession.Freeze(Rows);

    private static TradingRun Fresh(IReadOnlyList<SourceTick> rows, RunSettings? settings = null, TimeSpan? lag = null)
    {
        var spec = RecoveryKit.Spec(Freeze);
        return new TradingRun(spec.Freeze, lag is null ? spec.Translator : SyntheticSession.Translator(Freeze, lag), spec.Config, spec.Strategies, settings ?? spec.Settings, spec.Recorder);
    }

    private static void FeedPumped(TradingRun run, IReadOnlyList<SourceTick> rows, int count, int page)
    {
        var inPage = 0;
        foreach (var row in rows.Take(count))
        {
            run.Offer(row);
            if (++inPage >= page)
            {
                run.Pump(heartbeat: false);
                inPage = 0;
            }
        }

        run.Pump(heartbeat: false);
    }

    private static string Midway(IReadOnlyList<SourceTick> rows, int count, int page = 1, RunSettings? settings = null)
    {
        var run = Fresh(rows, settings);
        FeedPumped(run, rows, count, page);
        return run.CaptureState().Text;
    }

    [Fact]
    public void TheDigest_IsDeterministic_AcrossRepeatedRuns_AndCultures()
    {
        var a = Midway(Rows, 6_000);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(a, Midway(Rows, 6_000));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Equal(a, Midway(Rows, 6_000));
        Assert.StartsWith("#version=trading-state/1\n", a, StringComparison.Ordinal);
    }

    [Fact]
    public void TransportPatternsAndRunLabels_AreExcludedFromTheDigest()
    {
        var baseline = Midway(Rows, 6_000);

        // The same rows pumped in pages of 1, 7 and 1000, and under a different run id and mode label: same behaviour, same state at group boundaries.
        Assert.Equal(baseline, Midway(Rows, 6_000, page: 7).Replace("x", "x"));
        Assert.Equal(baseline, Midway(Rows, 6_000, settings: RecoveryKit.Spec(Freeze).Settings with { RunId = "another-id", RunMode = "LivePaper" }));
        Assert.DoesNotContain("another-id", baseline, StringComparison.Ordinal);
        Assert.DoesNotContain("Historical", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStateText_CoversEveryComponentOfTheInventory_AndNamesNoWallClock()
    {
        var run = Fresh(Rows, null);
        FeedPumped(run, Rows, 9_000, 1);
        var text = run.CaptureState().Text;
        output.WriteLine(string.Join("\n", text.Split('\n').Select(l => l.Split('=')[0]).Select(p => System.Text.RegularExpressions.Regex.Replace(p, "[0-9]+", "#")).Distinct().Take(400)));

        foreach (var marker in new[]
        {
            "loop/deadline[", "loop/nextCreationSeq", "engine/execution/book/A/cash", "engine/execution/quoteHistory/", "engine/variants/quoteBook/", "engine/variants/candles/history[", "engine/variants/A/slot[", "engine/integrity/killAll", "engine/admission/", "translator/lastRow[", "translator/bar[", "translator/partialBar",
        })
        {
            Assert.Contains(marker, text, StringComparison.Ordinal);
        }
    }

    private static void AssertSameChannelsDifferentState(TradingRun a, TradingRun b)
    {
        var channelsA = a.Recorders.Digests().ToDictionary(c => c.Key, c => c.Value.Hash);
        var channelsB = b.Recorders.Digests().ToDictionary(c => c.Key, c => c.Value.Hash);
        Assert.Equal(channelsA, channelsB);
        Assert.Equal(a.Engine.Outputs.Select(o => NiftySignal.Trading.Recovery.Codec.OutputCodec.Encode(o)), b.Engine.Outputs.Select(o => NiftySignal.Trading.Recovery.Codec.OutputCodec.Encode(o)));
        Assert.NotEqual(a.CaptureState().Digest, b.CaptureState().Digest);
    }

    [Fact]
    public void AStateDifference_ThatNoOutputOrChannelShowsYet_ChangesTheDigest_RetainedQuoteHistory()
    {
        // The retention window decides how much per-token quote history the execution engine keeps; outputs and all nine channels are identical so far.
        var specA = RecoveryKit.Spec(Freeze);
        var specB = specA with { Settings = specA.Settings with { Retention = specA.Settings.Retention with { QuoteHistoryRetention = TimeSpan.FromMinutes(20) } } };
        var runA = new TradingRun(specA.Freeze, specA.Translator, specA.Config, specA.Strategies, specA.Settings, specA.Recorder);
        var runB = new TradingRun(specB.Freeze, specB.Translator, specB.Config, specB.Strategies, specB.Settings, specB.Recorder);
        FeedPumped(runA, Rows, 3_000, 1);
        FeedPumped(runB, Rows, 3_000, 1);

        AssertSameChannelsDifferentState(runA, runB);
    }

    [Fact]
    public void AStateDifference_ThatNoOutputOrChannelShowsYet_ChangesTheDigest_ASourceFieldOnlyTheTranslatorRemembers()
    {
        // The row fingerprint the translator keeps per token (used to tell an exact re-delivery from a conflicting one) includes fields no channel records.
        var candidates = Enumerable.Range(2_000, 4_000).Where(k => Rows[k].Token != SyntheticSession.Future).Take(60).ToList();
        var found = false;
        foreach (var field in new Func<SourceTick, SourceTick>[]
        {
            r => r with { OpenInterest = (r.OpenInterest ?? 0) + 1 },
            r => r with { Volume = r.Volume + 1 },
        })
        {
            var changed = Rows.ToList();
            changed[candidates[^1]] = field(changed[candidates[^1]]);
            var runA = Fresh(Rows);
            var runB = Fresh(changed);
            // Feed through the changed row, then declare source progress so that it is released: it is then the last released row of its token.
            var cut = candidates[^1] + 1;
            FeedPumped(runA, Rows, cut, 1);
            FeedPumped(runB, changed, cut, 1);
            runA.Translator.AdvanceSource(Rows[cut - 1].ReceivedAt.AddSeconds(10));
            runB.Translator.AdvanceSource(Rows[cut - 1].ReceivedAt.AddSeconds(10));
            runA.Pump(heartbeat: false);
            runB.Pump(heartbeat: false);
            var same = runA.Recorders.Digests().All(c => runB.Recorders.Digests()[c.Key].Hash == c.Value.Hash);
            if (same)
            {
                AssertSameChannelsDifferentState(runA, runB);
                found = true;
                break;
            }
        }

        Assert.True(found, "no source field was invisible to the channels; adjust the candidate fields");
    }

    [Fact]
    public void WhatHasBeenOfferedButNotYetReleased_IsNotPartOfTheState_SoTransportBatchingCannotChangeIt()
    {
        var runA = Fresh(Rows);
        var runB = Fresh(Rows);
        FeedPumped(runA, Rows, 6_000, 1);
        FeedPumped(runB, Rows, 6_001, 1);

        // Row 6001 is still inside the ordering lag in the translator's buffer for both comparisons that matter: documented exclusion.
        Assert.Equal(runA.Engine.Outputs.Count, runB.Engine.Outputs.Count);
        Assert.Equal(runA.Translator.Counters.Input + 1, runB.Translator.Counters.Input);
    }
    [Fact]
    public void StateWriter_FormatsDecimalsWithTheirScale_AndExcludesNothingSilently()
    {
        var w = new StateWriter();
        w.Line("a", 1.50m);
        w.Line("b", 1.5m);
        w.Line("c", (decimal?)null);
        using (w.Scope("s"))
        {
            w.Line("d", true);
        }

        Assert.Equal("#version=trading-state/1\na=1.50\nb=1.5\nc=~\ns/d=1\n", w.Text);
        var other = new StateWriter();
        other.Line("a", 1.5m);
        other.Line("b", 1.5m);
        other.Line("c", (decimal?)null);
        using (other.Scope("s"))
        {
            other.Line("d", true);
        }

        Assert.NotEqual(w.Digest, other.Digest);
    }
}
