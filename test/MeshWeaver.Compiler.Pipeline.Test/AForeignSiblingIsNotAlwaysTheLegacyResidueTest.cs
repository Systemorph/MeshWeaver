using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 The bake probe answers <see cref="BakeState.BytesMissing"/> whenever the store holds a build
/// under the record's version that is not the one the record names
/// (Systemorph/MeshWeaver.Plugins#2799) — but TWO different histories produce that reading, and
/// only one of them may give up its regression baseline:
///
/// <list type="bullet">
/// <item>the file <b>at the record's own path</b> carries another MVID: the first-write-wins
/// residue. The build the record names never reached the store, so a failed rebuild takes nothing
/// away and must not refuse a new replica's readiness;</item>
/// <item>the record's path is <b>gone</b> and a sibling of the version answered instead: the
/// record named a working build and lost it. That is an ordinary store miss, and an image that
/// cannot rebuild a type that WAS working must still be refused.</item>
/// </list>
///
/// <para>A real <see cref="FileSystemAssemblyStore"/> and real assembly bytes, so the MVIDs are
/// real. <b>Should fail if</b> the flag is inferred from "a foreign sibling answered" alone: the
/// lost-build case then reads <c>RecordNamesABuildTheStoreLacks</c> and drops its baseline.</para>
/// </summary>
public sealed class AForeignSiblingIsNotAlwaysTheLegacyResidueTest : IDisposable
{
    private const string TypePath = "Acme/Widget";
    private const long Version = 7;
    private const string LaterPlatformBuild = "9999.0.0-ci.1";

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-foreign-sibling-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemAssemblyStore store;

    /// <summary>Two real builds — two real MVIDs.</summary>
    private static readonly byte[] BuildN = File.ReadAllBytes(typeof(NodeTypeBakeStatus).Assembly.Location);
    private static readonly byte[] BuildN1 = File.ReadAllBytes(typeof(FileSystemAssemblyStore).Assembly.Location);

    public AForeignSiblingIsNotAlwaysTheLegacyResidueTest()
        => store = new FileSystemAssemblyStore(root, NullLogger<FileSystemAssemblyStore>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static NodeTypeDefinition Record(AssemblyStoreLocation location, string mvid) => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Ok,
        CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
        LastCompiledVersion = Version,
        LatestAssemblyCollection = location.Collection,
        LatestAssemblyPath = location.ContentPath,
        LatestAssemblyMvid = mvid,
    };

    private async Task<AssemblyStoreLocation> Put(byte[] bytes)
        => (await store.PutWithLocation(TypePath, Version, bytes, null).Take(1)
            .Should().Emit(cancellationToken: TestContext.Current.CancellationToken))!;

    private async Task<NodeTypeBakeEntry> Probe(NodeTypeDefinition record)
    {
        var report = await NodeTypeBakeStatus
            .Probe(ImmutableDictionary<string, NodeTypeDefinition?>.Empty.Add(TypePath, record), store)
            .Take(1)
            .Should().Emit(cancellationToken: TestContext.Current.CancellationToken);
        return report!.Entries.Single();
    }

    [Fact]
    public async Task TheFileAtTheRecordsOwnPath_CarryingAnotherMvid_IsTheResidue_AndNotABaseline()
    {
        var n = await Put(BuildN);
        // First-write-wins: the store handed back N's path, the record took N+1's identity.
        var entry = await Probe(Record(n, ServedBuildIdentity.OfBytes(BuildN1)!));

        entry.State.Should().Be(BakeState.BytesMissing);
        entry.RecordNamesABuildTheStoreLacks.Should().BeTrue(entry.Detail ?? "(no detail)");
        entry.IsRegressionBaselineFor(LaterPlatformBuild).Should().BeFalse(
            "the build this record names never reached the store, so a failed rebuild takes nothing away");
    }

    [Fact]
    public async Task ARecordWhoseOwnFileIsGone_WithASiblingLeft_NamedAWorkingBuild_AndKeepsItsBaseline()
    {
        var n = await Put(BuildN);
        await Put(BuildN1);
        File.Delete(n.LocalPath);   // the build the record names is lost; its sibling remains

        var entry = await Probe(Record(n, ServedBuildIdentity.OfBytes(BuildN)!));

        entry.State.Should().Be(BakeState.BytesMissing, "the store no longer holds the build the record names");
        entry.RecordNamesABuildTheStoreLacks.Should().BeFalse(
            "the record DID name a working build — a sibling answering for a vanished file is an ordinary store miss ("
            + entry.Detail + ")");
        entry.IsRegressionBaselineFor(LaterPlatformBuild).Should().BeTrue(
            "an image that cannot rebuild a type that was working must still be refused");
    }

    [Fact]
    public async Task ARecordWhoseOwnFileIsThere_IsBaked()
    {
        var n = await Put(BuildN);
        await Put(BuildN1);

        (await Probe(Record(n, ServedBuildIdentity.OfBytes(BuildN)!))).State.Should().Be(BakeState.Baked,
            "CONTROL — a sibling under the same version does not disturb a record whose own build is in the store");
    }
}
