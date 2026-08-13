using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// The client surface of the build protocol (<c>Doc/Architecture/BuildCoordination</c>).
/// Everything is durable node state on Build nodes (<see cref="BuildNodeType.RootPath"/> and its
/// chunk children), written through <c>GetMeshNodeStream(path).Update(...)</c> and observed
/// through the same stream — no lease files, no request/response types, no in-memory gates.
///
/// <para>Claims: a candidate REGISTERS (<see cref="RequestBuildClaim"/>, a merge-safe
/// per-candidate map entry) and the node's own hub GRANTS (the arbiter installed by
/// <see cref="BuildNodeType"/>); the candidate then observes the grant on the stream
/// (<see cref="ObserveBuildClaim"/>). Holder-side writes go through
/// <see cref="UpdateBuildAsHolder"/>, which no-ops unless the caller still holds the claim —
/// a builder that lost its claim to staleness cannot corrupt its successor's state.</para>
///
/// <para>🚨 <b>Who holds the build is decided on the claim LOCK</b>
/// (<see cref="BuildNodeType.ClaimPath"/>), not on the Build node — a mirror is per-cluster and is
/// flushed wholesale, so a field on it cannot be exclusive across clusters (#1424). The lock is
/// touched by exactly three operations, all conditional: the arbiter's compare-and-set grant,
/// <see cref="BeatBuildClaim"/>'s stamp refresh, and <see cref="ReleaseBuildClaim"/>. Everything
/// else on this surface is ordinary node state.</para>
/// </summary>
public static class BuildCoordinationExtensions
{
    /// <summary>
    /// Ensures a Build node exists at <paramref name="path"/> — the "materialize once" step.
    /// Idempotent under concurrency: a lost create race resolves by re-reading the node the
    /// winner created; any other create fault propagates.
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="path">Node path; defaults to the build root.</param>
    /// <param name="initial">Initial content when this call creates the node.</param>
    /// <returns>Cold observable emitting the existing or newly created node.</returns>
    public static IObservable<MeshNode> EnsureBuildNode(
        this IMessageHub hub, string path = BuildNodeType.RootPath, BuildState? initial = null)
    {
        // Create-first, no existence pre-read: a stream subscribe on a MISSING node surfaces a
        // routing NotFound error (MeshNodeStreamCache.IsMissingNodeFailure), so probing before
        // creating is exactly backwards. "Already exists" — whether from an earlier boot or a
        // concurrent Ensure losing the race — IS the success case: the node this call must
        // guarantee is there. Any other create fault propagates.
        var workspace = hub.GetWorkspace();
        return hub.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(NewBuildNode(path, initial))
            .Catch((Exception ex) =>
                ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                    ? workspace.GetMeshNodeStream(path).Where(n => n is not null).Take(1)
                    : Observable.Throw<MeshNode>(ex));
    }

    /// <summary>
    /// Registers <paramref name="holder"/> as a claim candidate on the Build node. The entry is
    /// keyed by holder id (merge-safe across concurrent candidates); an existing registration
    /// keeps its original <see cref="BuildClaimRequest.RequestedAt"/> so re-requests cannot jump
    /// the queue. The grant arrives on the stream — observe it with <see cref="ObserveBuildClaim"/>.
    ///
    /// <para>The registration also carries this candidate's cluster-membership identity, taken from
    /// the calling hub's <see cref="IClusterMembership"/> when there is one. That is what lets the
    /// arbiter later ask the cluster whether this holder is still running, instead of aging a
    /// heartbeat — see <c>BuildNodeType.Arbitrate</c>. No cluster (monolith, test, dev) simply
    /// stamps nothing and keeps the clock.</para>
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="holder">This candidate's unique id (e.g. pod name + process guid).</param>
    /// <param name="frameworkVersion">The framework fingerprint the candidate would build for.</param>
    /// <param name="path">Node path; defaults to the build root.</param>
    /// <returns>Cold observable emitting the node after the registration write.</returns>
    public static IObservable<MeshNode> RequestBuildClaim(
        this IMessageHub hub, string holder, string frameworkVersion,
        string path = BuildNodeType.RootPath, int priority = 0)
    {
        var identity = hub.ServiceProvider.GetService<IClusterMembership>()?.LocalIdentity;
        return hub.EnsureBuildNode(path)
            .SelectMany(_ => hub.GetWorkspace().GetMeshNodeStream(path).Update(curr =>
            {
                if (curr is null) return curr!;
                var state = curr.ContentAs<BuildState>(hub.JsonSerializerOptions) ?? new BuildState();
                if (state.ClaimedBy == holder) return curr;
                if (state.RequestedClaims?.ContainsKey(holder) == true) return curr;
                return curr with
                {
                    Content = state with
                    {
                        RequestedClaims = (state.RequestedClaims
                            ?? ImmutableDictionary<string, BuildClaimRequest>.Empty)
                            .SetItem(holder, new BuildClaimRequest(
                                frameworkVersion, DateTime.UtcNow, identity, priority)),
                    }
                };
            }));
    }

    /// <summary>
    /// Emits the node's <see cref="BuildState"/> each time <paramref name="holder"/> holds the
    /// claim — the first emission is the grant. Callers bound the wait with <c>.Timeout(...)</c>.
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="holder">The candidate id passed to <see cref="RequestBuildClaim"/>.</param>
    /// <param name="path">Node path; defaults to the build root.</param>
    /// <returns>Hot observable of granted states.</returns>
    public static IObservable<BuildState> ObserveBuildClaim(
        this IMessageHub hub, string holder, string path = BuildNodeType.RootPath)
        => hub.GetWorkspace().GetMeshNodeStream(path)
            .Select(node => node?.ContentAs<BuildState>(hub.JsonSerializerOptions))
            .Where(state => state?.ClaimedBy == holder)
            .Select(state => state!);

    /// <summary>
    /// Applies <paramref name="change"/> to the node's state ONLY while <paramref name="holder"/>
    /// still holds the claim; otherwise the write is a no-op. This is the single primitive behind
    /// every holder-side transition (heartbeat, chunk plan, completion, failure), and the property
    /// that makes stale-claim stealing safe: a superseded builder's late writes land on nothing.
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="holder">The claim holder performing the write.</param>
    /// <param name="change">State transition to apply.</param>
    /// <param name="path">Node path; defaults to the build root.</param>
    /// <returns>Cold observable emitting the node after the write.</returns>
    public static IObservable<MeshNode> UpdateBuildAsHolder(
        this IMessageHub hub, string holder, Func<BuildState, BuildState> change,
        string path = BuildNodeType.RootPath)
        => hub.GetWorkspace().GetMeshNodeStream(path).Update(curr =>
        {
            var state = curr?.ContentAs<BuildState>(hub.JsonSerializerOptions);
            if (curr is null || state?.ClaimedBy != holder) return curr!;
            return curr with { Content = change(state) };
        });

    /// <summary>
    /// Refreshes the holder's liveness stamp. Call on <see cref="BuildNodeType.HeartbeatInterval"/>.
    ///
    /// <para>Two writes, one meaning: the visible stamp on the Build node, and — the load-bearing
    /// one — the stamp on the claim LOCK, which is what every cluster's arbiter ages when membership
    /// has no opinion about the holder (across clusters it never has). The lock write is a
    /// compare-and-set that no-ops unless this holder still owns the lock, so a superseded builder's
    /// heartbeat cannot keep a claim it no longer has alive.</para>
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="holder">The claim holder.</param>
    /// <param name="path">Node path; defaults to the build root.</param>
    /// <returns>Cold observable emitting the node after the write.</returns>
    public static IObservable<MeshNode> BeatBuildClaim(
        this IMessageHub hub, string holder, string path = BuildNodeType.RootPath)
        => hub.UpdateBuildAsHolder(holder, s => s with { HeartbeatAt = DateTime.UtcNow }, path)
            .SelectMany(node => OnOwnLock(
                    hub, holder, path,
                    (storage, held, state, options) => storage.WriteIfVersion(
                        held with
                        {
                            Content = state with { HeartbeatAt = DateTime.UtcNow },
                            Version = MeshNode.NextVersion(held.Version),
                        },
                        held.Version,
                        options))
                .Select(_ => node));

    /// <summary>
    /// Releases the claim LOCK — the step that lets any cluster's arbiter grant the next candidate.
    /// A no-op unless <paramref name="holder"/> still owns the lock, so a superseded builder's
    /// completion cannot release its successor's claim.
    ///
    /// <para><see cref="CompleteBuild"/> and <see cref="FailBuild"/> chain this automatically;
    /// call it directly only where a claim is released WITHOUT going through them — the chunk
    /// close-out in <c>BuildProtocolDriver</c>, which clears the claim fields itself.</para>
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="holder">The claim holder releasing the build.</param>
    /// <param name="path">The Build node (root or chunk) whose claim is released.</param>
    /// <returns>Cold observable completing when the lock is gone (or was never ours).</returns>
    public static IObservable<System.Reactive.Unit> ReleaseBuildClaim(
        this IMessageHub hub, string holder, string path)
        => OnOwnLock(
            hub, holder, path,
            (storage, held, _, _) => storage.DeleteIfExists(held.Path).Select(_ => (bool?)true));

    /// <summary>
    /// Reads the claim lock for <paramref name="path"/> and runs <paramref name="act"/> only while
    /// <paramref name="holder"/> still owns it. The read-then-act window is safe because every
    /// action is itself conditional (a compare-and-set on the version read, or a rowcount-gated
    /// delete) — the lock's whole point is that no write to it is unconditional.
    /// </summary>
    private static IObservable<System.Reactive.Unit> OnOwnLock(
        IMessageHub hub,
        string holder,
        string path,
        Func<IStorageAdapter, MeshNode, BuildState, JsonSerializerOptions, IObservable<bool?>> act)
    {
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return Observable.Return(System.Reactive.Unit.Default);
        var options = hub.JsonSerializerOptions;
        return storage.Read(BuildNodeType.ClaimPath(path), options)
            .Take(1)
            .SelectMany(held =>
            {
                var state = held?.ContentAs<BuildState>(options);
                return held is null || state?.ClaimedBy != holder
                    ? Observable.Return(System.Reactive.Unit.Default)
                    : act(storage, held, state, options).Select(_ => System.Reactive.Unit.Default);
            });
    }

    /// <summary>
    /// Completes the build: records the GO for <paramref name="go"/>'s fingerprint on the
    /// per-fingerprint history (never removing an older GO) and releases the claim, which lets the
    /// arbiter grant the next pending candidate.
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="holder">The claim holder completing the build.</param>
    /// <param name="go">The GO record for the fingerprint that was built.</param>
    /// <param name="path">Node path; defaults to the build root.</param>
    /// <returns>Cold observable emitting the node after the write.</returns>
    public static IObservable<MeshNode> CompleteBuild(
        this IMessageHub hub, string holder, BuildGo go, string path = BuildNodeType.RootPath)
        => hub.UpdateBuildAsHolder(
                holder,
                s => s with
                {
                    Status = BuildStatus.Ready,
                    Ready = (s.Ready ?? ImmutableDictionary<string, BuildGo>.Empty)
                        .SetItem(go.FrameworkVersion, go),
                    ClaimedBy = null,
                    ClaimedByIdentity = null,
                    ClaimedAt = null,
                    HeartbeatAt = null,
                },
                path)
            // …and drop the LOCK, which is what actually frees the build for the next candidate in
            // ANY cluster. Clearing ClaimedBy on the node alone would only free it here.
            .SelectMany(node => hub.ReleaseBuildClaim(holder, path).Select(_ => node));

    /// <summary>
    /// Fails the build: records the error and releases the claim. The fingerprint gets NO GO —
    /// readiness for that image stays refused, which is the fail-closed behaviour the probe
    /// contract requires.
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="holder">The claim holder failing the build.</param>
    /// <param name="error">What failed — lands on <see cref="BuildState.Error"/>.</param>
    /// <param name="path">Node path; defaults to the build root.</param>
    /// <returns>Cold observable emitting the node after the write.</returns>
    public static IObservable<MeshNode> FailBuild(
        this IMessageHub hub, string holder, string error, string path = BuildNodeType.RootPath)
        => hub.UpdateBuildAsHolder(
                holder,
                s => s with
                {
                    Status = BuildStatus.Failed,
                    Error = error,
                    ClaimedBy = null,
                    ClaimedByIdentity = null,
                    ClaimedAt = null,
                    HeartbeatAt = null,
                },
                path)
            // A failed build releases the claim exactly like a completed one — the fingerprint gets
            // no GO, but the build must not stay locked to a holder that has stopped.
            .SelectMany(node => hub.ReleaseBuildClaim(holder, path).Select(_ => node));

    /// <summary>
    /// THE readiness primitive: emits the GO record once the build root's history covers
    /// <paramref name="frameworkVersion"/>. A silo computes its own fingerprint locally (its Graph
    /// assembly MVID), subscribes here, and holds its health probe not-ready until the emission.
    /// Old-image silos observe their own fingerprint's earlier GO and stay ready throughout a
    /// rollout.
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="frameworkVersion">The fingerprint to wait for.</param>
    /// <returns>Hot observable emitting the GO record (first emission = go).</returns>
    public static IObservable<BuildGo> ObserveBuildGo(this IMessageHub hub, string frameworkVersion)
        => hub.GetWorkspace().GetMeshNodeStream(BuildNodeType.RootPath)
            .Select(node => node?.ContentAs<BuildState>(hub.JsonSerializerOptions))
            .Select(state => state?.Ready is { } ready
                && ready.TryGetValue(frameworkVersion, out var go) ? go : null)
            .Where(go => go is not null)
            .Select(go => go!)
            .DistinctUntilChanged();

    private static MeshNode NewBuildNode(string path, BuildState? initial)
    {
        var split = path.LastIndexOf('/');
        if (split <= 0 || split == path.Length - 1)
            throw new ArgumentException(
                $"Build node path '{path}' must contain a namespace and an id.", nameof(path));
        return new MeshNode(path[(split + 1)..], path[..split])
        {
            NodeType = BuildNodeType.NodeType,
            Content = initial ?? new BuildState(),
        };
    }
}
