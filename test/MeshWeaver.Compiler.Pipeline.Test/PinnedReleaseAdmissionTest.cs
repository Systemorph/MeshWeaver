using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// The pinned-release half of build-identity admission (#3472), decided from the release's
/// ARTIFACT links — pinned without a mesh. The first cut compared
/// <see cref="NodeTypeRelease.FrameworkVersion"/> (the assembly VERSION string, "3.0.0.0") to the
/// process identity hash, so every pin to a historical release was refused on every mesh from
/// 3.0.0-ci.7939; this repository had no test that pinned one. These are the three answers, each
/// with the input that distinguishes it from its neighbours.
/// </summary>
public class PinnedReleaseAdmissionDecisionTest
{
    private const string Live = "s1deb0000000000000000000000000000";
    private const string Foreign = "sc273ee39fdccbfc088f9aaf1fc548a9a";

    private static NodeTypeRelease Release(params ReleaseArtifact[] artifacts) => new()
    {
        Path = "P/T/Release/20260907120000-abcd1234",
        NodeTypePath = "P/T",
        Release = "abcd1234",
        Version = "12",
        FrameworkVersion = "3.0.0.0",           // the assembly version — NEVER the identity
        CreatedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero),
        Artifacts = artifacts.Length == 0 ? null : artifacts,
    };

    [Fact]
    public void AnArtifactForThisIdentity_IsAdmitted()
        => NodeTypeBuildIdentity.PinnedReleaseRefusal(
                Release(new ReleaseArtifact(Live, ReleaseArchitecture.Live, 12, "assemblies", "P_T/v12.dll")),
                Live, ReleaseArchitecture.Live)
            .Should().BeNull("the release records an artifact built by THIS process's framework");

    [Fact]
    public void AnArtifactForAnyArchitecture_IsAdmitted()
        => NodeTypeBuildIdentity.PinnedReleaseRefusal(
                Release(new ReleaseArtifact(Live, ReleaseArchitecture.Any, 12, "assemblies", "P_T/v12.dll")),
                Live, ReleaseArchitecture.Live)
            .Should().BeNull();

    [Fact]
    public void ArtifactsForAnotherIdentityOnly_AreRefused_NamingBoth()
    {
        var refusal = NodeTypeBuildIdentity.PinnedReleaseRefusal(
            Release(new ReleaseArtifact(Foreign, ReleaseArchitecture.Live, 12, "assemblies", "P_T/v12.dll")),
            Live, ReleaseArchitecture.Live);
        refusal.Should().NotBeNull("bytes keyed to another framework generation must not load here — the case #3472 exists for");
        refusal.Should().Contain("P/T/Release/20260907120000-abcd1234", "the refusal names the release")
            .And.Contain(Foreign[..9], "…and what it offers")
            .And.Contain(Live[..9], "…against what this process is");
    }

    [Fact]
    public void ARelease_WithNoArtifactLinksAtAll_IsAdmittedUnverified()
    {
        var legacy = Release();
        NodeTypeBuildIdentity.PinnedReleaseRefusal(legacy, Live, ReleaseArchitecture.Live)
            .Should().BeNull("a release written before artifact links states no identity — an absence is not a verdict (#890), "
                             + "and the assembly-version string it does carry has never gated adoption");
        NodeTypeBuildIdentity.IsPinnedReleaseUnverified(legacy).Should().BeTrue("…and the caller logs that it adopted unverified");
        NodeTypeBuildIdentity.IsPinnedReleaseUnverified(
                Release(new ReleaseArtifact(Live, ReleaseArchitecture.Live, 12, "assemblies", "P_T/v12.dll")))
            .Should().BeFalse();
    }

    [Fact]
    public void TheAssemblyVersionString_NeverDecides()
        // Same inputs as the admitted case except the field #3472 compared, set to something that
        // can never equal an identity hash — the answer must not move.
        => NodeTypeBuildIdentity.PinnedReleaseRefusal(
                Release(new ReleaseArtifact(Live, ReleaseArchitecture.Live, 12, "assemblies", "P_T/v12.dll"))
                    with { FrameworkVersion = "9.9.9.9" },
                Live, ReleaseArchitecture.Live)
            .Should().BeNull("NodeTypeRelease.FrameworkVersion is the assembly version and has never gated adoption");
}

/// <summary>
/// The same rule at the door that answers <see cref="GetCompilationPathRequest"/>: a type with two
/// REAL releases is pinned to the older one and the handler answers the older one's coordinates; a
/// hand-written release record that predates artifact links (the "3.0.0.0" shape every existing
/// release carries) is admitted; one whose only artifact is for another identity is refused. The
/// control arm (unpinned → newest) is what makes the pinned answer mean something.
/// </summary>
public class PinnedReleaseAdmissionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/PinnedReleaseType";
    private const string ForeignIdentity = "sc273ee39fdccbfc088f9aaf1fc548a9a";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static string Source(int answer) =>
        $"public record PinnedReleaseContent {{ public string Title {{ get; init; }} = \"\"; public int Answer() => {answer}; }}";

    private async Task<NodeTypeDefinition> WaitForRelease(string? knownRelease)
    {
        var node = await Mesh.GetMeshNodeStream(TypePath).Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.Content is NodeTypeDefinition d
                        && d.CompilationStatus == CompilationStatus.Ok
                        && !string.IsNullOrEmpty(d.LatestReleasePath)
                        && d.LatestReleasePath != knownRelease,
                "a compile of the source lands a new release on the type");
        return (NodeTypeDefinition)node.Content!;
    }

    private async Task<NodeTypeRelease> ReadRelease(string releasePath)
    {
        var node = await Mesh.GetMeshNodeStream(releasePath).Should().Within(TestTimeouts.Convergence)
            .Match(n => n?.Content is NodeTypeRelease, "the release node is readable");
        return (NodeTypeRelease)node.Content!;
    }

    private Task Pin(string? releasePath) =>
        Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with { RequestedReleasePath = releasePath })
            .Should().Within(TestTimeouts.Convergence).Emit();

    private async Task<GetCompilationPathResponse> AskForCompilationPath()
    {
        var reply = await GetClient()
            .Observe(new GetCompilationPathRequest(), o => o.WithTarget(new Address(TypePath)))
            .Take(1)
            .Should().Within(TestTimeouts.CrossSilo).Emit("the type's hub answers a compilation-path request");
        return reply.Message;
    }

    [Fact]
    public async Task APinToAnEarlierRealRelease_IsHonoured_AndAForeignOrLegacyRecordAnswersAsSpecified()
    {
        await MeshService.CreateNode(MeshNode.FromPath(TypePath) with
            {
                Name = "PinnedReleaseType",
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition
                {
                    Configuration = "config => config.WithContentType<PinnedReleaseContent>()",
                },
            })
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("api", $"{TypePath}/Source")
            {
                NodeType = "Code",
                Name = "api",
                State = MeshNodeState.Active,
                Content = new CodeConfiguration { Language = "csharp", Code = Source(1) },
            }))
            .Should().Within(TestTimeouts.CrossSilo).Emit();

        var v1 = (await WaitForRelease(knownRelease: null)).LatestReleasePath!;
        var v1Release = await ReadRelease(v1);
        v1Release.Artifacts.Should().NotBeNull("a release the pipeline writes today links its artifact");
        v1Release.Artifacts!.Should().Contain(a => a.FrameworkIdentity == NodeTypeCompilationHelpers.FrameworkVersion,
            "…keyed to this process's framework identity");
        v1Release.FrameworkVersion.Should().MatchRegex(@"^\d+\.\d+\.\d+\.\d+$",
            "the field #3472 compared is the assembly VERSION string — this is what every existing release carries");

        await Mesh.GetMeshNodeStream($"{TypePath}/Source/api")
            .Update<CodeConfiguration>(c => c with { Code = Source(2) })
            .Should().Within(TestTimeouts.Convergence).Emit();
        // A source edit marks the type dirty; a RELEASE is cut on request — the operator's Compile
        // button (RequestedReleaseAt), which InstallReleaseRequestWatcher turns into the compile.
        await Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with { RequestedReleaseAt = DateTimeOffset.UtcNow, RequestedReleaseBy = "operator" })
            .Should().Within(TestTimeouts.Convergence).Emit();
        var v2 = (await WaitForRelease(knownRelease: v1)).LatestReleasePath!;
        v2.Should().NotBe(v1);
        var v2Release = await ReadRelease(v2);

        // CONTROL — unpinned, the newest build answers.
        var newest = await AskForCompilationPath();
        newest.Success.Should().BeTrue(newest.Error ?? "unpinned resolves");
        newest.ContentPath.Should().Be(v2Release.AssemblyContentPath, "unpinned resolves the newest release");

        // THE ARM UNDER TEST — pinned to the EARLIER real release: honoured, not refused.
        await Pin(v1);
        var pinned = await AskForCompilationPath();
        pinned.Success.Should().BeTrue(
            $"a pin to a release this process's framework built must be honoured — before the fix every pin was "
            + $"refused as 'built against framework 3.0.0.0'; error: {pinned.Error}");
        pinned.ContentPath.Should().Be(v1Release.AssemblyContentPath, "the pinned release's bytes, not the newest");

        // LEGACY — a record with V1's coordinates but NO artifact links (what a pre-link release
        // looks like): admitted unverified, the exact record shape the first cut refused.
        var legacyPath = $"{TypePath}/Release/20260907000000-legacy01";
        await MeshService.CreateNode(MeshNode.FromPath(legacyPath) with
            {
                Name = "legacy release",
                NodeType = GraphNodeTypeNames.Release,
                State = MeshNodeState.Active,
                Content = v1Release with { Path = legacyPath, Release = "legacy01", Artifacts = null },
            })
            .Should().Within(TestTimeouts.Convergence).Emit();
        await Pin(legacyPath);
        var legacy = await AskForCompilationPath();
        legacy.Success.Should().BeTrue($"a release predating artifact links is admitted unverified; error: {legacy.Error}");
        legacy.ContentPath.Should().Be(v1Release.AssemblyContentPath);

        // FOREIGN — the same coordinates, but the only artifact link names another identity: refused, named.
        var foreignPath = $"{TypePath}/Release/20260907000001-foreign1";
        await MeshService.CreateNode(MeshNode.FromPath(foreignPath) with
            {
                Name = "foreign release",
                NodeType = GraphNodeTypeNames.Release,
                State = MeshNodeState.Active,
                Content = v1Release with
                {
                    Path = foreignPath, Release = "foreign1",
                    Artifacts = [new ReleaseArtifact(ForeignIdentity, ReleaseArchitecture.Live,
                        v1Release.AssemblyStoreVersion, v1Release.AssemblyCollection, v1Release.AssemblyContentPath)],
                },
            })
            .Should().Within(TestTimeouts.Convergence).Emit();
        await Pin(foreignPath);
        var foreign = await AskForCompilationPath();
        foreign.Success.Should().BeFalse("bytes keyed to another framework generation must not load here");
        foreign.Error.Should().Contain("carries no artifact for framework").And.Contain(ForeignIdentity[..9]);
    }
}
