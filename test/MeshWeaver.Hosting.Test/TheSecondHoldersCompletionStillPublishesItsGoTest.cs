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
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The claim QUEUE end to end on a real mesh: two candidates, granted in turn, and BOTH GOs on the
/// history at the end (#4708).
///
/// <para><b>Why core has this at all.</b> The defect it covers was caught by
/// <c>MeshWeaver.Hosting.Monolith.Test.BuildCoordinationTest.ClaimQueue_Go_And_HolderGuard</c> — a
/// test in <b>MeshWeaver.Plugins</b>, six failures across five branches including <c>main</c>,
/// while every core run of the same protocol was green because core ran none. The protocol is
/// core's, so a live exercise of it belongs here too; the decision-level assertions live next door
/// in <c>MeshWeaver.Graph.Test.AHoldersCompletionLandsOnTheOwnerTest</c>, which is where the
/// interleaving is BUILT rather than raced for.</para>
///
/// <para><b>What the defect was.</b> <c>CompleteBuild</c> asked "am I still the holder?" on a copy
/// the writer does not own — this hub's mirror, or the locally computed state its own predecessor
/// on the stream cache's per-path write queue handed forward. A completion's predecessor is very
/// often another completion, whose locally computed state carries <c>ClaimedBy = null</c>, so the
/// SECOND holder's guard refused, the lambda returned the node unchanged, nothing was posted, and
/// the caller was completed as a SUCCESS. The GO for the second fingerprint never existed and
/// nothing above <c>Debug</c> said so — which is why the only visible symptom was a wait that ran
/// out its whole budget.</para>
///
/// <para>🚨 The assertion is the EFFECT — the second fingerprint's GO on the authoritative record —
/// never that the write reported success, which is what the defect already did.</para>
/// </summary>
public class TheSecondHoldersCompletionStillPublishesItsGoTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string HolderA = "holder-a";
    private const string HolderB = "holder-b";

    // 120_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant. The
    // body's own waits are the real bounds; this only stops a wedge from running unbounded.
    [Fact(Timeout = 120_000)]
    public async Task BothHoldersGosSurvive_AndTheBuildEndsFree()
    {
        var hub = Mesh;
        var workspace = hub.GetWorkspace();

        await hub.EnsureBuildNode().FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        // A registers and is granted — nobody holds the node.
        await hub.RequestBuildClaim(HolderA, "fp-a").FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        await hub.ObserveBuildClaim(HolderA).FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        // B registers while A holds — it QUEUES.
        await hub.RequestBuildClaim(HolderB, "fp-b").FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        // A completes. Its GO lands and the arbiter hands the build to B.
        await hub.CompleteBuild(HolderA, new BuildGo("fp-a", DateTime.UtcNow)).FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        await hub.ObserveBuildGo("fp-a").FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        await hub.ObserveBuildClaim(HolderB).FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        // 🚨 …and B completes, with A's completion as this path's queue predecessor — the exact
        // ordering whose locally computed ClaimedBy=null made the old guard refuse in silence.
        await hub.CompleteBuild(HolderB, new BuildGo("fp-b", DateTime.UtcNow)).FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        var final = await workspace.GetMeshNodeStream(BuildNodeType.RootPath)
            .Select(n => n?.ContentAs<BuildState>(hub.JsonSerializerOptions))
            .Where(s => s?.Ready?.ContainsKey("fp-b") == true)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        final!.Ready!.Should().ContainKey("fp-a", "a newer build never revokes an older GO");
        final.Ready.Should().ContainKey("fp-b");
        final.ClaimedBy.Should().BeNull("both builds ended, so the node is free for the next image");
        final.Status.Should().Be(BuildStatus.Ready);
        final.ReportedOutcomes.Should().BeNull("every report is CONSUMED by the fold");
    }

    /// <summary>
    /// The CHUNK close-out on a real mesh — <c>BuildProtocolDriver.CloseChunk</c>'s shape without
    /// the bake that normally precedes it.
    ///
    /// <para>🚨 Why this is a separate case and not an argument. The close-out hand-rolled the same
    /// guarded terminal write the root's completion used, and now reports its outcome the same way —
    /// but it is folded by a DIFFERENT hub, the chunk node's own. That the chunk's hub runs an
    /// arbiter at all follows from the chunk being created with <c>NodeType = Build</c>
    /// (<c>BuildCoordinationExtensions.NewBuildNode</c>), which gives it the same
    /// <c>HubConfiguration</c>, and from <c>InstallClaimArbiter</c> opting out only for <c>_Claim</c>
    /// lock paths. That is a sound reading of the code and it is still a reading; this measures it.
    /// A chunk publishes no GO — the per-fingerprint history is root-only — so what has to land is
    /// its status and the release paths it wrote.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AChunkCloseOutLandsItsStatusAndReleasePaths()
    {
        var hub = Mesh;
        var workspace = hub.GetWorkspace();
        var chunkPath = $"{BuildNodeType.RootPath}/TestChunkCloseOut";
        var written = ImmutableList.Create($"{chunkPath}/Release/1");

        await hub.EnsureBuildNode().FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        await hub.EnsureBuildNode(chunkPath).FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        await hub.RequestBuildClaim(HolderA, "fp-a", chunkPath).FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        await hub.ObserveBuildClaim(HolderA, chunkPath).FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        await hub.ReportBuildOutcome(
                HolderA,
                BuildOutcome.Completed(DateTime.UtcNow, go: null, writtenPaths: written),
                chunkPath)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        var closed = await workspace.GetMeshNodeStream(chunkPath)
            .Select(n => n?.ContentAs<BuildState>(hub.JsonSerializerOptions))
            .Where(s => s is { Status: BuildStatus.Ready })
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        closed!.WrittenPaths.Should().Equal(
            written, "the release paths land in the same write as the status");
        closed.Ready.Should().BeNull("a chunk publishes no GO");
        closed.ClaimedBy.Should().BeNull("the chunk is free for a later build");
        closed.ReportedOutcomes.Should().BeNull();
    }
}
