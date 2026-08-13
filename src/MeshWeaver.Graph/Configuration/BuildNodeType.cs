using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers <c>Build</c> as a first-class NodeType — the coordination nodes of the build
/// protocol (<c>Doc/Architecture/BuildCoordination</c>). The ROOT lives at
/// <see cref="RootPath"/>; each CHUNK at <c>Admin/Build/{chunkName}</c>. Both carry a
/// <see cref="BuildState"/> payload.
///
/// <para><b>Who becomes the build master.</b> Nobody is elected. Candidates register a
/// <see cref="BuildClaimRequest"/> under their own holder id in
/// <see cref="BuildState.RequestedClaims"/> (per-candidate keys — RFC 7396 merge-safe), and the
/// node's OWN hub arbitrates: <see cref="InstallClaimArbiter"/> grants the earliest pending
/// request inside a serialised <c>Update</c> lambda, and steals a claim whose heartbeat has gone
/// stale. Correctness comes from node state, never from an in-memory gate.</para>
/// </summary>
public static class BuildNodeType
{
    /// <summary>The node-type identifier string for Build nodes.</summary>
    public const string NodeType = "Build";

    /// <summary>The build root — the node whose <see cref="BuildState.Ready"/> map is the GO signal.</summary>
    public const string RootPath = "Admin/Build";

    /// <summary>
    /// How long a claim survives without a heartbeat before the arbiter hands it to the next
    /// candidate. Generously larger than <see cref="HeartbeatInterval"/>: a missed beat under load
    /// must never produce two concurrent builders — the exact storm the claim exists to prevent.
    /// </summary>
    public static readonly TimeSpan ClaimStaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>How often a claim holder refreshes <see cref="BuildState.HeartbeatAt"/> while building.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Registers the Build node type on the mesh builder by adding its MeshNode definition.
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder to configure.</param>
    /// <returns>The same builder, to allow fluent chaining.</returns>
    public static TBuilder AddBuildType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>
    /// Builds the MeshNode definition for the Build node type: content payload, no UI create
    /// (only the protocol writes these nodes), and the claim arbiter installed on every
    /// Build node's own hub.
    /// </summary>
    /// <returns>The Build MeshNode definition.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Build",
        NodeType = MeshNode.NodeTypePath,   // this MeshNode IS a NodeType definition
        Icon = "/static/NodeTypeIcons/task-list.svg",
        ExcludeFromContext = new HashSet<string> { "create" }, // no UI create — only the build protocol writes these
        Content = new NodeTypeDefinition
        {
            Description = "Build coordination node. The root carries the chunk plan and the per-fingerprint GO signal; each chunk carries its queries, its claim and the release paths it wrote.",
        },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<BuildState>())
            .AddDefaultLayoutAreas()
            .WithInitialization(hub => hub.RegisterForDisposal(InstallClaimArbiter(hub)))
    };

    /// <summary>
    /// The claim arbiter — runs on each Build node's OWN hub, where <c>Update</c> lambdas are
    /// serialised by the action block, so the grant check and the grant write are one atomic step.
    ///
    /// <para>Two triggers, one decision procedure: every own-stream emission (a candidate
    /// registered, a holder released) and a slow periodic tick (a dead holder emits nothing — the
    /// stale steal can only come from a timer). Both funnel into <see cref="Arbitrate"/>, which
    /// re-reads state inside the lambda and returns the node UNCHANGED when there is nothing to
    /// do, so redundant triggers write nothing.</para>
    /// </summary>
    /// <param name="hub">The Build node's own hub.</param>
    /// <returns>The subscription to dispose with the hub.</returns>
    internal static IDisposable InstallClaimArbiter(IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<MeshNode>>();
        var workspace = hub.GetWorkspace();

        void TryArbitrate() =>
            workspace.GetMeshNodeStream()
                .Update(node => Arbitrate(node, hub.JsonSerializerOptions, DateTime.UtcNow))
                .Subscribe(
                    _ => { },
                    ex => logger?.LogWarning(
                        ex, "Build claim arbitration failed on {Address}", hub.Address));

        var onEmission = ActivityControlPlaneExtensions.SubscribeWithReEstablish(
            () => workspace.GetMeshNodeStream()
                .Select(node => node?.ContentAs<BuildState>(hub.JsonSerializerOptions))
                .Where(state => state?.RequestedClaims is { Count: > 0 })
                .Select(state => string.Join(
                    "|",
                    state!.RequestedClaims!.Keys.OrderBy(k => k, StringComparer.Ordinal)
                        .Append(state.ClaimedBy ?? string.Empty)))
                .DistinctUntilChanged(),
            _ => TryArbitrate(),
            hub.Address,
            logger,
            "build claim arbiter");

        // A holder that died emits nothing — staleness can only be observed by a timer. The tick
        // is cheap by construction: Arbitrate returns the node unchanged unless a pending claim
        // exists AND the current claim is absent or stale, so a quiet node writes nothing.
        var staleTick = Observable.Interval(HeartbeatInterval)
            .Subscribe(_ => TryArbitrate());

        return new System.Reactive.Disposables.CompositeDisposable(onEmission, staleTick);
    }

    /// <summary>
    /// The single decision procedure, executed inside the owning hub's serialised
    /// <c>Update</c> lambda. Grants the earliest pending claim when the node is unclaimed or the
    /// current claim's heartbeat is stale; otherwise returns the node unchanged. Pure over its
    /// inputs so the staleness rules are testable without waiting on wall-clock.
    /// </summary>
    /// <param name="node">The Build node as read inside the update lambda.</param>
    /// <param name="options">Serializer options for content recovery.</param>
    /// <param name="now">The decision instant.</param>
    /// <returns>The node with a granted claim, or the node unchanged.</returns>
    public static MeshNode Arbitrate(
        MeshNode node, System.Text.Json.JsonSerializerOptions options, DateTime now)
    {
        if (node is null) return node!;
        var state = node.ContentAs<BuildState>(options);
        if (state?.RequestedClaims is not { Count: > 0 } pending)
            return node;

        var lastBeat = state.HeartbeatAt ?? state.ClaimedAt;
        var claimLive = state.ClaimedBy is not null
            && state.Status is BuildStatus.Planning or BuildStatus.Building
            && lastBeat is { } beat
            && now - beat <= ClaimStaleAfter;
        if (claimLive)
            return node;

        var granted = pending
            .OrderBy(kv => kv.Value.RequestedAt)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .First();

        return node with
        {
            Content = state with
            {
                ClaimedBy = granted.Key,
                ClaimedAt = now,
                HeartbeatAt = now,
                FrameworkVersion = granted.Value.FrameworkVersion,
                Status = BuildStatus.Planning,
                Error = null,
                RequestedClaims = state.RequestedClaims!.Remove(granted.Key),
            }
        };
    }

}
