using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// An ADOPTED build compiles nothing, so until a bundle carried the producer's edges an adopting mesh
/// had no dependency edge set for the build it serves (<c>Doc/Architecture/DependencyGraph</c> →
/// adopted prebuilts; measured 2026-10-27: 0 edge sets on two production meshes after the roll that
/// shipped the extraction, every type adopted). <see cref="PrebuiltAssemblySeeder.WithAdoptedEdges"/>
/// writes the bundle's set once the record serves those bytes.
///
/// <para>Negative controls: a set for a build the record does NOT serve writes nothing, an outcome
/// that adopted nothing writes nothing, and a second pass over the same build writes nothing (the
/// writer runs on every bundle pass of every boot).</para>
/// </summary>
public class AnAdoptedBuildCarriesItsEdgesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The admission this process runs under — read live by the gate; unarmed unless a test says otherwise.</summary>
    private MeshAdmission admission = MeshAdmission.Unarmed;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
                services.AddSingleton<IMeshAdmissionAuthority>(new LiveAdmission(() => admission)));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private static byte[] EmitAssembly(string name)
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText($"namespace {name} {{ public class Marker {{ }} }}")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        compilation.Emit(stream).Success.Should().BeTrue("the premise: the test assembly emits");
        return stream.ToArray();
    }

    private async Task<string> AdoptedType(CancellationToken ct)
    {
        var typePath = $"{TestPartition}/Edges{Guid.NewGuid().ToString("N")[..8]}";
        await MeshService.CreateNode(MeshNode.FromPath(typePath) with
            {
                Name = typePath,
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition { Description = "adoption target", Configuration = "config => config" },
            })
            .Should().Within(TestTimeouts.Convergence).Emit("the NodeType must exist", cancellationToken: ct);
        return typePath;
    }

    private IObservable<PrebuiltAssemblySeeder.SeedOutcome> Seed(string typePath, byte[] bytes) =>
        PrebuiltAssemblySeeder.SeedDetailed(
            Mesh, typePath, bytes, pdbBytes: null,
            frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid, logger: null,
            dependencies: null, sourceFingerprint: null, moduleVersion: "1.0.0",
            sourcePaths: null, sourceIncludes: null, producerPlatformVersion: null, platformCeiling: null);

    private Task<MeshNode> SettledOn(string typePath, string mvid) =>
        Mesh.GetMeshNodeStream(typePath)
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && string.Equals(d.LatestAssemblyMvid, mvid, StringComparison.Ordinal)
                        && d.RequestedSourceStampAt is null,
                "the adoption lands and the owner settles its source stamp");

    /// <summary>The set as a bundle carries it: plain web JSON, no type discriminator.</summary>
    private static JsonElement BundleJson(string source, string mvid, string target) =>
        JsonSerializer.SerializeToElement(new DependencyEdgeSet
        {
            Source = source,
            BuildMvid = mvid,
            Commit = "0123456789abcdef0123456789abcdef01234567",
            Edges = [new DependencyEdge { Target = target, Kind = DependencyEdgeKinds.NodeType }],
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private async Task<MeshNode?> StoredRow(string typePath, CancellationToken ct) =>
        await Storage.Read(DependencyEdgeSetPaths.PathFor(typePath), Mesh.JsonSerializerOptions)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the store answers", ct);

    private Task<PrebuiltAssemblySeeder.SeedOutcome> WithEdges(
        string typePath, JsonElement json, PrebuiltAssemblySeeder.SeedOutcome outcome, CancellationToken ct) =>
        PrebuiltAssemblySeeder.WithAdoptedEdges(Mesh, typePath, json, outcome, null)
            .Should().Within(TestTimeouts.Convergence).Emit("the outcome passes through", ct);

    [Fact(Timeout = 120_000)]
    public async Task TheBundlesEdges_AreWrittenForTheBuildTheRecordServes_Once()
    {
        var ct = TestContext.Current.CancellationToken;
        var typePath = await AdoptedType(ct);
        var bytes = EmitAssembly("EdgesA" + Guid.NewGuid().ToString("N")[..6]);
        var mvid = ServedBuildIdentity.OfBytes(bytes)!;
        (await Seed(typePath, bytes).Should().Within(TestTimeouts.Convergence).Emit("a seed answers", ct))
            .Should().Be(PrebuiltAssemblySeeder.SeedOutcome.Adopted);
        var record = await SettledOn(typePath, mvid);

        // THE CASE: the bundle's set for the adopted bytes is written, under the record's version.
        (await WithEdges(typePath, BundleJson(typePath, mvid, "Lib/Used"), PrebuiltAssemblySeeder.SeedOutcome.Adopted, ct))
            .Should().Be(PrebuiltAssemblySeeder.SeedOutcome.Adopted, "the verdict is never changed by the edges");
        var row = await StoredRow(typePath, ct);
        row.Should().NotBeNull("an adopted build now has an edge set");
        var set = row!.ContentAs<DependencyEdgeSet>(Mesh.JsonSerializerOptions)!;
        set.BuildMvid.Should().Be(mvid);
        set.Edges.Select(e => e.Target).Should().Equal(["Lib/Used"]);
        var durable = await Storage.Read(typePath, Mesh.JsonSerializerOptions).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the record is stored", ct);
        set.StampVersion.Should().Be(durable!.Version, "the generation is the durable record's version");
        record.Should().NotBeNull();

        // Negative control 1: the same build again (every bundle pass of every boot) writes nothing.
        (await WithEdges(typePath, BundleJson(typePath, mvid, "Lib/Used"), PrebuiltAssemblySeeder.SeedOutcome.AlreadyServed, ct))
            .Should().Be(PrebuiltAssemblySeeder.SeedOutcome.AlreadyServed);
        var again = await StoredRow(typePath, ct);
        again.Should().NotBeNull();
        again!.Version.Should().Be(row!.Version, "an unchanged set is never rewritten");
    }

    [Fact(Timeout = 120_000)]
    public async Task ASetForAnotherBuild_OrAnUncoveredOutcome_WritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var typePath = await AdoptedType(ct);
        var bytes = EmitAssembly("EdgesB" + Guid.NewGuid().ToString("N")[..6]);
        var mvid = ServedBuildIdentity.OfBytes(bytes)!;
        (await Seed(typePath, bytes).Should().Within(TestTimeouts.Convergence).Emit("a seed answers", ct))
            .Should().Be(PrebuiltAssemblySeeder.SeedOutcome.Adopted);
        await SettledOn(typePath, mvid);

        // Negative control 2: the record serves another build — its edges are not these.
        await WithEdges(typePath, BundleJson(typePath, "ffffffffffffffffffffffffffffffff", "Lib/Other"),
            PrebuiltAssemblySeeder.SeedOutcome.Adopted, ct);
        (await StoredRow(typePath, ct)).Should().BeNull("a set for a build the record does not serve is not written");

        // Negative control 3: an outcome that adopted nothing writes nothing, even for the served build.
        await WithEdges(typePath, BundleJson(typePath, mvid, "Lib/Used"),
            PrebuiltAssemblySeeder.SeedOutcome.DeclinedIdentity, ct);
        (await StoredRow(typePath, ct)).Should().BeNull("a declined seed covers nothing");

        // …and the control that proves the two above could have written: the served build, covered.
        await WithEdges(typePath, BundleJson(typePath, mvid, "Lib/Used"), PrebuiltAssemblySeeder.SeedOutcome.Adopted, ct);
        (await StoredRow(typePath, ct)).Should().NotBeNull();
    }

    /// <summary>
    /// 🚨 A PROVISIONAL process only queues its adoption stamp (<see cref="MeshPublicationGate"/>), so
    /// at the moment the seed answers <c>Adopted</c> the record still names the previous build. The
    /// edge write must follow the stamp out of the gate, or admission releases the stamp alone and
    /// the adopted build is left without its edges.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AProvisionalAdoption_WritesItsEdgesWhenTheHeldStampIsReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var typePath = await AdoptedType(ct);
        var bytes = EmitAssembly("EdgesP" + Guid.NewGuid().ToString("N")[..6]);
        var mvid = ServedBuildIdentity.OfBytes(bytes)!;
        var gate = Mesh.ServiceProvider.GetRequiredService<MeshPublicationGate>();
        admission = MeshAdmission.Provisional;

        (await Seed(typePath, bytes).Should().Within(TestTimeouts.Convergence).Emit("a seed answers", ct))
            .Should().Be(PrebuiltAssemblySeeder.SeedOutcome.Adopted, "a held stamp is still an adoption for this process");
        await WithEdges(typePath, BundleJson(typePath, mvid, "Lib/Used"), PrebuiltAssemblySeeder.SeedOutcome.Adopted, ct);
        (await StoredRow(typePath, ct)).Should().BeNull("nothing is published while this process is provisional");

        admission = MeshAdmission.Admitted;
        gate.Reconsider();
        await SettledOn(typePath, mvid);

        var row = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => Storage.Read(DependencyEdgeSetPaths.PathFor(typePath), Mesh.JsonSerializerOptions).Take(1))
            .Where(n => n is not null)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the edges follow the released stamp out of the gate", ct);
        var set = row!.ContentAs<DependencyEdgeSet>(Mesh.JsonSerializerOptions)!;
        set.BuildMvid.Should().Be(mvid);
        set.Edges.Select(e => e.Target).Should().Equal(["Lib/Used"]);
    }

    private sealed class LiveAdmission(Func<MeshAdmission> current) : IMeshAdmissionAuthority
    {
        public MeshAdmission Admission => current();

        public string AdmissionReason => $"test admission {current()}";
    }
}
