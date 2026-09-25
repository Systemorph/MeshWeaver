using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 Issue #5057, the residual <c>#5201</c> recorded but did not repair: <b>a release that lands
/// after BOTH bounds is adopted when it lands</b>.
///
/// <para>The settle and its same-id re-cut each wait <see cref="NodeTypeBuildState.CreateBound"/>. A
/// landing slower than both ends the settle with <see cref="NodeTypeDefinition.UnreleasedBuildPath"/>
/// stamped, and then the node lands at exactly that path. It was measured doing so on the control
/// instance: <c>Hosting/TriageItem/Release/20260923055416-Hd-IFSiA</c> landed 21 s after its id was
/// minted, and #5474 folded 69 more such lines on the image carrying #5201. Before
/// <see cref="LateReleaseAdoption"/> nothing read the stamp again, so the type kept binding the
/// previous build's release.</para>
///
/// <para><b>The control, on a real mesh.</b> A NodeType is seeded in exactly the stamped state: the
/// settle is over, <c>latestReleasePath</c> names the previous build's release, and
/// <c>unreleasedBuildPath</c> names the id the attempts were minting. Its owner is activated, which
/// installs the watchers <c>MeshDataSource</c> installs on every NodeType hub. Then the release node
/// is created at the stamped path, which is the late landing. The pointer must follow. Without the
/// watcher registered this test fails at the <c>Match</c>, because nothing else ever writes the
/// pointer.</para>
/// </summary>
public class ALateReleaseIsAdoptedWhenItLandsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PreviousRelease = "Release/20260920072917-Djt66iSB";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static NodeTypeDefinition Stamped(string typePath, string unreleased) => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Ok,
        LastCompiledVersion = 3326,
        LatestReleasePath = $"{typePath}/{PreviousRelease}",
        UnreleasedBuildPath = unreleased,
        UnreleasedBuildReason = "the create did not land within 00:00:10",
        ReleaseNotes = "notes for the release that landed late",
        CompiledSources = ImmutableDictionary<string, long>.Empty,
    };

    private static string ReleasePathFor(string typePath, string id) =>
        $"{typePath}/{GraphNodeTypeNames.ReleaseSegment}/{id}";

    private static MeshNode ReleaseNodeAt(string releasePath, string typePath)
    {
        var releaseNamespace = $"{typePath}/{GraphNodeTypeNames.ReleaseSegment}";
        var version = releasePath[(releaseNamespace.Length + 1)..];
        return new MeshNode(version, releaseNamespace)
        {
            Name = $"Release {version}",
            NodeType = GraphNodeTypeNames.Release,
            MainNode = typePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeRelease
            {
                Path = releasePath,
                NodeTypePath = typePath,
                Release = version[15..],
                Version = version,
                FrameworkVersion = "3.0.0.0",
                CreatedAt = DateTimeOffset.UtcNow,
                AssemblyStoreVersion = 3326,
                Status = "Succeeded",
            },
        };
    }

    private async Task SeedAsync(string typePath, string unreleased)
    {
        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typePath,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = Stamped(typePath, unreleased),
        };
        await MeshService.CreateNode(typeNode)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the stamped NodeType must exist", cancellationToken: TestContext.Current.CancellationToken);

        // Reading the node through the stream activates its owner, and with it the watchers.
        await Mesh.GetMeshNodeStream(typePath)
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.Content is NodeTypeDefinition d
                        && string.Equals(d.UnreleasedBuildPath, unreleased, StringComparison.Ordinal),
                "the owner serves the stamped state");
    }

    /// <summary>
    /// 🚨 THE CASE: the settle is over, the stamp names the path, the node lands there, and the
    /// pointer follows it with the stamp cleared.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARelease_LandingAtTheStampedPath_IsAdopted()
    {
        var typePath = $"{TestPartition}/LateRelease{Guid.NewGuid().ToString("N")[..8]}";
        var unreleased = ReleasePathFor(typePath, "20260923055416-Hd-IFSiA");
        await SeedAsync(typePath, unreleased);

        // The late landing.
        await MeshService.CreateNode(ReleaseNodeAt(unreleased, typePath))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the late create lands", cancellationToken: TestContext.Current.CancellationToken);

        var adopted = await Mesh.GetMeshNodeStream(typePath)
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.Content is NodeTypeDefinition d
                        && string.Equals(d.LatestReleasePath, unreleased, StringComparison.Ordinal),
                "a release that landed after the settle stopped waiting must become the type's "
                + "release when it lands. Nothing else ever writes the pointer again (#5057)");

        var def = (NodeTypeDefinition)adopted!.Content!;
        def.UnreleasedBuildPath.Should().BeNull("the build has a release now");
        def.UnreleasedBuildReason.Should().BeNull("the reason never outlives the path");
        def.ReleaseNotes.Should().BeNull("the notes were written for this release and are spent on it");
        def.LastCompiledVersion.Should().Be(3326L, "adoption moves the pointer and touches no build state");
    }

    /// <summary>
    /// The negative half: a release at some OTHER path is not the stamped one, so the stamp stands
    /// and the pointer does not move. The watch is on one path and never on "any release".
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AReleaseAtAnotherPath_IsNotAdopted_AndTheStampStands()
    {
        var typePath = $"{TestPartition}/LateReleaseOther{Guid.NewGuid().ToString("N")[..8]}";
        var unreleased = ReleasePathFor(typePath, "20260923055416-Hd-IFSiA");
        await SeedAsync(typePath, unreleased);

        await MeshService.CreateNode(ReleaseNodeAt(ReleasePathFor(typePath, "20260923055426-OtherByt"), typePath))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("an unrelated release lands", cancellationToken: TestContext.Current.CancellationToken);

        await Mesh.GetMeshNodeStream(typePath)
            .Where(n => n?.Content is NodeTypeDefinition d
                        && (d.UnreleasedBuildPath is null
                            || !string.Equals(d.LatestReleasePath, $"{typePath}/{PreviousRelease}",
                                StringComparison.Ordinal)))
            .Should()
            .NotEmit(TestTimeouts.Quick, "a release for other bytes must never be adopted");
    }

    // ───────────── the decision, pure ─────────────

    [Fact]
    public void Adopt_MovesThePointer_WhenTheStampNamesThePath()
    {
        var def = Stamped("T", "T/Release/20260923055416-Hd-IFSiA");
        var adopted = LateReleaseAdoption.Adopt(def, "T/Release/20260923055416-Hd-IFSiA");
        adopted.Should().NotBeNull();
        adopted!.LatestReleasePath.Should().Be("T/Release/20260923055416-Hd-IFSiA");
        adopted.UnreleasedBuildPath.Should().BeNull();
        adopted.UnreleasedBuildReason.Should().BeNull();
        adopted.ReleaseNotes.Should().BeNull();
    }

    [Theory]
    [InlineData("T/Release/20260923055426-OtherByt")]
    [InlineData("t/release/20260923055416-hd-ifsia")]
    [InlineData("")]
    public void Adopt_LeavesTheNodeAlone_ForAnyOtherPath(string landed)
        => LateReleaseAdoption.Adopt(Stamped("T", "T/Release/20260923055416-Hd-IFSiA"), landed)
            .Should().BeNull("only the stamped id names these bytes");

    [Fact]
    public void Adopt_LeavesTheNodeAlone_WhenALaterSettleClearedTheStamp()
    {
        var cleared = Stamped("T", "T/Release/20260923055416-Hd-IFSiA") with
        {
            UnreleasedBuildPath = null,
            UnreleasedBuildReason = null,
        };
        LateReleaseAdoption.Adopt(cleared, "T/Release/20260923055416-Hd-IFSiA")
            .Should().BeNull("the stamp describes the current build only; a later settle owns the pointer");
        LateReleaseAdoption.Adopt(null, "T/Release/20260923055416-Hd-IFSiA").Should().BeNull();
    }
}
