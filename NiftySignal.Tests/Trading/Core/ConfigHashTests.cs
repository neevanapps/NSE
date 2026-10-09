using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Session;

namespace NiftySignal.Tests.Trading.Core;

public class ConfigHashTests
{
    // Golden values: if one of these changes, the canonical serialisation or the default session policy changed, which silently
    // re-versions every persisted decision. Update deliberately, together with TradingConfig.SchemaVersion where meaning changed.
    private const string GoldenCanonicalJson = """{"Candles":{"Clock":"AdaptiveFuturesBars","HistoryDepth":64,"MaxCloseAge":null,"MinObservationsPerCandle":1},"ConfigVersion":"cp2-test","SchemaVersion":2,"Selection":{"ExpiryPolicy":"NearestExpiryOnOrAfterTradeDate","MaxQuoteAge":"00:00:02","MaximumEntryPremium":150,"MinCalendarDaysToExpiry":0,"MinimumEntryPremium":100,"Ranking":["AskNearestBandMidpoint","SmallestSpread"]},"Session":{"EntryClose":"15:00:00.0000000","EntryOpen":"09:30:00.0000000","FinalClose":"15:10:00.0000000","LiquidationStart":null,"SessionEnd":"15:35:00.0000000","TimeZoneId":"Asia/Kolkata"},"Variants":{}}""";

    private const string GoldenHash = "dd87e0188dd81bfe67be22c74ceb3f8618be15920ede8b16c9d4e73ec01092f5";

    private static TradingConfig Default() => new() { ConfigVersion = "cp2-test" };

    [Fact]
    public void DefaultConfig_HasAStableCanonicalFormAndHash()
    {
        Assert.Equal(GoldenCanonicalJson, Default().ToCanonicalJson());
        Assert.Equal(GoldenHash, Default().ComputeHash().Value);
    }

    [Fact]
    public void Hash_IsStableAcrossCulturesAndRepeatedRuns()
    {
        var baseline = Default().ComputeHash();

        foreach (var culture in new[] { "tr-TR", "de-DE", "ar-SA", "th-TH", "fr-FR", "hi-IN", "ja-JP" })
        {
            Assert.Equal(baseline, Culture.Under(culture, () => Default().ComputeHash()));
        }

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(baseline, Default().ComputeHash());
        }
    }

    [Fact]
    public void EverySemanticChange_ChangesTheHash()
    {
        var baseline = Default().ComputeHash();
        var variants = new[]
        {
            Default() with { ConfigVersion = "cp1-test2" },
            Default() with { Session = new SessionConfig { LiquidationStart = new TimeOnly(15, 5, 0) } },
            Default() with { Session = new SessionConfig { EntryOpen = new TimeOnly(9, 30, 1) } },
            Default() with { Session = new SessionConfig { FinalClose = new TimeOnly(15, 11, 0) } },
            Default() with { Session = new SessionConfig { SessionEnd = new TimeOnly(15, 36, 0) } },
        };

        var hashes = variants.Select(v => v.ComputeHash()).Append(baseline).ToList();

        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }

    [Fact]
    public void ConfigWithEqualValues_HashesEqually_WhateverTheConstructionOrder()
    {
        var a = new TradingConfig { ConfigVersion = "v", Session = new SessionConfig { EntryClose = new TimeOnly(15, 0, 0), EntryOpen = new TimeOnly(9, 30, 0) } };
        var b = new TradingConfig { Session = new SessionConfig { EntryOpen = new TimeOnly(9, 30, 0), EntryClose = new TimeOnly(15, 0, 0) }, ConfigVersion = "v" };

        Assert.Equal(a.ComputeHash(), b.ComputeHash());
    }

    private sealed record Sample
    {
        public required Dictionary<string, decimal> Map { get; init; }

        public required HashSet<string> Set { get; init; }

        public required List<string> List { get; init; }

        public decimal Amount { get; init; }

        public string? Note { get; init; }

        [CanonicalIgnore]
        public string Ignored { get; init; } = "x";
    }

    [Fact]
    public void Dictionaries_AndSets_AreOrderIndependent_ListsAreNot()
    {
        var a = new Sample
        {
            Map = new Dictionary<string, decimal> { ["b"] = 2m, ["a"] = 1m, ["c"] = 3m },
            Set = new HashSet<string> { "z", "y", "x" },
            List = new List<string> { "1", "2" },
        };
        var b = new Sample
        {
            Map = new Dictionary<string, decimal> { ["c"] = 3m, ["a"] = 1m, ["b"] = 2m },
            Set = new HashSet<string> { "x", "z", "y" },
            List = new List<string> { "1", "2" },
        };
        var reordered = new Sample { Map = a.Map, Set = a.Set, List = new List<string> { "2", "1" } };

        Assert.Equal(CanonicalJson.Hash(a), CanonicalJson.Hash(b));
        Assert.NotEqual(CanonicalJson.Hash(a), CanonicalJson.Hash(reordered));
    }

    [Fact]
    public void Decimals_AreNormalised_AndNullIsExplicit()
    {
        Sample Make(decimal amount, string? note = null) => new()
        {
            Map = new Dictionary<string, decimal>(),
            Set = new HashSet<string>(),
            List = new List<string>(),
            Amount = amount,
            Note = note,
        };

        Assert.Equal(CanonicalJson.Serialize(Make(1.5m)), CanonicalJson.Serialize(Make(1.50m)));
        Assert.Equal(CanonicalJson.Serialize(Make(0m)), CanonicalJson.Serialize(Make(0.000m)));
        Assert.Contains("\"Amount\":1.5,", CanonicalJson.Serialize(Make(1.500m)));
        Assert.Contains("\"Amount\":100,", CanonicalJson.Serialize(Make(100.00m)));
        Assert.Contains("\"Note\":null", CanonicalJson.Serialize(Make(1m)));
        Assert.NotEqual(CanonicalJson.Hash(Make(1m)), CanonicalJson.Hash(Make(1m, "")));
        Assert.DoesNotContain("Ignored", CanonicalJson.Serialize(Make(1m)));
        Assert.Equal(CanonicalJson.Serialize(Make(1.5m)), Culture.Under("de-DE", () => CanonicalJson.Serialize(Make(1.5m))));
    }

    [Fact]
    public void UnsafeTypes_AreRejected()
    {
        Assert.Throws<NotSupportedException>(() => CanonicalJson.Serialize(new { Value = 1.5d }));
        Assert.Throws<NotSupportedException>(() => CanonicalJson.Serialize(new { Value = 1.5f }));
        Assert.Throws<NotSupportedException>(() => CanonicalJson.Serialize(new { Value = DateTime.UnixEpoch }));
        Assert.Throws<NotSupportedException>(() => CanonicalJson.Serialize(new { Value = Guid.Empty }));
        Assert.Throws<NotSupportedException>(() => CanonicalJson.Serialize(new { Value = (DayOfWeek)99 }));
        Assert.Throws<NotSupportedException>(() => CanonicalJson.Serialize(new Dictionary<int, string> { [1] = "a" }));
    }

    [Fact]
    public void Strings_AreEscapedToPlainAscii()
    {
        Assert.Equal("\"a\\\"b\\\\c\\n\\u00e9\\u20b9\"", CanonicalJson.Serialize("a\"b\\c\né₹"));
    }

    [Fact]
    public void DateTimeOffsets_AreWrittenAsUtc_RegardlessOfOffset()
    {
        var utc = new DateTimeOffset(2026, 9, 22, 4, 0, 0, TimeSpan.Zero);
        var ist = new DateTimeOffset(2026, 9, 22, 9, 30, 0, TimeSpan.FromHours(5.5));

        Assert.Equal("\"2026-09-22T04:00:00.0000000Z\"", CanonicalJson.Serialize(utc));
        Assert.Equal(CanonicalJson.Serialize(utc), CanonicalJson.Serialize(ist));
    }

    [Fact]
    public void Validation_ReportsProblems()
    {
        Assert.Empty(Default().Validate());
        Assert.NotEmpty((Default() with { SchemaVersion = 1 }).Validate());
        Assert.NotEmpty((Default() with { ConfigVersion = " " }).Validate());
        Assert.NotEmpty((Default() with { Session = new SessionConfig { EntryClose = new TimeOnly(9, 0, 0) } }).Validate());
    }
}
