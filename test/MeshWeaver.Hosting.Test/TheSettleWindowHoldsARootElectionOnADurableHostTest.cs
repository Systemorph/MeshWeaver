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
/// #1424's convergence window, measured on a RUNNING mesh that has a durable store (#4729).
///
/// <para><b>Why a live case and not only the decision-level ones.</b> The defect was a decision
/// procedure that was right about a shape production never builds. Its unit test held a ROOT node
/// and passed; the durable path hands the same procedure the claim LOCK
/// (<c>Admin/Build/_Claim</c>), whose path is not <c>Admin/Build</c>, so the window was skipped on
/// every host with an <c>IStorageAdapter</c> — every real deployment. Only a test that lets the
/// installed arbiter decide, on a host wired the way a deployment is, can say whether the window is
/// charged where it matters; the sibling cases in
/// <c>MeshWeaver.Graph.Test.TheSettleWindowIsChargedOnTheDurablePathTest</c> say what is decided and
/// why.</para>
///
/// <para><b>The measurement this replaces.</b> Before the fix, the two full root elections of
/// <see cref="TheSecondHoldersCompletionStillPublishesItsGoTest"/> — on this same monolith mesh,
/// which registers an <c>IStorageAdapter</c> — completed in <b>0.878 s</b>, where one window alone
/// costs five seconds. That number is the defect, stated as a stopwatch reading.</para>
///
/// <para>🚨 The assertion is a LOWER bound on elapsed time and never an upper one. A grant that
/// cannot arrive before the window has run is a hard invariant of the decision; "and not much
/// after" is a statement about how fast the runner is, and would be a flake on a contended one. The
/// clock starts BEFORE the registration is written, so the elapsed it measures is never shorter
/// than the age of the registration the arbiter reasons about.</para>
/// </summary>
public class TheSettleWindowHoldsARootElectionOnADurableHostTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Holder = "settle-window-holder";

    // 120_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant. The
    // body's own waits are the real bounds; this only stops a wedge from running unbounded.
    [Fact(Timeout = 120_000)]
    public async Task AFreshRootElection_IsNotGrantedBeforeTheWindowHasRun()
    {
        var hub = Mesh;

        await hub.EnsureBuildNode().FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        // Before the registration, so this can only ever UNDER-state the age the arbiter reads.
        var startedAt = DateTime.UtcNow;

        await hub.RequestBuildClaim(Holder, "fp-settle").FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        var granted = await hub.ObserveBuildClaim(Holder).FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        var elapsed = DateTime.UtcNow - startedAt;

        granted.FrameworkVersion.Should().Be("fp-settle");
        elapsed.Should().BeGreaterThanOrEqualTo(
            BuildNodeType.GrantSettleWindow,
            "a fresh root election must let a concurrent registration from another cluster land "
            + "before it elects, and this host takes the DURABLE path — the one that was skipping "
            + "the window entirely");
    }

    /// <summary>
    /// 🚨 The other side, and the reason the window is a window: a registration that has ALREADY
    /// outlived it is granted, so a durable host still elects a builder. Nothing here waits on the
    /// clock — the registration carries its own age, which is the field the decision reads — and no
    /// upper bound is asserted, so a slow runner cannot turn this into a flake.
    ///
    /// <para>It passes on a tree with the defect too, by construction: that is what a control for
    /// "the fix did not simply stop granting" has to do. The discriminator is the case above.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARegistrationOlderThanTheWindow_IsGrantedWithoutFurtherWaiting()
    {
        var hub = Mesh;

        await hub.EnsureBuildNode().FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        // The same field RequestBuildClaim writes, only already converged — a candidate that
        // registered a minute ago and whose arbiter is only now getting to it.
        var aged = DateTime.UtcNow - BuildNodeType.GrantSettleWindow - TimeSpan.FromMinutes(1);
        await hub.GetWorkspace().GetMeshNodeStream(BuildNodeType.RootPath)
            .Update<BuildState>((node, content) => node with
            {
                Content = (content ?? new BuildState()) with
                {
                    RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                        .Add(Holder, new BuildClaimRequest("fp-aged", aged)),
                }
            })
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        var granted = await hub.ObserveBuildClaim(Holder).FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        granted.FrameworkVersion.Should().Be("fp-aged");
        granted.Status.Should().Be(BuildStatus.Planning);
    }
}
