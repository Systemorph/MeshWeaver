using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The in-box <see cref="INodeTypeInstanceLocations"/>: the <c>nodeType → locations</c> projection
/// of <see cref="NodeTypeDefinition.InstanceLocations"/> (#3039), one instance per mesh.
///
/// <para><b>Two lanes, both fail-open.</b> The STATIC lane folds the definitions registered on the
/// mesh builder (<c>AddMeshNodes</c> / <c>IStaticNodeProvider</c>) — the same registered-node seam
/// <see cref="MeshConfiguration"/> derives <c>IsSatelliteNodeType</c> from, computed here because
/// <see cref="NodeTypeDefinition"/> is not visible from <c>MeshWeaver.Mesh.Contract</c>. The
/// DYNAMIC lane is fed by each definition node's OWN hub while it is live on this process
/// (<see cref="PublishFrom"/>, installed by <see cref="NodeTypeNodeType"/>'s HubConfiguration): an
/// entry appears when the hub reads its node, follows every edit of the declaration, and is
/// removed when the hub is disposed or its stream faults — so this projection can never hold a
/// STALE declaration, which is the one shape that would lose rows (an under-stated declaration).
/// A definition whose hub lives on another silo is simply unknown here and its queries fan out in
/// full: slow, never partial.</para>
///
/// <para>🚨 <b>The static fold is also the authoring gate for in-process declarations.</b> A static
/// definition that declares locations for a type the permission fold enumerates mesh-wide
/// (<see cref="NeverNarrowedNodeTypes"/>) throws, naming the type and the reason — the loudest
/// possible red for a shape that has no write boundary to refuse it at.
/// <see cref="InstanceLocationDeclarationValidator"/> is the same gate for authored and installed
/// declarations.</para>
/// </summary>
public sealed class NodeTypeInstanceLocations : INodeTypeInstanceLocations
{
    private readonly Lazy<ImmutableDictionary<string, IReadOnlyList<string>>> _static;
    private ImmutableDictionary<string, IReadOnlyList<string>> _dynamic =
        ImmutableDictionary.Create<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the projection over <paramref name="services"/>' static nodes. The fold runs on first
    /// use, not here — the singleton is resolved while the mesh is still being composed.
    /// </summary>
    /// <param name="services">The mesh's root service provider.</param>
    public NodeTypeInstanceLocations(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _static = new(
            () => FromStaticNodes(
                services.EnumerateStaticNodes(),
                services.GetService<IMessageHub>()?.JsonSerializerOptions ?? JsonSerializerOptions.Default,
                NeverNarrowedNodeTypes.GatedNodeTypesOf(services.GetRequiredService<MeshConfiguration>())),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public IReadOnlyList<string>? LocationsFor(string nodeType)
    {
        if (string.IsNullOrEmpty(nodeType))
            return null;
        // A static definition shadows a durable row of the same path everywhere else in the mesh;
        // it does here too.
        if (_static.Value.TryGetValue(nodeType, out var declared))
            return declared;
        return _dynamic.TryGetValue(nodeType, out declared) ? declared : null;
    }

    /// <summary>
    /// Records (or, for a null/empty <paramref name="locations"/>, forgets) the declaration of the
    /// definition at <paramref name="nodeTypePath"/>. Called by the definition's own hub.
    /// </summary>
    /// <param name="nodeTypePath">The definition node's path — the name instances carry as their type.</param>
    /// <param name="locations">The declared locations, or null when the definition declares none.</param>
    public void Record(string nodeTypePath, IReadOnlyList<string>? locations)
    {
        if (string.IsNullOrEmpty(nodeTypePath))
            return;
        if (locations is not { Count: > 0 })
        {
            Forget(nodeTypePath);
            return;
        }
        var snapshot = (IReadOnlyList<string>)locations.ToImmutableArray();
        ImmutableInterlocked.AddOrUpdate(ref _dynamic, nodeTypePath, snapshot, (_, _) => snapshot);
    }

    /// <summary>
    /// Removes the dynamic entry for <paramref name="nodeTypePath"/> — the definition's hub is gone
    /// (or its stream faulted), so nothing on this process is keeping the declaration current.
    /// </summary>
    /// <param name="nodeTypePath">The definition node's path.</param>
    public void Forget(string nodeTypePath)
    {
        if (!string.IsNullOrEmpty(nodeTypePath))
            ImmutableInterlocked.TryRemove(ref _dynamic, nodeTypePath, out _);
    }

    /// <summary>
    /// The static fold: every NodeType definition among <paramref name="staticNodes"/> that declares
    /// <see cref="NodeTypeDefinition.InstanceLocations"/>, keyed by its path. Pure, so a test can run
    /// it over a hand-built set.
    /// </summary>
    /// <param name="staticNodes">The mesh's static nodes.</param>
    /// <param name="options">Serializer options for reading content that arrived untyped.</param>
    /// <param name="gatedNodeTypes">The mesh's type-declared gates, or null when none.</param>
    /// <returns>The declared locations by node type, compared case-insensitively.</returns>
    /// <exception cref="InvalidOperationException">
    /// A static definition declares locations for a type the permission fold enumerates mesh-wide
    /// (<see cref="NeverNarrowedNodeTypes"/>); the message names the type and the reason.
    /// </exception>
    public static ImmutableDictionary<string, IReadOnlyList<string>> FromStaticNodes(
        IEnumerable<MeshNode> staticNodes,
        JsonSerializerOptions options,
        IReadOnlySet<string>? gatedNodeTypes)
    {
        ArgumentNullException.ThrowIfNull(staticNodes);
        var builder = ImmutableDictionary.CreateBuilder<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in staticNodes)
        {
            if (!ImportWriteOrder.IsNodeTypeDefinition(node))
                continue;
            var locations = node.ContentAs<NodeTypeDefinition>(options)?.InstanceLocations;
            if (locations is not { Count: > 0 })
                continue;
            var refusal = InstanceLocationDeclarationValidator.Refusal(node, locations, gatedNodeTypes);
            if (refusal is not null)
                throw new InvalidOperationException(refusal);
            builder[node.Path] = locations.ToImmutableArray();
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// The dynamic lane, installed on a NodeType definition's OWN hub at its init turn: mirrors the
    /// node's <see cref="NodeTypeDefinition.InstanceLocations"/> into this mesh's projection for as
    /// long as the hub — and therefore the stream keeping the entry current — is alive. Returns the
    /// subscription to register for disposal with the hub; disposing it forgets the entry.
    /// </summary>
    /// <param name="hub">The definition node's own hub.</param>
    /// <returns>The subscription coupling the entry's lifetime to the hub's.</returns>
    public static IDisposable PublishFrom(IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var projection = hub.ServiceProvider.GetService<NodeTypeInstanceLocations>();
        if (projection is null)
            return Disposable.Empty;
        var logger = hub.ServiceProvider.GetService<ILogger<NodeTypeInstanceLocations>>();
        string? published = null;
        var subscription = hub.GetWorkspace().GetMeshNodeStream()
            .Where(node => node is not null)
            .Subscribe(
                node =>
                {
                    published = node!.Path;
                    projection.Record(
                        node.Path,
                        node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.InstanceLocations);
                },
                ex =>
                {
                    // A faulted stream keeps nothing current: drop the entry rather than serve a
                    // declaration nobody is maintaining. The query then fans out — slow, never partial.
                    logger?.LogDebug(ex, "Instance-location stream faulted on {Address}; declaration forgotten", hub.Address);
                    if (published is { } path)
                        projection.Forget(path);
                });
        return Disposable.Create(() =>
        {
            subscription.Dispose();
            if (published is { } path)
                projection.Forget(path);
        });
    }
}
