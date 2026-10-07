using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 <b>A record's bytes missing on THIS pod are re-fetched from what was SHIPPED — not compiled,
/// not left as the assembly-unavailable card</b> (MeshWeaver#6052 ask 2).
///
/// <para>The shape, from a client estate: two replicas, a per-pod <c>FileSystemAssemblyStore</c>
/// (collection <c>local</c>), one SHARED NodeType record. Pod A adopted a shipped bundle and stamped
/// the record with the bytes' content path and MVID; pod B never held them. Pod B's only recoveries
/// were a recompile routed to the owner (landing the bytes on pod A again) and, after one attempt,
/// "its compiled assembly could not be loaded on this node". <c>/health</c> read
/// <c>bake-report bytesmissing=93</c>. The bytes the record names were in the registry all
/// along.</para>
///
/// <para>Each test stands two real stores in two directories — pod A and pod B — and a byte source
/// that serves exactly what a registry bundle would. No core interface is mocked: the source is
/// the seam, the store, the probe and the refetch are the production code.</para>
/// </summary>
public class BytesMissingRefetchesTheShippedBuildTest : IDisposable
{
    private const string TypePath = "Publish/Slide";
    private const long Version = 42;

    private readonly string podA = Path.Combine(Path.GetTempPath(), "mw-6052-a-" + Guid.NewGuid().ToString("N"));
    private readonly string podB = Path.Combine(Path.GetTempPath(), "mw-6052-b-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemAssemblyStore storeA;
    private readonly FileSystemAssemblyStore storeB;

    /// <summary>The shipped build — real assembly bytes, so the MVID is a real one.</summary>
    private static readonly byte[] Shipped = File.ReadAllBytes(typeof(ShippedBuildRefetch).Assembly.Location);

    /// <summary>A DIFFERENT build of the same type (another real assembly, another MVID).</summary>
    private static readonly byte[] OtherBuild = File.ReadAllBytes(typeof(FileSystemAssemblyStore).Assembly.Location);

    public BytesMissingRefetchesTheShippedBuildTest()
    {
        storeA = new FileSystemAssemblyStore(podA, NullLogger<FileSystemAssemblyStore>.Instance);
        storeB = new FileSystemAssemblyStore(podB, NullLogger<FileSystemAssemblyStore>.Instance);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { podA, podB })
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Pod A's adoption: the bytes land in A's store and the SHARED record names them.</summary>
    private async Task<MissingBuild> AdoptOnPodA(byte[] bytes)
    {
        var location = (await storeA.PutWithLocation(TypePath, Version, bytes, null).Should().Emit())!;
        return new MissingBuild(TypePath, Version, location.ContentPath, ShippedBuildRefetch.MvidOf(bytes));
    }

    private static IServiceProvider Services(IShippedBuildSource? source)
    {
        var services = new ServiceCollection();
        if (source is not null)
            services.AddSingleton(source);
        return services.BuildServiceProvider();
    }

    /// <summary>What a registry bundle serves: the bytes it carries, counting how often it is asked.</summary>
    private sealed class BundleSource(params ShippedBuild[] builds) : IShippedBuildSource
    {
        public int Calls;
        public IObservable<IReadOnlyList<ShippedBuild>> Fetch(IReadOnlyCollection<string> nodeTypePaths)
            => Observable.Defer(() =>
            {
                Calls++;
                return Observable.Return<IReadOnlyList<ShippedBuild>>(
                    builds.Where(b => nodeTypePaths.Contains(b.NodeTypePath)).ToList());
            });
    }

    /// <summary>
    /// THE FIX: pod B resolves the record's build by re-landing the shipped bytes under the record's
    /// own key — the SAME content-hashed name pod A wrote, carrying the record's MVID — so the
    /// record needs no write and the bind-time identity check passes.
    /// </summary>
    [Fact]
    public async Task APodLackingTheRecordsBytes_ReLandsTheShippedBuild_UnderTheRecordsOwnKey()
    {
        var record = await AdoptOnPodA(Shipped);
        var source = new BundleSource(new ShippedBuild(TypePath, Shipped, null, "registry Publish@1.0.0"));

        var resolved = await ShippedBuildRefetch
            .ResolveOrRefetch(Services(source), storeB, record, NullLogger.Instance)
            .Should().Emit();

        resolved.Should().NotBeNullOrEmpty("the shipped build IS the build the record names");
        Path.GetRelativePath(podB, resolved!).Replace('\\', '/').Should().Be(record.ContentPath,
            "content addressing lands the same bytes on the same name the record already carries");
        ServedBuildIdentity.OfFile(resolved).Should().Be(record.AssemblyMvid);
        source.Calls.Should().Be(1);
    }

    /// <summary>
    /// NEGATIVE CONTROL — the state before the fix: with no source, pod B misses exactly as it
    /// did, and the caller's existing recovery (recompile / assembly-unavailable card) runs.
    /// </summary>
    [Fact]
    public async Task WithoutASource_ThePodStillMisses()
    {
        var record = await AdoptOnPodA(Shipped);

        var resolved = await ShippedBuildRefetch
            .ResolveOrRefetch(Services(source: null), storeB, record, NullLogger.Instance)
            .Should().Emit();

        resolved.Should().BeNull();
    }

    /// <summary>
    /// 🚨 A shipped build that is NOT the record's build (the record names a compile made on pod A)
    /// is never landed: under the record's version it would be refused by the bind-time identity
    /// check and recompiled anyway, and it would sit in the store as a wrong answer to the version.
    /// </summary>
    [Fact]
    public async Task AShippedBuildThatIsNotTheRecordsBuild_IsNotLanded()
    {
        var record = await AdoptOnPodA(OtherBuild);
        var source = new BundleSource(new ShippedBuild(TypePath, Shipped, null, "registry Publish@1.0.0"));

        var resolved = await ShippedBuildRefetch
            .ResolveOrRefetch(Services(source), storeB, record, NullLogger.Instance)
            .Should().Emit();

        resolved.Should().BeNull();
        Directory.EnumerateFiles(podB, "*.dll", SearchOption.AllDirectories).Should().BeEmpty(
            "nothing is written under the record's version that the record does not name");
    }

    /// <summary>
    /// The boot sweep's half: a type the probe classifies <see cref="BakeState.BytesMissing"/>
    /// reads <see cref="BakeState.Baked"/> after the batch land — so the pre-warm does not compile
    /// it — and the whole batch costs ONE source call (one download per package, not per type).
    /// </summary>
    [Fact]
    public async Task BytesMissing_ReadsBaked_AfterTheBatchLand_InOneSourceCall()
    {
        var record = await AdoptOnPodA(Shipped);
        const string secondType = "Publish/Deck";
        var secondLocation = (await storeA.PutWithLocation(secondType, 7, OtherBuild, null).Should().Emit())!;
        var second = new MissingBuild(secondType, 7, secondLocation.ContentPath, ShippedBuildRefetch.MvidOf(OtherBuild));

        var definitions = new Dictionary<string, NodeTypeDefinition?>
        {
            [TypePath] = Claiming(record),
            [secondType] = Claiming(second),
        };
        var before = await NodeTypeBakeStatus.Probe(definitions, storeB).Should().Emit();
        Assert.Equal(new[] { secondType, TypePath }, before!.BytesMissing.Select(e => e.TypePath).OrderBy(p => p, StringComparer.Ordinal));

        var source = new BundleSource(
            new ShippedBuild(TypePath, Shipped, null, "registry Publish@1.0.0"),
            new ShippedBuild(secondType, OtherBuild, null, "registry Publish@1.0.0"));
        var landed = await ShippedBuildRefetch
            .Land(Services(source), storeB, source, [record, second], NullLogger.Instance)
            .Should().Emit();

        Assert.Equal(new[] { secondType, TypePath }, landed!.OrderBy(p => p, StringComparer.Ordinal));
        source.Calls.Should().Be(1);
        var after = await NodeTypeBakeStatus.Probe(definitions, storeB).Should().Emit();
        after!.BytesMissing.Should().BeEmpty();
        after.Entries.Should().OnlyContain(e => e.State == BakeState.Baked);
    }

    private static NodeTypeDefinition Claiming(MissingBuild build) => new()
    {
        CompilationStatus = CompilationStatus.Ok,
        LastCompiledVersion = build.Version,
        LatestAssemblyCollection = FileSystemAssemblyStore.FileSystemCollectionName,
        LatestAssemblyPath = build.ContentPath,
        LatestAssemblyMvid = build.AssemblyMvid,
        CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
    };

    /// <summary>The package a type belongs to is its first path segment — never guessed further.</summary>
    [Theory]
    [InlineData("Publish/Slide", "Publish")]
    [InlineData("Hosting/Deployment/Sub", "Hosting")]
    [InlineData("Solo", "Solo")]
    [InlineData("", null)]
    [InlineData("/Leading", null)]
    public void ThePackageOfANodeType_IsItsFirstSegment(string path, string? package)
        => RegistryShippedBuildSource.PackageOf(path).Should().Be(package);
}
