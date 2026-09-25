#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>ExpectedDbVersion is readable from outside an image</b> (#4764 (b3); policy
/// <c>db-migration-planned</c>) — and the reading is shaped so that a release published BEFORE the
/// field, a portal deployed BEFORE the field, and a garbled file can never be taken for a number.
///
/// <para>What is pinned: the file lives in a SUBDIRECTORY of <c>_releases</c> (so every existing
/// reader, which enumerates the marker directory's FILES and reads each body as a framework identity,
/// never sees it); absent/garbled is UNKNOWN, never zero; and the decision the two numbers feed —
/// <see cref="SelfUpdateVerdict.MayPatchAfter(MigrationRunOutcome, ReleaseSchemaStep)"/> — moves only
/// the two outcomes that establish nothing, in opposite directions.</para>
/// </summary>
public class ReleaseSchemaMarkerTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rsm-" + Guid.NewGuid().ToString("N"));

    public ReleaseSchemaMarkerTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a temp directory the OS will reap; nothing under test depends on it */ }
    }

    private void Publish(string version, string identity, string? schema)
    {
        var markers = Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName);
        Directory.CreateDirectory(markers);
        File.WriteAllText(Path.Combine(markers, version), identity + "\n");
        if (schema is null)
            return;
        var dir = Path.Combine(markers, ReleaseSchemaMarker.DirectoryName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, version), schema + "\n");
    }

    [Fact]
    public void APublishedNumberIsRead_AndBuildMetadataIsIgnored()
    {
        Publish("3.0.0-ci.9332", "sabc", "58");

        Assert.Equal(58, ReleaseSchemaMarker.Read(root, "3.0.0-ci.9332"));
        Assert.Equal(58, ReleaseSchemaMarker.Read(root, "3.0.0-ci.9332+39f49494eb"));
    }

    [Fact]
    public void AnAbsentOrGarbledNumberIsUnknown_NeverZero()
    {
        Publish("3.0.0-ci.9218", "sabc", null);
        Publish("3.0.0-ci.9219", "sabc", "fifty-eight");
        Publish("3.0.0-ci.9220", "sabc", "0");

        Assert.Null(ReleaseSchemaMarker.Read(root, "3.0.0-ci.9218"));
        Assert.Null(ReleaseSchemaMarker.Read(root, "3.0.0-ci.9219"));
        Assert.Null(ReleaseSchemaMarker.Read(root, "3.0.0-ci.9220"));
        Assert.Null(ReleaseSchemaMarker.Read(root, "3.0.0-ci.1"));
        Assert.Null(ReleaseSchemaMarker.Read(null, "3.0.0-ci.9218"));
    }

    [Fact]
    public void AVersionThatCouldEscapeTheDirectoryIsNeverAPath()
    {
        Assert.Null(ReleaseSchemaMarker.PathFor(root, "../etc/passwd"));
        Assert.Null(ReleaseSchemaMarker.PathFor(root, ".."));
        Assert.Null(ReleaseSchemaMarker.PathFor(root, ""));
    }

    /// <summary>
    /// 🚨 The reason the number lives in a SUBDIRECTORY: every reader of <c>_releases</c> that is
    /// already deployed enumerates its FILES as versions and reads each body as an identity. A schema
    /// file beside the markers would be listed as a release named "3.0.0-ci.9332.db" (or its body
    /// "58" read as a framework identity) by every portal that predates this field.
    /// </summary>
    [Fact]
    public void TheSchemaFileIsInvisibleToEveryExistingMarkerReader()
    {
        Publish("3.0.0-ci.9332", "sabc", "58");

        var releases = PublishedBundleCatalogue.PublishedReleases(root);
        Assert.Null(releases.Refusal);
        Assert.Equal(new[] { "3.0.0-ci.9332" }, releases.Versions.ToArray());
        Assert.Equal("3.0.0-ci.9332", SealedPublicationIndex.ReleasesOf(root)["sabc"]);
    }

    [Fact]
    public void TheStepComparesTheRunningReleaseWithTheTarget()
    {
        Publish("3.0.0-ci.9218", "sold", "57");
        Publish("3.0.0-ci.9332", "snew", "58");
        Publish("3.0.0-ci.9333", "snew", "58");
        Publish("3.0.0-ci.9000", "sancient", null);

        var bump = ReleaseSchemaMarker.Step(root, "3.0.0-ci.9218+39f49494", "3.0.0-ci.9332");
        Assert.True(bump.MovesSchema);
        Assert.False(bump.KeepsSchema);
        Assert.Contains("expects db_version 58", bump.Describe());
        Assert.Contains("expects 57", bump.Describe());

        var same = ReleaseSchemaMarker.Step(root, "3.0.0-ci.9332", "3.0.0-ci.9333");
        Assert.True(same.KeepsSchema);
        Assert.False(same.MovesSchema);

        var unknown = ReleaseSchemaMarker.Step(root, "3.0.0-ci.9000", "3.0.0-ci.9332");
        Assert.False(unknown.Known);
        Assert.False(unknown.MovesSchema);
        Assert.False(unknown.KeepsSchema);
    }

    /// <summary>
    /// The decision the numbers feed. An UNKNOWN step must leave the blanket rule exactly as it was —
    /// otherwise every release published before this field would change behaviour on the day it
    /// shipped — and a known step moves only NotSupported (refused across a bump) and Forbidden
    /// (allowed when nothing moves). Completed/Failed/TimedOut are decided by what the migration DID,
    /// which no published number can overrule.
    /// </summary>
    [Theory]
    [InlineData(MigrationRunOutcome.Completed, null, null, true)]
    [InlineData(MigrationRunOutcome.Completed, 57, 58, true)]
    [InlineData(MigrationRunOutcome.Failed, 58, 58, false)]
    [InlineData(MigrationRunOutcome.TimedOut, 58, 58, false)]
    [InlineData(MigrationRunOutcome.NotSupported, null, null, true)]
    [InlineData(MigrationRunOutcome.NotSupported, 58, 58, true)]
    [InlineData(MigrationRunOutcome.NotSupported, 57, 58, false)]
    [InlineData(MigrationRunOutcome.Forbidden, null, null, false)]
    [InlineData(MigrationRunOutcome.Forbidden, 57, 58, false)]
    [InlineData(MigrationRunOutcome.Forbidden, 58, 58, true)]
    [InlineData(MigrationRunOutcome.Forbidden, 58, 57, true)]
    public void TheSharedDecisionMovesOnlyTheOutcomesThatEstablishNothing(
        MigrationRunOutcome outcome, int? installed, int? target, bool mayPatch)
    {
        var step = new ReleaseSchemaStep("3.0.0-ci.1", installed, "3.0.0-ci.2", target);

        Assert.Equal(mayPatch, SelfUpdateVerdict.MayPatchAfter(outcome, step));
        if (installed is null && target is null)
            Assert.Equal(SelfUpdateVerdict.MayPatchAfter(outcome), SelfUpdateVerdict.MayPatchAfter(outcome, step));
    }
}
