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
/// navigation context. The canonical replacement for
/// <c>INodeTypeService.GetCreatableTypesAsync</c>: subscribes to
/// <see cref="MeshNodeStreamExtensions.GetQuery(IWorkspace, object, string[])"/>
/// for namespace-scoped queries — never a global <c>nodeType:NodeType</c>
/// scan. Per <c>Doc/Architecture/SyncedMeshNodeQueries.md</c>.
///
/// <para>Sources merged into the result:</para>
/// <list type="number">
///   <item>Child NodeTypes under the current <paramref name="nodePath"/>
///     (<c>namespace:{nodePath} nodeType:NodeType</c>).</item>
///   <item>Child NodeTypes under the parent's NodeType — e.g. an
///     <c>ACME/ProductLaunch</c> node with <c>NodeType=ACME/Project</c>
///     creates <c>ACME/Project/Todo</c>.</item>
///   <item>Explicit <c>CreatableTypes</c> JSON on the parent NodeType
///     definition (read from
///     <see cref="NodeTypeDefinition.CreatableTypes"/>).</item>
///   <item><see cref="MeshConfiguration.GlobalCreatableTypes"/> when
///     <c>IncludeGlobalTypes</c> is true (default).</item>
/// </list>
///
/// <para>The provider deliberately does NOT scan <c>nodeType:NodeType</c>
/// without a namespace bound — the "collect everything" pattern that
/// pre-empted this refactor.</para>
/// </summary>
internal sealed class CreatableTypesProvider(
    IMessageHub hub,
    MeshConfiguration meshConfiguration,
    ISecurityService? securityService = null) : ICreatableTypesProvider
{
    public IObservable<IReadOnlyList<CreatableTypeInfo>> GetCreatableTypes(
        string? nodePath, MeshNode? parentNode)
    {
        var workspace = hub.GetWorkspace();
        var currentType = parentNode?.NodeType;
        var queries = BuildQueries(nodePath, currentType);

        IObservable<IReadOnlyList<CreatableTypeInfo>> typesObs;
        if (queries.Length == 0)
        {
            // Root case: no live query. Static types + globals from in-memory
            // MeshConfiguration only — no DB scan.
            typesObs = Observable.Return(BuildRootInfos(meshConfiguration));
        }
        else
        {
            typesObs = workspace.GetQuery($"creatable-types:{nodePath ?? ""}", queries)
                .Select(snapshot => BuildInfos(snapshot, meshConfiguration, parentNode, currentType));
        }

        // Filter by Create permission on the parent path — combined with the
        // live synced access-control query inside ISecurityService.HasPermission
        // (which itself binds to workspace.GetQuery on AccessAssignment). If the
        // user can't create at this namespace, return empty regardless of
        // candidate types.
        if (securityService is null || string.IsNullOrEmpty(nodePath))
            return typesObs;

        return securityService.HasPermission(nodePath, Permission.Create)
            .CombineLatest(typesObs, (canCreate, types) => canCreate ? types : (IReadOnlyList<CreatableTypeInfo>)[]);
    }

    private static string[] BuildQueries(string? nodePath, string? currentType)
    {
        if (string.IsNullOrEmpty(nodePath))
            return [];

        // Q1: NodeTypes along the ancestor chain of <myself> — picks up
        // types defined under any namespace in the path's hierarchy.
        // Q2 (when applicable): NodeTypes under the parent's NodeType so
        // an instance can offer the children its type defines (e.g. an
        // ACME/Project instance can create ACME/Project/Todo).
        var list = new List<string>(2)
        {
            $"nodeType:NodeType scope:selfAndAncestors namespace:{nodePath}",
        };
        if (!string.IsNullOrEmpty(currentType)
            && !string.Equals(currentType, MeshNode.NodeTypePath, StringComparison.Ordinal))
        {
            list.Add($"namespace:{currentType} nodeType:NodeType");
        }
        return list.ToArray();
    }

    private static IReadOnlyList<CreatableTypeInfo> BuildRootInfos(MeshConfiguration meshConfiguration)
    {
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CreatableTypeInfo>();
        foreach (var typePath in meshConfiguration.GlobalCreatableTypes)
        {
            if (!added.Add(typePath)) continue;
            var info = BuildInfoFromConfig(typePath, meshConfiguration);
            if (info is not null) result.Add(info);
        }
        return result;
    }

    private static IReadOnlyList<CreatableTypeInfo> BuildInfos(
        IEnumerable<MeshNode> snapshot,
        MeshConfiguration meshConfiguration,
        MeshNode? parentNode,
        string? currentType)
    {
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CreatableTypeInfo>();

        // 1. Synced-query child NodeTypes (deduped by Path).
        foreach (var typeNode in snapshot)
        {
            if (!added.Add(typeNode.Path)) continue;
            result.Add(BuildInfoFromMeshNode(typeNode));
        }

        // 2. JSON-based CreatableTypes from the parent's NodeType definition.
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

        // 3. Global types — opt-out via parent's IncludeGlobalTypes.
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
