using System;
using System.Collections.Immutable;

namespace MeshWeaver.Graph.Configuration;

/// <summary>Lifecycle of a build unit — the root build or one chunk.</summary>
public enum BuildStatus
{
    /// <summary>No build has run or been requested on this node yet.</summary>
    None,
    /// <summary>A claimant holds the node and is computing the chunk plan (root only).</summary>
    Planning,
    /// <summary>The build is executing.</summary>
    Building,
    /// <summary>The build completed and its artifacts are usable — for the root, this is the GO signal.</summary>
    Ready,
    /// <summary>The build terminated with a regression or an execution fault; see <see cref="BuildState.Error"/>.</summary>
    Failed,
    /// <summary>The build was cancelled via <see cref="BuildState.RequestedStatus"/>.</summary>
    Cancelled,
}

/// <summary>
/// A pending request to become the builder of a Build node, written by a CANDIDATE into
/// <see cref="BuildState.RequestedClaims"/> under its own holder id. Requests are per-candidate
/// dictionary entries on purpose: cross-hub <c>stream.Update</c> writes travel as RFC 7396 merge
/// patches, so two candidates writing the SAME field would silently last-write-win — distinct keys
/// compose instead. The claim DECISION is taken only by the node's own hub (the arbiter), inside
/// its own serialised <c>Update</c> lambda.
/// </summary>
/// <param name="FrameworkVersion">The framework fingerprint (Graph assembly MVID) the candidate would build for.</param>
/// <param name="RequestedAt">When the candidate registered — the arbiter grants the earliest request first.</param>
/// <param name="ClusterIdentity">
/// The candidate's <c>IClusterMembership.LocalIdentity</c>, or <c>null</c> where there is no cluster
/// (monolith, test, dev box). The arbiter copies it onto
/// <see cref="BuildState.ClaimedByIdentity"/> when it grants, which is what lets a later takeover
/// decision ask membership instead of a clock.
/// </param>
/// <param name="Priority">
/// Election precedence — HIGHER wins before any timestamp is compared (#1424). A dedicated bake
/// process registers at <see cref="BakePriority"/> so it beats every serving pod whenever both
/// are candidates; serving pods register at the default 0 and thereby stand down exactly when a
/// dedicated builder is present, while remaining the fallback when none is.
/// </param>
public sealed record BuildClaimRequest(
    string FrameworkVersion, DateTime RequestedAt, string? ClusterIdentity = null, int Priority = 0)
{
    /// <summary>The election precedence a dedicated bake process registers with.</summary>
    public const int BakePriority = 1;
}

/// <summary>
/// One completed build — the GO record for a framework fingerprint, kept as history on the root's
/// <see cref="BuildState.Ready"/> map. A newer build never revokes an older GO: old-image pods
/// stay ready on their own fingerprint's record throughout a rollout.
/// </summary>
/// <param name="FrameworkVersion">The fingerprint this GO covers.</param>
/// <param name="ReadyAt">When the build reached <see cref="BuildStatus.Ready"/>.</param>
/// <param name="Commits">The source commits the build was pinned to, keyed by module/space path.</param>
/// <param name="Detail">Human-readable summary (chunk counts, timings).</param>
public sealed record BuildGo(
    string FrameworkVersion,
    DateTime ReadyAt,
    ImmutableDictionary<string, string>? Commits = null,
    string? Detail = null);

/// <summary>
/// What a read of the durable build root actually established about one framework fingerprint's GO
/// — THREE states, never two (#3404).
///
/// <para>🚨 The third state exists because a failed read and a real negative are different facts,
/// and code that models only "GO / no GO" has no way to say which it got. A pod that renders "I
/// could not determine" as "there is no GO" refuses a build it never asked about — which is what
/// held two <c>memex-cloud</c> rollouts on 2026-09-06.</para>
/// </summary>
public enum BuildGoWitness
{
    /// <summary>The witness was read and it carries a GO for the requested fingerprint.</summary>
    Go,

    /// <summary>
    /// The witness was READ and it carries no GO for the requested fingerprint — a real negative,
    /// established by an answer. Includes "the durable row does not exist at all": nothing has ever
    /// been built here, which the store answered rather than failed to answer.
    /// </summary>
    NoGo,

    /// <summary>
    /// 🚨 The witness could not be read, so NOTHING was established. This is not a negative and must
    /// never be reported as one: the store may be absent, the read may have faulted, or the row may
    /// have come back in a shape that did not materialize. <see cref="BuildGoReading.Detail"/> always
    /// says which, and <see cref="BuildGoReading.Error"/> carries the fault when there was one.
    /// </summary>
    Undetermined,
}

/// <summary>
/// One reading of the durable build root: <see cref="Witness"/> says what was established, and
/// <see cref="Detail"/> always says how — so a caller can act on the distinction AND report it.
///
/// <para>Deliberately not a <c>BuildGo?</c>. A nullable reference can carry the answer but not the
/// PROVENANCE of the answer, and the provenance is the whole point: the same <c>null</c> licenses a
/// redundant bake (harmless) and a readiness refusal (a held rollout).</para>
/// </summary>
/// <param name="Witness">What this read established.</param>
/// <param name="Go">The GO record, present exactly when <see cref="Witness"/> is <see cref="BuildGoWitness.Go"/>.</param>
/// <param name="Detail">Why the reading says what it says — always present, for the log line and the health payload.</param>
/// <param name="Error">The fault that made the reading undetermined, when there was one.</param>
public sealed record BuildGoReading(
    BuildGoWitness Witness,
    BuildGo? Go,
    string Detail,
    Exception? Error = null)
{
    /// <summary>The witness answered with a GO for the requested fingerprint.</summary>
    public static BuildGoReading Found(BuildGo go) =>
        new(BuildGoWitness.Go, go, $"the durable witness carries the GO recorded at {go.ReadyAt:O}");

    /// <summary>The witness ANSWERED, and the answer is that no GO exists for this fingerprint.</summary>
    public static BuildGoReading NotRecorded(string detail) =>
        new(BuildGoWitness.NoGo, null, detail);

    /// <summary>Nothing was established — see <see cref="BuildGoWitness.Undetermined"/>.</summary>
    public static BuildGoReading Undetermined(string detail, Exception? error = null) =>
        new(BuildGoWitness.Undetermined, null, detail, error);
}

/// <summary>
/// Content of a <c>Build</c> node — the coordination state of the build protocol
/// (<c>Doc/Architecture/BuildCoordination</c>). The ROOT node (<c>Admin/Build</c>) carries the
/// build's identity, the chunk plan and the per-fingerprint GO history; each CHUNK node
/// (<c>Admin/Build/{chunkName}</c>) carries its queries, its claim and the release paths it wrote.
/// All coordination goes through this state via <c>GetMeshNodeStream(path).Update(...)</c> —
/// there is no other channel (no lease files, no request/response types).
/// </summary>
public sealed record BuildState
{
    // ---- shared (root and chunk) ----

    /// <summary>Current lifecycle state. Written only by the claim holder and the arbiter.</summary>
    public BuildStatus Status { get; init; }

    /// <summary>
    /// Control-plane input: an outside writer requests a transition (e.g.
    /// <see cref="BuildStatus.Cancelled"/>) and the owning hub reacts — never writes
    /// <see cref="Status"/> directly.
    /// </summary>
    public BuildStatus? RequestedStatus { get; init; }

    /// <summary>
    /// Candidates waiting to build this node, keyed by holder id. See
    /// <see cref="BuildClaimRequest"/> for why this is a per-candidate map.
    /// </summary>
    public ImmutableDictionary<string, BuildClaimRequest>? RequestedClaims { get; init; }

    /// <summary>The holder currently granted this node, or <c>null</c> when unclaimed.</summary>
    public string? ClaimedBy { get; init; }

    /// <summary>
    /// The holder's cluster-membership identity, copied from its
    /// <see cref="BuildClaimRequest.ClusterIdentity"/> at grant time. This — not
    /// <see cref="ClaimedBy"/> — is what the arbiter hands to
    /// <c>IClusterMembership.StateOf</c>, because it is the implementation's own opaque
    /// identity and round-trips exactly, while a holder id is a human-facing label.
    /// <c>null</c> means "no cluster said anything", which resolves to
    /// <c>ClusterMemberState.Unknown</c> and leaves the heartbeat clock in charge.
    /// </summary>
    public string? ClaimedByIdentity { get; init; }

    /// <summary>When the current claim was granted.</summary>
    public DateTime? ClaimedAt { get; init; }

    /// <summary>
    /// The holder's liveness stamp — the FALLBACK liveness signal, not the primary one. Where the
    /// cluster can resolve <see cref="ClaimedByIdentity"/> its verdict decides takeover outright
    /// (gone → steal now, alive → never steal); the heartbeat clock governs only when membership
    /// has no opinion. See <c>BuildNodeType.Arbitrate</c>.
    /// </summary>
    public DateTime? HeartbeatAt { get; init; }

    /// <summary>Terminal failure detail when <see cref="Status"/> is <see cref="BuildStatus.Failed"/>.</summary>
    public string? Error { get; init; }

    // ---- root only ----

    /// <summary>The framework fingerprint the in-flight build targets (Graph assembly MVID).</summary>
    public string? FrameworkVersion { get; init; }

    /// <summary>Chunk names of the current plan; each addresses a child Build node at <c>{root}/{name}</c>.</summary>
    public ImmutableList<string>? Chunks { get; init; }

    /// <summary>
    /// Per-fingerprint GO history — THE readiness signal. A silo is ready when this map holds its
    /// own fingerprint. Entries are only ever added; completing a new build never removes an old GO.
    /// </summary>
    public ImmutableDictionary<string, BuildGo>? Ready { get; init; }

    // ---- chunk only ----

    /// <summary>
    /// The mesh queries defining this chunk — as simple as a list of paths, or a module such as
    /// <c>namespace:MyPlugin scope:subtree nodeType:Code</c>.
    /// </summary>
    public ImmutableList<string>? Queries { get; init; }

    /// <summary>Source commits this chunk's compile is pinned to, keyed by module/space path.</summary>
    public ImmutableDictionary<string, string>? Commits { get; init; }

    /// <summary>
    /// The paths this chunk's build wrote — the release node paths
    /// (<c>{nodeTypePath}/Release/{version}</c>) its compiles minted. Releases remain the system of
    /// record for artifacts; the chunk records which ones this build produced.
    /// </summary>
    public ImmutableList<string>? WrittenPaths { get; init; }

    /// <summary>Path of the <c>_Activity</c> node executing this chunk's build.</summary>
    public string? ActivityPath { get; init; }
}
