using System;
using System.Collections.Immutable;
using System.IO;
using System.Reactive.Linq;
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
/// 🚨 MeshWeaver#6038 — <b>an adoption whose output is unchanged writes nothing.</b>
///
/// <para>Measured on memex.systemorph.com, <c>Feedback/Feedback</c> v323389–v323396
/// (2026-10-08): the same legacy bundle re-stamped the record over its OWN stamp (15:11, 15:23),
/// and a fingerprint-verified bundle and the legacy one replaced each other every few minutes.
/// Each seed is two versions (the stamp, then the owner's source stamp) and a fresh store
/// generation, which is how one NodeType reached ~323k versions.</para>
///
/// <para>The case is on a real mesh with a real file-system store: seed bytes A, let the owner
/// settle, seed A again — nothing may be written. The NEGATIVE CONTROL seeds different bytes B on
/// the same record and must be adopted, so the skip is a verdict about the bytes, not a seeder that
/// stopped writing.</para>
/// </summary>
public class AnAdoptionThatChangesNothingWritesNothingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static byte[] EmitAssembly(string name)
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText($"namespace {name} {{ public class Marker {{ }} }}")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        result.Success.Should().BeTrue("the premise: the test assembly emits");
        return stream.ToArray();
    }

    private async Task CreateTypeAsync(string typePath)
    {
        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typePath,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition { Description = "adoption target", Configuration = "config => config" },
        };
        await MeshService.CreateNode(typeNode)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the NodeType must exist", cancellationToken: TestContext.Current.CancellationToken);
    }

    private IObservable<PrebuiltAssemblySeeder.SeedOutcome> Seed(string typePath, byte[] bytes) =>
        PrebuiltAssemblySeeder.SeedDetailed(
            Mesh, typePath, bytes, pdbBytes: null,
            frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid, logger: null,
            dependencies: null, sourceFingerprint: null, moduleVersion: "1.2.0",
            sourcePaths: null, sourceIncludes: null, producerPlatformVersion: null, platformCeiling: null);

    /// <summary>Waits until the record carries <paramref name="mvid"/> AND the owner has fulfilled
    /// the source stamp the adoption requested — the settled state a later seed observes.</summary>
    private Task<MeshNode> SettledOn(string typePath, string mvid) =>
        Mesh.GetMeshNodeStream(typePath)
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && string.Equals(d.LatestAssemblyMvid, mvid, StringComparison.Ordinal)
                        && d.RequestedSourceStampAt is null,
                "the adoption lands and the owner settles its source stamp");

    [Fact(Timeout = 120_000)]
    public async Task ReSeedingTheSameBytes_WritesNothing_AndDifferentBytesAreStillAdopted()
    {
        var ct = TestContext.Current.CancellationToken;
        var typePath = $"{TestPartition}/Churn{Guid.NewGuid().ToString("N")[..8]}";
        await CreateTypeAsync(typePath);

        var a = EmitAssembly("ChurnA" + Guid.NewGuid().ToString("N")[..6]);
        var mvidA = ServedBuildIdentity.OfBytes(a)!;

        (await Seed(typePath, a).Should().Within(TestTimeouts.Convergence).Emit("a seed always answers", ct))
            .Should().Be(PrebuiltAssemblySeeder.SeedOutcome.Adopted, "the first seed has nothing to keep");
        var settled = await SettledOn(typePath, mvidA);
        var before = settled.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;

        // THE CASE: the very same bytes again.
        (await Seed(typePath, a).Should().Within(TestTimeouts.Convergence).Emit("a seed always answers", ct))
            .Should().Be(PrebuiltAssemblySeeder.SeedOutcome.AlreadyServed,
                "the record already serves these exact bytes under the same stamp (#6038)");
        PrebuiltAssemblySeeder.IsCovered(PrebuiltAssemblySeeder.SeedOutcome.AlreadyServed)
            .Should().BeTrue("callers must count an already-served type as covered, never compile over it");

        var after = await Mesh.GetMeshNodeStream(typePath)
            .Should().Within(TestTimeouts.Convergence).Emit("the record is readable", ct);
        after.Version.Should().Be(settled.Version, "nothing was written — no new version (#6038)");
        var afterDef = after.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
        afterDef.LastCompiledVersion.Should().Be(before.LastCompiledVersion, "no new store key was minted");
        afterDef.LatestAssemblyPath.Should().Be(before.LatestAssemblyPath);

        // NEGATIVE CONTROL: different bytes on the same (unverified) record ARE adopted.
        var b = EmitAssembly("ChurnB" + Guid.NewGuid().ToString("N")[..6]);
        var mvidB = ServedBuildIdentity.OfBytes(b)!;
        mvidB.Should().NotBe(mvidA, "the premise: the control's bytes differ");
        (await Seed(typePath, b).Should().Within(TestTimeouts.Convergence).Emit("a seed always answers", ct))
            .Should().Be(PrebuiltAssemblySeeder.SeedOutcome.Adopted,
                "different bytes over an unverified build are a real change and must still be written");
        var replaced = await SettledOn(typePath, mvidB);
        replaced.Version.Should().BeGreaterThan(settled.Version);
    }
}

/// <summary>
/// The pure half of #6038: <see cref="PrebuiltAssemblySeeder.StandingBuildKept"/> — which standing
/// builds a bundle may NOT replace, and (the controls) which it still must.
/// </summary>
public class StandingBuildKeptTest
{
    private const string LiveFingerprint = "77f94fd3840f0065";

    private static NodeTypeDefinition Usable(BuildProvenance provenance, string? adoptedFingerprint,
        string? moduleVersion, string mvid = "97cc9233aaa64a3fb72edca5a2ce18ce") => new()
    {
        CompilationStatus = CompilationStatus.Ok,
        LastCompiledVersion = 323394,
        LatestAssemblyCollection = "local",
        LatestAssemblyPath = "Feedback_Feedback/v323394-x.dll",
        LatestAssemblyMvid = mvid,
        CompiledFrameworkVersion = PrebuiltAssemblySeeder.LiveFrameworkMvid,
        BuildProvenance = provenance,
        AdoptedSourceFingerprint = adoptedFingerprint,
        CurrentSourceFingerprint = LiveFingerprint,
        AdoptedModuleVersion = moduleVersion,
        CompiledSources = ImmutableDictionary<string, long>.Empty.Add("T/Source/A", 1),
        CurrentSourceVersions = ImmutableDictionary<string, long>.Empty.Add("T/Source/A", 1),
    };

    [Fact]
    public void SameBytesSameStamp_IsKept()
        => PrebuiltAssemblySeeder.StandingBuildKept(
                Usable(BuildProvenance.AdoptedUnverified, null, "1.2.0"), storeHasBytes: true,
                "97cc9233aaa64a3fb72edca5a2ce18ce", null, null, "1.2.0")
            .Should().NotBeNull();

    [Fact]
    public void SameBytes_ButTheStoreLostThem_IsReplaced()
        => PrebuiltAssemblySeeder.StandingBuildKept(
                Usable(BuildProvenance.AdoptedUnverified, null, "1.2.0"), storeHasBytes: false,
                "97cc9233aaa64a3fb72edca5a2ce18ce", null, null, "1.2.0")
            .Should().BeNull("a record whose bytes are gone here is not a usable build — BytesMissing re-seeds");

    [Fact]
    public void SameBytes_NowWithAFingerprint_IsWritten()
        => PrebuiltAssemblySeeder.StandingBuildKept(
                Usable(BuildProvenance.AdoptedUnverified, null, "1.2.0"), storeHasBytes: true,
                "97cc9233aaa64a3fb72edca5a2ce18ce", null, LiveFingerprint, "1.2.0")
            .Should().BeNull("the stamp changes — the owner can now verify the bytes");

    [Fact]
    public void VerifiedBuild_IsNotReplacedByALegacyBundle()
        => PrebuiltAssemblySeeder.StandingBuildKept(
                Usable(BuildProvenance.AdoptedVerified, LiveFingerprint, "1.6.8", mvid: "1fe15f852214405999a9a43f518dbd00"),
                storeHasBytes: true, "97cc9233aaa64a3fb72edca5a2ce18ce", null, null, "1.2.0")
            .Should().NotBeNull("the measured ping-pong: the legacy 1.2.0 bundle replaced the verified 1.6.8 build");

    [Fact]
    public void VerifiedBuild_IsNotReplacedByAnOlderOrEqualVerifiedBundle()
    {
        var standing = Usable(BuildProvenance.AdoptedVerified, LiveFingerprint, "1.6.8", mvid: "1fe1");
        PrebuiltAssemblySeeder.StandingBuildKept(standing, true, "other", null, LiveFingerprint, "1.6.8")
            .Should().NotBeNull("between two equally proven builds the standing one wins");
        PrebuiltAssemblySeeder.StandingBuildKept(standing, true, "other", null, LiveFingerprint, "1.6.2")
            .Should().NotBeNull();
    }

    [Fact]
    public void VerifiedBuild_IsReplacedByANewerVerifiedBundle()
        => PrebuiltAssemblySeeder.StandingBuildKept(
                Usable(BuildProvenance.AdoptedVerified, LiveFingerprint, "1.6.8", mvid: "1fe1"),
                true, "other", null, LiveFingerprint, "1.7.0")
            .Should().BeNull("a strictly newer release of the same source is a real upgrade");

    [Fact]
    public void UnverifiedBuild_IsReplacedByAVerifiedBundle()
        => PrebuiltAssemblySeeder.StandingBuildKept(
                Usable(BuildProvenance.AdoptedUnverified, null, "1.2.0"),
                true, "1fe15f852214405999a9a43f518dbd00", null, LiveFingerprint, "1.6.8")
            .Should().BeNull("the converging direction: proven bytes replace unprovable ones");

    [Fact]
    public void VerifiedBuild_IsReplacedOnAFrameworkRoll()
    {
        var standing = Usable(BuildProvenance.AdoptedVerified, LiveFingerprint, "1.6.8", mvid: "1fe1") with
        {
            CompiledFrameworkVersion = "0000000000000000",
        };
        PrebuiltAssemblySeeder.StandingBuildKept(standing, storeHasBytes: false, "other", null, null, "1.2.0")
            .Should().BeNull("a record keyed to another framework with no live-key bytes here (FrameworkStale) is never kept — a store hit under the live key would be Baked (bytes win)");
    }

    [Fact]
    public void LocallyCompiledLiveBuild_IsKept()
        => PrebuiltAssemblySeeder.StandingBuildKept(
                Usable(BuildProvenance.Compiled, null, null, mvid: "1fe1"),
                true, "other", null, LiveFingerprint, "9.0.0")
            .Should().NotBeNull();

    [Theory]
    [InlineData("1.7.0", "1.6.8", true)]
    [InlineData("1.6.8", "1.6.8", false)]
    [InlineData("1.6.8-ci.5", "1.6.8", false)]
    [InlineData("1.10", "1.9.3", true)]
    [InlineData(null, "1.6.8", false)]
    [InlineData("1.7.0", null, false)]
    [InlineData("garbage", "1.0", false)]
    public void IsStrictlyNewer(string? candidate, string? standing, bool expected)
        => PrebuiltAssemblySeeder.IsStrictlyNewer(candidate, standing).Should().Be(expected);
}
