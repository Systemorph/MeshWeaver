using System.Collections.Immutable;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The drift measurement behind MeshWeaver#4620 — the half that decides whether a synced partition
/// still IS the tree of the commit it claims.
///
/// <para>🚨 Every case is stated in BOTH directions, because the defect this measurement exists to
/// remove is a reading that looks like a pass. The instance that produced it reported
/// <c>lastSyncOutcome: Imported</c> / <c>lastAttemptWasFinal: true</c> on a partition holding one
/// file from each of two trees; a detector that could only say "clean" would have reported the same
/// thing one layer up.</para>
/// </summary>
public class SyncedPartitionDriftTest
{
    private const string TypePath = "Hosting/Issue";
    private const string Sealed = "fingerprint-of-the-sealed-tree";
    private const string Drifted = "fingerprint-of-a-mixed-tree";

    private static PrebuiltBundleInventory Shelf(
        params (string Path, string Fingerprint)[] entries)
        => new(
            entries
                .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .ToImmutableDictionary(
                    g => g.Key,
                    g => g.Select(e => e.Fingerprint).ToImmutableHashSet(StringComparer.Ordinal),
                    StringComparer.OrdinalIgnoreCase),
            entries.Length,
            SealedReadOutcome.Read);

    private static IReadOnlyDictionary<string, NodeTypeDefinition> Live(string? fingerprint,
        string path = TypePath)
        => new Dictionary<string, NodeTypeDefinition>(StringComparer.Ordinal)
        {
            [path] = new() { CurrentSourceFingerprint = fingerprint },
        };

    [Fact]
    public void LiveSourcesNoBundleRecords_AreDrift_AndTheTypeIsNamed()
    {
        var reading = SyncedPartitionDrift.Measure(Live(Drifted), Shelf((TypePath, Sealed)));

        reading.Measured.Should().BeTrue("the shelf was readable and a type could be compared");
        reading.Drifted.Should().ContainSingle().Which.Should().Be(TypePath,
            "the partition holds sources the sealed commit's bundle does not record — the mix");
        reading.Compared.Should().Be(1, "a claim about drift states its denominator");
        reading.Reason.Should().Contain(TypePath, "an operator must be told WHICH type");
    }

    [Fact]
    public void LiveSourcesABundleRecords_AreNotDrift()
    {
        var reading = SyncedPartitionDrift.Measure(Live(Sealed), Shelf((TypePath, Sealed)));

        reading.Measured.Should().BeTrue();
        reading.Drifted.Should().BeEmpty("the partition is the tree its commit claims");
        reading.Compared.Should().Be(1);
    }

    /// <summary>🚨 The control that makes the clean answer mean something: the SAME live tree reads
    /// as drift the moment the shelf records another fingerprint. Without it, a measurement that
    /// always answered "clean" would pass the test above.</summary>
    [Fact]
    public void TheCleanAnswerIsNotUnconditional_TheSameTreeDriftsAgainstAnotherShelf()
    {
        var live = Live(Sealed);

        SyncedPartitionDrift.Measure(live, Shelf((TypePath, Sealed)))
            .Drifted.Should().BeEmpty();
        SyncedPartitionDrift.Measure(live, Shelf((TypePath, "some-other-commit")))
            .Drifted.Should().ContainSingle(
                "the reading follows the shelf, so it can fail — the negative control");
    }

    [Fact]
    public void AnUnusableShelf_IsNOTMeasured_AndNeverReportsACleanPartition()
    {
        var reading = SyncedPartitionDrift.Measure(
            Live(Drifted), PrebuiltBundleInventory.NotConfigured);

        reading.Measured.Should().BeFalse(
            "'the shelf could not be read' and 'the partition is clean' are different facts and "
            + "folding them is the defect #4620 is about");
        reading.Drifted.Should().BeEmpty("an abstention claims nothing either way");
        reading.Reason.Should().Contain("not judged");
    }

    [Fact]
    public void ATypeNoBundleNames_IsNotDrift_AndIsNotCounted()
    {
        var reading = SyncedPartitionDrift.Measure(
            Live(Drifted), Shelf(("Hosting/Something/Else", Sealed)));

        reading.Measured.Should().BeFalse(
            "nothing could be compared — this identity ships no bytes for the live type");
        reading.Drifted.Should().BeEmpty("a type no bundle names is not evidence of a mix");
    }

    [Fact]
    public void ATypeThatWasNeverFolded_IsNotDrift()
    {
        var reading = SyncedPartitionDrift.Measure(Live(null), Shelf((TypePath, Sealed)));

        reading.Measured.Should().BeFalse("a definition with no fingerprint has nothing to compare");
        reading.Drifted.Should().BeEmpty();
    }

    /// <summary>A partition of many types reports every drifted one, and the denominator counts
    /// only what could actually be compared.</summary>
    [Fact]
    public void ManyTypes_ReportEveryDriftedOne_AgainstTheComparableDenominator()
    {
        var live = new Dictionary<string, NodeTypeDefinition>(StringComparer.Ordinal)
        {
            ["P/A"] = new() { CurrentSourceFingerprint = Sealed },
            ["P/B"] = new() { CurrentSourceFingerprint = Drifted },
            ["P/C"] = new() { CurrentSourceFingerprint = Drifted },
            ["P/D"] = new() { CurrentSourceFingerprint = null },      // never folded
            ["P/E"] = new() { CurrentSourceFingerprint = Drifted },   // no bundle names it
        };
        var reading = SyncedPartitionDrift.Measure(
            live, Shelf(("P/A", Sealed), ("P/B", Sealed), ("P/C", Sealed)));

        reading.Measured.Should().BeTrue();
        reading.Drifted.Should().Equal("P/B", "P/C");
        reading.Compared.Should().Be(3, "D was never folded and E is named by no bundle");
        reading.Reason.Should().Contain("2 of 3");
    }

    /// <summary>The shelf may record SEVERAL fingerprints for one type (several bundles); holding
    /// any one of them is agreement, not drift.</summary>
    [Fact]
    public void AnyRecordedFingerprintIsAgreement()
        => SyncedPartitionDrift
            .Measure(Live(Sealed), Shelf((TypePath, "another"), (TypePath, Sealed)))
            .Drifted.Should().BeEmpty();
}
