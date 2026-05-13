using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Builds a reactive list of <see cref="CreatableTypeInfo"/> for a given
/// navigation context. Canonical replacement for the legacy
/// <c>INodeTypeService.GetCreatableTypesAsync</c>.
///
/// <para>Queries run against <see cref="IMeshQueryCore"/> directly so the
/// "what types exist?" lookup is NOT access-control-filtered. The list
/// of candidate types is independent of the user's permissions on
/// individual instances — visibility of an instance has nothing to do
/// with whether a type is offered to a user who has Create permission
/// at <paramref name="nodePath"/>. The Create-permission gate runs at
/// the outer level via <see cref="ISecurityService.HasPermission"/>.</para>
///
/// <para>Sources merged into the result (deduped by NodeType path):</para>
/// <list type="number">
///   <item>NodeType MeshNodes returned by the query (dynamic NodeTypes
///     persisted as <c>nodeType:NodeType</c> rows + static NodeTypes
///     surfaced by <see cref="IStaticNodeProvider"/>).</item>
///   <item>Static <see cref="MeshConfiguration.Nodes"/> entries with
///     <c>NodeType = "NodeType"</c> — for AddMeshNodes registrations
///     that aren't persisted (built-in types like Markdown, Thread).</item>
///   <item>Explicit <c>CreatableTypes</c> JSON on the parent NodeType
///     definition (read from
///     <see cref="NodeTypeDefinition.CreatableTypes"/>).</item>
///   <item><see cref="MeshConfiguration.GlobalCreatableTypes"/> when
///     <c>IncludeGlobalTypes</c> on the parent's NodeTypeDefinition is
///     true (default).</item>
/// </list>
/// </summary>
internal sealed class CreatableTypesProvider(
    IMessageHub hub,
    MeshConfiguration meshConfiguration,
    ISecurityService? securityService = null) : ICreatableTypesProvider
{
    public IObservable<IReadOnlyList<CreatableTypeInfo>> GetCreatableTypes(
        string? nodePath, MeshNode? parentNode)
    {
        var meshQueryCore = hub.ServiceProvider.GetService<IMeshQueryCore>();
        var currentType = parentNode?.NodeType;

        var typeNodesObs = meshQueryCore is null
            ? Observable.Return<IReadOnlyList<MeshNode>>([])
            : QueryTypeNodes(meshQueryCore, nodePath, currentType);

        var typesObs = typeNodesObs.Select(typeNodes =>
            BuildInfos(typeNodes, meshConfiguration, currentType));

        // Outer security gate: only apply Create-permission filter for a
        // specific parent path. Root listing (nodePath == "") is global
        // metadata — anyone navigating to "Create" at root sees the full
        // type set; the Create operation itself is gated at the receiver.
        if (securityService is null || string.IsNullOrEmpty(nodePath))
            return typesObs;

        return securityService.HasPermission(nodePath, Permission.Create)
            .CombineLatest(typesObs, (canCreate, types) =>
                canCreate ? types : (IReadOnlyList<CreatableTypeInfo>)[]);
    }

    /// <summary>
    /// Run the right shape of query for the given <paramref name="nodePath"/>
    /// + <paramref name="currentType"/> against <see cref="IMeshQueryCore"/>
    /// — no access control on the result set. Path-deduped via
    /// <c>ImmutableDictionary&lt;string, MeshNode&gt;</c> Scan.
    /// </summary>
    private static IObservable<IReadOnlyList<MeshNode>> QueryTypeNodes(
        IMeshQueryCore meshQueryCore, string? nodePath, string? currentType)
    {
        var queries = BuildQueries(nodePath, currentType);
        if (queries.Length == 0)
            return Observable.Return<IReadOnlyList<MeshNode>>([]);

        var observables = queries.Select(q => meshQueryCore
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(q), options: null!)
            .Take(1)
            .Catch<QueryResultChange<MeshNode>, Exception>(
                _ => Observable.Empty<QueryResultChange<MeshNode>>()));

        // Aggregate (not Scan) — Scan only emits per input, so an empty
        // result-set produces zero emissions and the downstream CombineLatest
        // gate stays closed forever. Aggregate emits the final accumulator
        // exactly once at OnCompleted; for an empty stream that's the seed.
        return Observable.Merge(observables)
            .SelectMany(change => change.Items)
            .Aggregate(
                ImmutableDictionary<string, MeshNode>.Empty
                    .WithComparers(StringComparer.OrdinalIgnoreCase),
                (acc, node) => acc.ContainsKey(node.Path) ? acc : acc.Add(node.Path, node))
            .Select(acc => (IReadOnlyList<MeshNode>)acc.Values.ToArray());
    }

    private static string[] BuildQueries(string? nodePath, string? currentType)
    {
        if (string.IsNullOrEmpty(nodePath))
        {
            // Root listing: no namespace bound. Surface every NodeType
            // definition so the create UI can offer the full menu.
            return ["nodeType:NodeType"];
        }

        // Q1: NodeTypes along the ancestor chain of <myself> — picks up
        // types defined under any namespace in the path's hierarchy.
        var list = new List<string>(2)
        {
            $"nodeType:NodeType scope:selfAndAncestors namespace:{nodePath}",
        };
        // Q2 (when applicable): NodeTypes under the parent's NodeType so
        // an instance can offer the children its type defines (e.g. an
        // ACME/Project instance can create ACME/Project/Todo).
        if (!string.IsNullOrEmpty(currentType)
            && !string.Equals(currentType, MeshNode.NodeTypePath, StringComparison.Ordinal))
        {
            list.Add($"namespace:{currentType} nodeType:NodeType");
        }
        return list.ToArray();
    }

    private static IReadOnlyList<CreatableTypeInfo> BuildInfos(
        IReadOnlyList<MeshNode> queryNodes,
        MeshConfiguration meshConfiguration,
        string? currentType)
    {
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CreatableTypeInfo>();

        // 1. Query-returned NodeTypes (already deduped by Path upstream).
        foreach (var typeNode in queryNodes)
        {
            if (!added.Add(typeNode.Path)) continue;
            result.Add(BuildInfoFromMeshNode(typeNode));
        }

        // 2. Static AddMeshNodes-registered NodeType MeshNodes that aren't
        //    persisted (don't show up in the query). Filter on
        //    NodeType == MeshNode.NodeTypePath to grab only NodeType
        //    definitions, not arbitrary static nodes.
        foreach (var (path, typeNode) in meshConfiguration.Nodes)
        {
            if (!string.Equals(typeNode.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal))
                continue;
            if (!added.Add(path)) continue;
            result.Add(BuildInfoFromMeshNode(typeNode));
        }

        // 3. JSON-based CreatableTypes from the parent's NodeType definition.
        var includeGlobal = true;
        if (!string.IsNullOrEmpty(currentType)
            && !string.Equals(currentType, MeshNode.NodeTypePath, StringComparison.Ordinal)
            && meshConfiguration.Nodes.TryGetValue(currentType, out var parentTypeNode)
            && parentTypeNode.Content is NodeTypeDefinition parentDef)
        {
            includeGlobal = parentDef.IncludeGlobalTypes;
            if (parentDef.CreatableTypes is not null)
            {
                foreach (var typePath in parentDef.CreatableTypes)
                {
                    if (!added.Add(typePath)) continue;
                    var info = BuildInfoFromConfig(typePath, meshConfiguration);
                    if (info is not null) result.Add(info);
                }
            }
        }

        // 4. Global types — opt-out via parent's IncludeGlobalTypes.
        if (includeGlobal)
        {
            foreach (var typePath in meshConfiguration.GlobalCreatableTypes)
            {
                if (!added.Add(typePath)) continue;
                var info = BuildInfoFromConfig(typePath, meshConfiguration);
                if (info is not null) result.Add(info);
            }
        }

        return result;
    }

    private static CreatableTypeInfo BuildInfoFromMeshNode(MeshNode node)
    {
        var def = node.Content as NodeTypeDefinition;
        var icon = def?.Emoji ?? node.Icon;
        return new CreatableTypeInfo(
            NodeTypePath: node.Path,
            DisplayName: node.Name ?? GetLastSegment(node.Path),
            Icon: icon,
            Description: def?.Description,
            Order: node.Order ?? 0);
    }

    private static CreatableTypeInfo? BuildInfoFromConfig(
        string typePath, MeshConfiguration meshConfiguration)
    {
        if (meshConfiguration.Nodes.TryGetValue(typePath, out var node))
            return BuildInfoFromMeshNode(node);
        return new CreatableTypeInfo(
            NodeTypePath: typePath,
            DisplayName: GetLastSegment(typePath),
            Icon: null,
            Description: null,
            Order: 0);
    }

    private static string GetLastSegment(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
    }
}
