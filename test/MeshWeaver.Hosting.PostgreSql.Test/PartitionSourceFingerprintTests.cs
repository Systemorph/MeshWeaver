using System.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Invariants for <see cref="PartitionSourceFingerprint"/> — the content-version key behind the
/// static-repo import pattern (Doc/Architecture/StaticRepoImport.md). Pure unit test, no fixture.
/// </summary>
public class PartitionSourceFingerprintTests
{
    private static MeshNode Doc(string id, string ns, string body, long version = 1) =>
        new(id, ns) { NodeType = "Markdown", Name = id, Content = body, Version = version };

    private static MeshNode[] SampleSet() =>
    [
        Doc("CRUD", "Doc/DataMesh", "crud body"),
        Doc("QuerySyntax", "Doc/DataMesh", "query body"),
        Doc("UnifiedPath", "Doc/DataMesh", "unified body"),
    ];

    [Fact]
    public void Fingerprint_IsStable_And16HexChars()
    {
        var a = PartitionSourceFingerprint.Compute(SampleSet(), versioned: false);
        var b = PartitionSourceFingerprint.Compute(SampleSet(), versioned: false);
        a.Should().Be(b);
        a.Should().HaveLength(16);
        a.Should().MatchRegex("^[0-9a-f]{16}$");
    }

    [Fact]
    public void Fingerprint_IsOrderIndependent()
    {
        var forward = PartitionSourceFingerprint.Compute(SampleSet(), versioned: false);
        var reversed = PartitionSourceFingerprint.Compute(SampleSet().Reverse(), versioned: false);
        reversed.Should().Be(forward, "enumeration order must not affect the fingerprint");
    }

    [Fact]
    public void Fingerprint_Changes_WhenContentChanges()
    {
        var before = PartitionSourceFingerprint.Compute(SampleSet(), versioned: false);
        var modified = SampleSet();
        modified[2] = modified[2] with { Content = "unified body — EDITED" };
        var after = PartitionSourceFingerprint.Compute(modified, versioned: false);
        after.Should().NotBe(before);
    }

    [Fact]
    public void Fingerprint_Changes_WhenNodeAddedOrRemoved()
    {
        var baseSet = SampleSet();
        var baseFp = PartitionSourceFingerprint.Compute(baseSet, versioned: false);

        var added = baseSet.Append(Doc("NodeTypes", "Doc/DataMesh", "nt body")).ToArray();
        PartitionSourceFingerprint.Compute(added, versioned: false).Should().NotBe(baseFp);

        var removed = baseSet.Take(2).ToArray();
        PartitionSourceFingerprint.Compute(removed, versioned: false).Should().NotBe(baseFp);
    }

    [Fact]
    public void VersionedMode_TracksVersion_NotContent()
    {
        // Versioned partitions fingerprint (path, version): a content edit at the SAME version is
        // invisible (the version is the contract), but a version bump changes the fingerprint.
        var v1 = PartitionSourceFingerprint.Compute(SampleSet(), versioned: true);

        var contentEditedSameVersion = SampleSet();
        contentEditedSameVersion[0] = contentEditedSameVersion[0] with { Content = "crud body — EDITED" };
        PartitionSourceFingerprint.Compute(contentEditedSameVersion, versioned: true)
            .Should().Be(v1, "versioned mode keys on version, not content");

        var bumped = SampleSet();
        bumped[0] = bumped[0] with { Version = 2 };
        PartitionSourceFingerprint.Compute(bumped, versioned: true)
            .Should().NotBe(v1, "a version bump must change the fingerprint");
    }

    [Fact]
    public void BlankPaths_AreIgnored()
    {
        var withBlank = SampleSet().Append(new MeshNode("", "") { Content = "noise" }).ToArray();
        PartitionSourceFingerprint.Compute(withBlank, versioned: false)
            .Should().Be(PartitionSourceFingerprint.Compute(SampleSet(), versioned: false));
    }
}
