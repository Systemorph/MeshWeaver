using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A write that does not touch a source's CODE (an AI description round, an icon, a rename) is not a
/// source change: it must not move the type's source snapshot, mark it dirty or recompile it. The
/// snapshot was keyed on <see cref="MeshNode.LastModified"/>, which every write stamps, so each such
/// write read as an edit (measured on the public cloud instance: a full compile 3-4 s after a
/// description round, twice, identical diagnostics). The snapshot now carries each source's code
/// digest, and a source whose code is unchanged keeps its recorded version (<see cref="SourceVersionCarry"/>).
///
/// <para>Each test pairs a negative control (a metadata-only write changes nothing) with its positive
/// twin (a code edit does move the snapshot), so a carry that suppressed real edits is red too.</para>
/// </summary>
public class AMetadataWriteIsNotASourceChangeTest
{
    private const string Path = "P/App/Source/code";
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private static MeshNode Source(string code, DateTimeOffset modified, string? description = null) =>
        new("code", "P/App/Source")
        {
            NodeType = "Code",
            Content = new CodeConfiguration { Code = code },
            LastModified = modified,
            Description = description,
        };

    /// <summary>The record after one compile of <paramref name="node"/> that the watcher has published.</summary>
    private static NodeTypeDefinition Published(MeshNode node)
    {
        var (versions, digests) = SourceVersionCarry.Fold([node], null, null);
        return new NodeTypeDefinition
        {
            Configuration = "config => config",
            CompilationStatus = CompilationStatus.Ok,
            LatestReleasePath = "P_App/v1.dll",
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
            CurrentSourceVersions = versions,
            CurrentSourceDigests = digests,
            CompiledSources = versions,
        };
    }

    [Fact]
    public void ADescriptionWrite_KeepsTheRecordedVersion()
    {
        var def = Published(Source("class A {}", T0));

        var (versions, digests) = SourceVersionCarry.Fold(
            [Source("class A {}", T0.AddMinutes(3), description: "an AI description")],
            def.CurrentSourceVersions, def.CurrentSourceDigests);

        Assert.Equal(def.CurrentSourceVersions![Path], versions[Path]);
        Assert.Equal(def.CurrentSourceDigests, digests);
    }

    [Fact]
    public void ACodeEdit_TakesTheNewVersion()
    {
        var def = Published(Source("class A {}", T0));

        var (versions, _) = SourceVersionCarry.Fold(
            [Source("class A { int X; }", T0.AddMinutes(3))],
            def.CurrentSourceVersions, def.CurrentSourceDigests);

        Assert.Equal(T0.AddMinutes(3).UtcTicks, versions[Path]);
        Assert.NotEqual(def.CurrentSourceVersions![Path], versions[Path]);
    }

    [Fact]
    public void ARecordWithoutDigests_NeverCarries()
    {
        // A record written before the field existed: nothing is carried, exactly the old behaviour.
        var def = Published(Source("class A {}", T0)) with { CurrentSourceDigests = null };

        var (versions, _) = SourceVersionCarry.Fold(
            [Source("class A {}", T0.AddMinutes(3))], def.CurrentSourceVersions, def.CurrentSourceDigests);

        Assert.Equal(T0.AddMinutes(3).UtcTicks, versions[Path]);
    }

    [Fact]
    public void ANodeThatIsNotTypedCode_NeverCarries()
    {
        var def = Published(Source("class A {}", T0));
        var untyped = Source("class A {}", T0.AddMinutes(3)) with { Content = "class A {}" };

        var (versions, digests) = SourceVersionCarry.Fold([untyped], def.CurrentSourceVersions, def.CurrentSourceDigests);

        Assert.Equal(T0.AddMinutes(3).UtcTicks, versions[Path]);
        Assert.Empty(digests);
    }

    [Fact]
    public void ANewSource_TakesItsOwnVersion()
    {
        var def = Published(Source("class A {}", T0));
        var added = new MeshNode("b", "P/App/Source")
        {
            NodeType = "Code", Content = new CodeConfiguration { Code = "class B {}" }, LastModified = T0.AddMinutes(1),
        };

        var (versions, _) = SourceVersionCarry.Fold(
            [Source("class A {}", T0.AddMinutes(3)), added], def.CurrentSourceVersions, def.CurrentSourceDigests);

        Assert.Equal(T0.AddMinutes(1).UtcTicks, versions["P/App/Source/b"]);
        Assert.Equal(def.CurrentSourceVersions![Path], versions[Path]);
    }

    [Fact]
    public void AMetadataWrite_PublishesNothing_AndTheTypeStaysClean()
    {
        var def = Published(Source("class A {}", T0));
        var (versions, digests) = SourceVersionCarry.Fold(
            [Source("class A {}", T0.AddMinutes(3), "an AI description")],
            def.CurrentSourceVersions, def.CurrentSourceDigests);

        var written = NodeTypeCompilationHelpers.PublishSourceSnapshot(
            def, versions, fingerprint: null, includes: null, (null, false), (d, _) => d, digests);

        Assert.Null(written);
        Assert.False(def.IsDirty);
    }

    [Fact]
    public void ACodeEdit_PublishesANewGeneration_AndTheTypeIsDirty()
    {
        var def = Published(Source("class A {}", T0));
        var (versions, digests) = SourceVersionCarry.Fold(
            [Source("class A { int X; }", T0.AddMinutes(3))], def.CurrentSourceVersions, def.CurrentSourceDigests);

        var written = NodeTypeCompilationHelpers.PublishSourceSnapshot(
            def, versions, fingerprint: null, includes: null, (null, false), (d, _) => d, digests);

        Assert.NotNull(written);
        Assert.True(written.IsDirty);
    }

    [Fact]
    public void TheCompileConsumedSnapshot_AgreesWithThePublishedOne_AfterAMetadataWrite()
    {
        // A compile run after the description write consumes the live node (LastModified moved) and
        // must record the same generation the watcher keeps, or the type reads dirty until the next edit.
        var def = Published(Source("class A {}", T0));
        var live = Source("class A {}", T0.AddMinutes(3), "an AI description");

        var consumed = SourceVersionCarry.Fold([live], def.CurrentSourceVersions, def.CurrentSourceDigests).Versions;

        Assert.False((def with { CompiledSources = consumed }).IsDirty);
    }

    [Fact]
    public void IsSourcesUpToDate_ReadsAMetadataWriteAsCurrent_AndACodeEditAsStale()
    {
        var def = Published(Source("class A {}", T0));

        Assert.True(NodeTypeBuildState.IsSourcesUpToDate(def, [Source("class A {}", T0.AddMinutes(3), "d")]));
        Assert.False(NodeTypeBuildState.IsSourcesUpToDate(def, [Source("class A { int X; }", T0.AddMinutes(3))]));
    }

    [Fact]
    public void TheSnapshotWait_DoesNotTreatAMetadataOnlyOwnerAsBehind()
    {
        // The release request's wait compares the snapshot with the owners' live versions. The owner
        // now holds a newer timestamp than the carried version; its code digest says it is the same code.
        var def = Published(Source("class A {}", T0));
        var owner = Source("class A {}", T0.AddMinutes(3), "d");
        var edited = Source("class A { int X; }", T0.AddMinutes(3));

        Assert.True(SourceVersionCarry.ShowsSameCode(def.CurrentSourceDigests, Path, owner));
        Assert.False(SourceVersionCarry.ShowsSameCode(def.CurrentSourceDigests, Path, edited));
    }
}
