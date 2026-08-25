using System;
using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Orleans.Runtime;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>#2076 — a follower waited forever on a claim whose holder was gone.</b>
///
/// <para><b>What happened</b> (memex-cloud, 2026-08-22, reproduced twice). A portal pod resolved its
/// identity, logged <i>"BuildProtocol: claim held elsewhere — … follows the build"</i> and never
/// proceeded. A cluster-wide scan over both namespaces and every pod found NO pod holding the grant
/// and none publishing GO: the durable claim's holder was a pod that had been <b>deleted
/// mid-boot</b>. The follower sat in <c>FollowGo</c> for 25+ minutes and, with
/// <c>PreWarm:GateReadiness=true</c>, held its readiness — and therefore the whole rollout.</para>
///
/// <para><b>Why the existing recovery could not fire.</b> The protocol already has everything it
/// needs: the claim arbiter re-runs on a periodic tick, and <c>BuildNodeType.HolderStillHoldsIt</c>
/// ages a holder out after <see cref="BuildNodeType.ClaimStaleAfter"/> when membership has no
/// opinion. But that clock is consulted <i>only</i> for <see cref="ClusterMemberState.Unknown"/> —
/// an <see cref="ClusterMemberState.Alive"/> verdict means <i>"never take over, however old the
/// heartbeat looks"</i>, which is correct and deliberate (#1355: a stopped heartbeat on a running
/// process means busy or starved, and evicting it puts two builders on one compile).
/// <see cref="OrleansClusterMembership"/> mapped <see cref="SiloStatus.Created"/> and
/// <see cref="SiloStatus.Joining"/> to Alive. Orleans only probes ACTIVE silos, so a process that
/// died before finishing its join leaves a Created/Joining row that no failure detector will ever
/// move to <see cref="SiloStatus.Dead"/> — the holder reads Alive forever, and the clock fallback is
/// STRUCTURALLY UNREACHABLE for precisely the case it exists to cover.</para>
///
/// <para><b>The fix is a classification, not a bound.</b> No timer is added, no budget widened, no
/// retry introduced — <c>Alive</c> is narrowed back to what its contract says: a member the cluster
/// has POSITIVELY recorded as running. A member that never became one is <c>Unknown</c>, which hands
/// the decision to the heartbeat clock that was there all along. That is observing the holder's
/// silence, not guessing at it: a holder that is genuinely mid-join and working keeps its claim
/// through the heartbeat it writes, and only one silent for the full staleness budget is displaced.</para>
/// </summary>
public class NeverActiveHolderIsNotAliveTest
{
    private static readonly JsonSerializerOptions Options = new();
    private static readonly DateTime T0 = new(2026, 8, 22, 13, 20, 0, DateTimeKind.Utc);

    /// <summary>
    /// The mapping itself. <see cref="SiloStatus.Created"/>/<see cref="SiloStatus.Joining"/> are the
    /// two that changed; the rest are pinned so the narrowing cannot quietly grow.
    /// </summary>
    [Theory]
    // The regression under test: a silo that never became a full member. Nothing probes it, so
    // nothing will ever move it to Dead — calling it Alive is a verdict the cluster cannot support.
    [InlineData(SiloStatus.Created, ClusterMemberState.Unknown)]
    [InlineData(SiloStatus.Joining, ClusterMemberState.Unknown)]
    // Reached Active and still executing — including while it drains. Anything it holds, it holds.
    [InlineData(SiloStatus.Active, ClusterMemberState.Alive)]
    [InlineData(SiloStatus.ShuttingDown, ClusterMemberState.Alive)]
    [InlineData(SiloStatus.Stopping, ClusterMemberState.Alive)]
    // The ONE positive departure verdict.
    [InlineData(SiloStatus.Dead, ClusterMemberState.Gone)]
    // Absence is not death (see the class remarks on OrleansClusterMembership).
    [InlineData(SiloStatus.None, ClusterMemberState.Unknown)]
    public void SiloStatus_MapsToTheStateItsConsumersCanActOn(
        SiloStatus status, ClusterMemberState expected)
        => OrleansClusterMembership.Classify(status).Should().Be(expected,
            "Alive is read as permission-denied-forever by every takeover rule, so it must mean "
            + "'the cluster positively recorded this member as running' — never merely 'a row "
            + "exists for it' (#2076)");

    /// <summary>
    /// The consequence, expressed on the decision procedure the outage actually stalled: a build
    /// claim held by a silo that never reached Active is handed to the waiting candidate once the
    /// staleness budget has elapsed.
    ///
    /// <para><b>Non-vacuity.</b> On <c>origin/main</c> <c>Classify(Joining)</c> is <c>Alive</c>, so
    /// <c>HolderStillHoldsIt</c> short-circuits before ever reading the clock and
    /// <c>Arbitrate</c> returns the node unchanged — this assertion fails with
    /// <c>ClaimedBy == "boot-casualty"</c>, which is the 25-minute hang in one line.</para>
    /// </summary>
    [Fact]
    public void AClaimHeldByASiloThatNeverJoined_IsTakenOverOnceTheHeartbeatHasAgedOut()
    {
        var node = ClaimHeldBy("boot-casualty", "silo-deleted-mid-boot", T0);

        var result = BuildNodeType.Arbitrate(
                node, Options,
                T0 + BuildNodeType.ClaimStaleAfter + TimeSpan.FromSeconds(1),
                new StatusCluster("silo-live", ("silo-deleted-mid-boot", SiloStatus.Joining)))
            .ContentAs<BuildState>(Options)!;

        result.ClaimedBy.Should().Be("follower",
            "a holder that never became a cluster member cannot defend its claim forever — nothing "
            + "will ever probe it Dead, so the staleness clock has to govern it (#2076)");
        result.FrameworkVersion.Should().Be("fp-next");
    }

    /// <summary>
    /// The guard on the other side, so the narrowing above cannot be read as "aging out is now
    /// allowed for anyone": a holder still mid-join whose heartbeat is FRESH keeps its claim. The
    /// clock governs it — that is the whole point — and the clock says it is alive.
    /// </summary>
    [Fact]
    public void AJoiningHolderWithAFreshHeartbeat_KeepsItsClaim()
    {
        var node = ClaimHeldBy("still-booting", "silo-joining", T0);

        BuildNodeType.Arbitrate(
                node, Options,
                T0 + BuildNodeType.ClaimStaleAfter - TimeSpan.FromMinutes(1),
                new StatusCluster("silo-live", ("silo-joining", SiloStatus.Joining)))
            .Should().BeSameAs(node,
                "Unknown means 'ask the clock', not 'take it' — a redundant arbitration pass over a "
                + "claim whose heartbeat is still inside the budget must write nothing");
    }

    /// <summary>
    /// An ACTIVE holder is still untouchable however ancient its heartbeat looks — #1355's rule,
    /// re-pinned here because this change edits the very mapping that rule reads.
    /// </summary>
    [Fact]
    public void AnActiveHolder_IsStillNeverTakenOver()
    {
        var node = ClaimHeldBy("slow-builder", "silo-active", T0);

        BuildNodeType.Arbitrate(
                node, Options,
                T0 + BuildNodeType.ClaimStaleAfter + TimeSpan.FromHours(1),
                new StatusCluster("silo-live", ("silo-active", SiloStatus.Active)))
            .Should().BeSameAs(node,
                "a stopped heartbeat on a process the cluster sees running means busy or starved, "
                + "never dead — evicting it puts two builders on one compile (#1355)");
    }

    private static MeshNode ClaimHeldBy(string holder, string identity, DateTime beat) =>
        new("Build", "Admin")
        {
            NodeType = BuildNodeType.NodeType,
            Content = new BuildState
            {
                ClaimedBy = holder,
                ClaimedByIdentity = identity,
                ClaimedAt = beat,
                HeartbeatAt = beat,
                Status = BuildStatus.Building,
                RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                    .Add("follower", new BuildClaimRequest("fp-next", beat.AddMinutes(1), "silo-live")),
            }
        };

    /// <summary>
    /// One pod's view of the cluster expressed in the SAME currency Orleans speaks — a
    /// <see cref="SiloStatus"/> per identity, routed through the production classifier. Deliberately
    /// not a hand-written Alive/Gone/Unknown double: the defect lived in the status→state mapping,
    /// so a double that skipped it would assert nothing about the code that broke.
    /// </summary>
    private sealed class StatusCluster(string localIdentity, params (string Identity, SiloStatus Status)[] members)
        : IClusterMembership
    {
        private readonly ImmutableDictionary<string, SiloStatus> members =
            members.ToImmutableDictionary(m => m.Identity, m => m.Status);

        public string LocalIdentity { get; } = localIdentity;

        public ClusterMemberState StateOf(string identity) =>
            identity == LocalIdentity
                ? ClusterMemberState.Alive
                : OrleansClusterMembership.Classify(
                    members.TryGetValue(identity, out var status) ? status : SiloStatus.None);
    }
}
