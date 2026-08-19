using System;
using System.Collections.Generic;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins Systemorph/MeshWeaver#1836: a source whose node carries no real modification time must
/// still read as CHANGED when it changes.
///
/// <para><c>FileInfo.LastWriteTimeUtc</c> returns <c>1601-01-01</c> — it does not throw — for a
/// file that does not exist, so an unstattable path stamped that value onto the node as a real
/// timestamp. It then became the node's source version, compared EQUAL to itself across every
/// later edit, and the NodeType never recompiled again while every status field said
/// <c>Ok</c>. Measured on memex 2026-08-18: an <c>Edu/Module</c> change imported, logged
/// "Recompiling", minted a fresh release — and ran the old code, because six of its fourteen
/// sources carried 1601 on BOTH sides of the comparison.</para>
/// </summary>
public class SourceVersionMustChangeTests
{
    private static MeshNode Source(string id, DateTimeOffset lastModified, long version) =>
        new(id, "T/Source") { NodeType = "Code", LastModified = lastModified, Version = version };

    private static readonly DateTimeOffset NoRealTimestamp =
        new(new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    /// <summary>The constant IS the FILETIME epoch — the value .NET hands back for a missing file.</summary>
    [Fact]
    public void UnknownSourceVersion_IsTheFileTimeEpoch()
    {
        Assert.Equal(NodeTypeDefinition.UnknownSourceVersionTicks, NoRealTimestamp.UtcTicks);
        // And it is exactly what a non-existent file reports, which is the whole trap.
        var missing = new System.IO.FileInfo(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mw-absent-{Guid.NewGuid():N}"));
        Assert.False(missing.Exists);
        Assert.Equal(NoRealTimestamp.UtcDateTime, missing.LastWriteTimeUtc);
    }

    /// <summary>A node with a real mtime keys on it — unchanged behaviour for the healthy case.</summary>
    [Fact]
    public void RealTimestamp_IsUsedAsTheSourceVersion()
    {
        var when = new DateTimeOffset(2026, 8, 18, 11, 38, 12, TimeSpan.Zero);
        Assert.Equal(when.UtcTicks, NodeTypeDefinition.SourceVersionOf(Source("a", when, version: 7)));
    }

    /// <summary>
    /// No real mtime → key on <c>Version</c>, the owning hub's monotonic write counter. That is
    /// the only field guaranteed to move when the source moves.
    /// </summary>
    [Theory]
    [InlineData(0)]      // default(DateTimeOffset)
    [InlineData(1601)]   // FileInfo.LastWriteTimeUtc for a file that is not there
    public void NoRealTimestamp_FallsBackToTheNodeVersion(int year)
    {
        var stamp = year == 0 ? default : NoRealTimestamp;
        Assert.Equal(42, NodeTypeDefinition.SourceVersionOf(Source("a", stamp, version: 42)));
    }

    /// <summary>
    /// THE REGRESSION. An edit lands on a source with no real timestamp: the node's Version bumps,
    /// its LastModified does not. Keyed the old way both snapshots read 1601 and <c>IsDirty</c> was
    /// false — the change was invisible and no recompile was ever scheduled.
    /// </summary>
    [Fact]
    public void EditToAnUntimestampedSource_IsVisibleToIsDirty()
    {
        var before = Source("EduCourseNavigationProvider", NoRealTimestamp, version: 1);
        var after = Source("EduCourseNavigationProvider", NoRealTimestamp, version: 2);

        // What the old fold recorded: identical on both sides, so the edit vanished.
        Assert.Equal(before.LastModified.UtcTicks, after.LastModified.UtcTicks);

        var def = new NodeTypeDefinition
        {
            CompiledSources = Snapshot(before),
            CurrentSourceVersions = Snapshot(after),
        };
        Assert.True(def.IsDirty, "an edited source must read as dirty even with no usable timestamp");
    }

    /// <summary>The converse: no edit, no dirt. The fallback must not make every type permanently dirty.</summary>
    [Fact]
    public void UnchangedUntimestampedSource_IsNotDirty()
    {
        var node = Source("EduCourseNavigationProvider", NoRealTimestamp, version: 1);
        var def = new NodeTypeDefinition
        {
            CompiledSources = Snapshot(node),
            CurrentSourceVersions = Snapshot(node),
        };
        Assert.False(def.IsDirty);
    }

    /// <summary>
    /// The migration is ONE self-healing compile: a snapshot recorded the old way (raw 1601) differs
    /// from the same unchanged source keyed the new way, so the type recompiles exactly once and
    /// then converges instead of storming.
    /// </summary>
    [Fact]
    public void LegacySnapshot_RecompilesOnceThenConverges()
    {
        var node = Source("EduCourseNavigationProvider", NoRealTimestamp, version: 9);
        var legacy = new Dictionary<string, long> { [node.Path] = node.LastModified.UtcTicks };

        var firstPass = new NodeTypeDefinition
        {
            CompiledSources = legacy,
            CurrentSourceVersions = Snapshot(node),
        };
        Assert.True(firstPass.IsDirty, "the legacy 1601 snapshot must re-key and trigger one compile");

        // That compile re-records CompiledSources through the same rule — and it settles.
        var afterCompile = firstPass with { CompiledSources = Snapshot(node) };
        Assert.False(afterCompile.IsDirty, "and then it must settle — one heal, not a storm");
    }

    /// <summary>
    /// The mint site. A parser handed a path that is not on disk must not adopt 1601 as the
    /// node's modification time.
    ///
    /// <para>This is the half that matters most, and it is why fixing the storage adapter alone
    /// was not enough: the adapter only stamps <c>if (node.LastModified == default)</c>, and a
    /// parser that already stamped 1601 puts that guard permanently out of reach — 1601 is not
    /// default, so the adapter never corrects it. <c>.cs</c> files are exactly the ones whose
    /// timestamps drive a NodeType's compile.</para>
    /// </summary>
    [Fact]
    public void MissingFile_IsNeverStampedWithTheFileTimeEpoch()
    {
        var absent = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"mw-absent-{Guid.NewGuid():N}.cs");
        Assert.False(System.IO.File.Exists(absent));

        var stamped = MeshWeaver.Hosting.Persistence.FileTimestamps.ObservedAt(absent);

        Assert.NotEqual(NoRealTimestamp, stamped);
        Assert.True(stamped.UtcTicks > NodeTypeDefinition.UnknownSourceVersionTicks,
            "a node written now was modified now — never 1601, which cannot change and so "
            + "freezes the NodeType's assembly");
    }

    /// <summary>A file that IS there keeps its real mtime — the helper must not paper over reality.</summary>
    [Fact]
    public void ExistingFile_KeepsItsRealModificationTime()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"mw-present-{Guid.NewGuid():N}.cs");
        System.IO.File.WriteAllText(path, "// x");
        try
        {
            var expected = new DateTimeOffset(
                new System.IO.FileInfo(path).LastWriteTimeUtc, TimeSpan.Zero);
            Assert.Equal(expected,
                MeshWeaver.Hosting.Persistence.FileTimestamps.ObservedAt(path));
        }
        finally { System.IO.File.Delete(path); }
    }

    // The fold under test, applied exactly as both snapshot producers apply it.
    private static Dictionary<string, long> Snapshot(params MeshNode[] nodes)
    {
        var d = new Dictionary<string, long>();
        foreach (var n in nodes)
            d[n.Path] = NodeTypeDefinition.SourceVersionOf(n);
        return d;
    }
}
