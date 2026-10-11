using NiftySignal.Trading.Data;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Tests.Trading.Data;

/// <summary>Every record that is stored and loaded again is exactly equal to the original, and its own content hash still validates.</summary>
public class EntityMapperTests
{
    private static RunSnapshot Snapshot => ContractData.Snapshot;

    [Fact]
    public void TheRunHeader_RoundTripsExactly_IncludingProvenanceAndVariantIdentity()
    {
        var header = Snapshot.Header!;

        var entity = EntityMapper.ToEntity(header);
        var back = EntityMapper.ToRecord(entity);

        Assert.True(back.HashIsValid);
        Assert.Equal(header.ContentHash, back.ContentHash);                  // the hash covers identity, snapshots, horizon and variants
        Assert.Equal(header.Compatibility, back.Compatibility);
        Assert.Equal((header.RunId, header.TradeDate, header.ConfigJson, header.FreezeSnapshot, header.SourceManifest, header.Horizon),
            (back.RunId, back.TradeDate, back.ConfigJson, back.FreezeSnapshot, back.SourceManifest, back.Horizon));
        Assert.Equal((header.Provenance.GitCommit, header.Provenance.TreeClean, header.Provenance.Sdk), (back.Provenance.GitCommit, back.Provenance.TreeClean, back.Provenance.Sdk));
        Assert.Equal(header.Provenance.AssemblyHashes.OrderBy(a => a.Key, StringComparer.Ordinal), back.Provenance.AssemblyHashes.OrderBy(a => a.Key, StringComparer.Ordinal));
        Assert.True(header.Variants.SequenceEqual(back.Variants));
        Assert.All(entity.Variants, v => Assert.False(string.IsNullOrEmpty(v.StrategyId)));
    }

    [Fact]
    public void StepsOutputsControlsCheckpointsAndTheFinalRecord_RoundTripExactly()
    {
        var runId = Snapshot.Header!.RunId;
        foreach (var step in Snapshot.Steps)
        {
            var back = EntityMapper.ToRecord(EntityMapper.ToEntity(runId, step), EntityMapper.ApplicationsOf(runId, step));
            Assert.True(back.HashIsValid);
            Assert.Equal(step.ContentHash, back.ContentHash);
            Assert.Equal(step.AppliedControls, back.AppliedControls);
            Assert.True(step.Digests.SequenceEqual(back.Digests));
        }

        foreach (var output in Snapshot.Outputs)
        {
            var entity = EntityMapper.ToEntity(runId, output);
            var back = EntityMapper.ToRecord(entity);
            Assert.Equal(output, back);
            Assert.True(back.HashIsValid);
            Assert.Equal(OutputCodec.Version, entity.CodecVersion);
            Assert.Equal(output.StrategyId, entity.StrategyId);
        }

        var journalSeq = 1L;
        foreach (var control in Snapshot.Controls)
        {
            Assert.Equal(control, EntityMapper.ToRecord(EntityMapper.ToEntity(runId, control, journalSeq++)));
        }

        foreach (var checkpoint in Snapshot.Checkpoints)
        {
            Assert.Equal(checkpoint, EntityMapper.ToRecord(EntityMapper.ToEntity(runId, checkpoint)));
        }

        var (record, lines) = EntityMapper.ToEntity(runId, Snapshot.Final!);
        var final = EntityMapper.ToRecord(record, lines);
        Assert.True(final.HashIsValid);
        Assert.Equal(Snapshot.Final!.AuditLines, final.AuditLines);
        Assert.Equal(Snapshot.Final.SummaryLines, final.SummaryLines);
    }

    [Fact]
    public void Instants_AreStoredAsExactTicks_NotAsMicrosecondTimestamps()
    {
        var at = new DateTimeOffset(2026, 9, 22, 4, 0, 0, TimeSpan.Zero).AddTicks(7);          // 700 ns: invisible to a timestamptz column

        Assert.Equal(at, TickTime.FromTicks(TickTime.ToTicks(at)));
        Assert.NotEqual(at, TickTime.Convenience(TickTime.ToTicks(at)));                       // the convenience copy loses it, which is why it is never authoritative
        Assert.Throws<NotSupportedException>(() => TickTime.ToTicks(new DateTimeOffset(2026, 9, 22, 9, 30, 0, TimeSpan.FromHours(5.5))));
    }
}
