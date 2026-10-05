using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// Immutable adaptive-V1 discovery seed: absolute RollingStrictDeltaRatioTotal values from the
/// 13 out-of-fold adaptive discovery sessions (2026-09-08..2026-09-25). The payload is gzip+base64
/// only to keep source size small; it is ordinary JSON doubles after decompression.
///
/// SHA/source provenance: NiftyResearcher adaptive-v1-flow-evolution artifact produced from
/// branch claude/sweet-volta-xv5ums after the OOF adaptive replay was frozen.
/// </summary>
public static class AdaptiveDiscoveryStrongSeed
{
    public static readonly DateOnly LastDiscoveryDate = new(2026, 9, 25);
    public const int ExpectedCount = 1917;
    public const double ExpectedTwoThirdsQuantile = 0.186286d;

    const string Payload = "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";

    static readonly Lazy<IReadOnlyList<double>> ValuesLazy = new(Decode);

    public static IReadOnlyList<double> AbsoluteStrictRatios => ValuesLazy.Value;

    static IReadOnlyList<double> Decode()
    {
        var compressed = Convert.FromBase64String(Payload);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var json = reader.ReadToEnd();
        var values = JsonSerializer.Deserialize<double[]>(json)
            ?? throw new InvalidOperationException("Adaptive discovery strong-state seed could not be decoded.");

        if (values.Length != ExpectedCount)
        {
            throw new InvalidOperationException(
                $"Adaptive discovery seed count mismatch: expected {ExpectedCount}, decoded {values.Length}.");
        }

        var q = AdaptiveWeak2Classifier.Quantile(values, AdaptiveWeak2Classifier.StrongQuantile);
        if (Math.Abs(q - ExpectedTwoThirdsQuantile) > 1e-12)
        {
            throw new InvalidOperationException(
                $"Adaptive discovery seed quantile mismatch: expected {ExpectedTwoThirdsQuantile:R}, decoded {q:R}.");
        }

        return values;
    }
}
