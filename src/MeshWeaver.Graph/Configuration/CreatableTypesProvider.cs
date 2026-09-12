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
/// at <c>nodePath</c>. The Create-permission gate runs at
/// the outer level via <see cref="PermissionEvaluator.HasPermission(IMessageHub, string, Permission)"/>.</para>
///
/// <para>Sources merged into the result (deduped by NodeType path):</para>
/// <list type="number">
///   <item>NodeType MeshNodes returned by the query (dynamic NodeTypes
///     persisted as <c>nodeType:NodeType</c> rows + static NodeTypes
///     surfaced by <see cref="IStaticNodeProvider"/>).</item>
///   <item>Static <see cref="IStaticNodeProvider"/> entries that have not opted out of
///     <see cref="MeshContexts.Create"/> — the AddMeshNodes registrations that are never
///     persisted (Markdown, Group, Role, Redirect, UiContribution, HomeTab, …).</item>
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
    MeshConfiguration meshConfiguration) : ICreatableTypesProvider
{
    public IObservable<IReadOnlyList<CreatableTypeInfo>> GetCreatableTypes(
        string? nodePath, MeshNode? parentNode)
    {
        // The parent node's NodeType drives the "child NodeTypes of the
        // parent's type" query (Q2 — e.g. an ACME/Project instance offers
        // ACME/Project/Todo). Callers MAY pass parentNode when they already
        // hold it; when they don't, resolve it ourselves so the result is
        // robust regardless of caller timing (a caller's short best-effort
        // lookup could otherwise hand us null and silently drop Q2).
        if (parentNode is null && !string.IsNullOrEmpty(nodePath))
        {
            return hub.GetWorkspace().GetMeshNodeStream(nodePath)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
                .SelectMany(resolved => GetCreatableTypesCore(nodePath, resolved));
        }
        return GetCreatableTypesCore(nodePath, parentNode);
    }

    private IObservable<IReadOnlyList<CreatableTypeInfo>> GetCreatableTypesCore(
        string? nodePath, MeshNode? parentNode)
    {
        var meshQueryCore = hub.ServiceProvider.GetService<IMeshQueryCore>();
        var currentType = parentNode?.NodeType;

        var typeNodesObs = meshQueryCore is null
            ? Observable.Return<IReadOnlyList<MeshNode>>([])
            : QueryTypeNodes(meshQueryCore, hub.JsonSerializerOptions, nodePath, currentType);

        // Resolve the parent's NodeTypeDefinition — its CreatableTypes (when
        // set) is an explicit whitelist that FILTERS auto-discovery on top of
        // the synced query. For RUNTIME NodeTypes this def is not in
        // meshConfiguration.Nodes (static config only), so layer a live
        // GetMeshNodeStream lookup on top of the type-node query.
        var parentDefObs = ResolveParentNodeTypeDefinition(currentType);

        var typesObs = typeNodesObs
            .CombineLatest(parentDefObs, (typeNodes, parentDef) => (typeNodes, parentDef))
            .SelectMany(x => ResolveDeclaredTypeNodes(meshQueryCore, x.parentDef)
                .Select(declared => BuildInfos(
                    x.typeNodes, declared, meshConfiguration, hub.ServiceProvider,
                    currentType, x.parentDef, hub.JsonSerializerOptions)));

        // Outer security gate: only apply Create-permission filter for a
        // specific parent path. Root listing (nodePath == "") is global
        // metadata — anyone navigating to "Create" at root sees the full
        // type set; the Create operation itself is gated at the receiver.
        if (string.IsNullOrEmpty(nodePath))
            return typesObs;

        // Outer security gate is unconditional now — when RLS is not
        // configured, the EffectivePermissionsDelegate default returns
        // Permission.All, so the filter is a no-op.
        return hub.CheckPermission(nodePath, Permission.Create)
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
        IMeshQueryCore meshQueryCore,
        System.Text.Json.JsonSerializerOptions jsonOptions,
        string? nodePath, string? currentType)
    {
        var queries = BuildQueries(nodePath, currentType);
        if (queries.Length == 0)
            return Observable.Return<IReadOnlyList<MeshNode>>([]);

        // Pass the hub's real JsonSerializerOptions — IMeshQueryCore.Query
        // deserialises rows with it; null would NRE inside the provider and the
        // .Catch below would silently swallow every query result.
        //
        // 🚨 Timeout(15s) → Empty is a deadlock guard. Aggregate (line 128
        // below) waits for ALL merged observables to complete — if one
        // Query NEVER emits its Initial frame (the "synced-query
        // first-emission" race that's already documented in
        // project_synced_query_race.md), the merged stream never completes,
        // Aggregate never emits its single tuple, and the outer FirstAsync
        // hangs. Symptom: CreatableTypesFileSystemTest CI failure
        // 2026-05-23 — test ran for its full 20 s timeout. With the
        // timeout: a stuck query is treated as "no results", the downstream
        // CombineLatest gate proceeds, and the test (or UI) gets the
        // partial answer it would have gotten if the timeout query had
        // returned empty naturally. Strictly better than hanging.
        var observables = queries.Select(q => meshQueryCore
            .Query<MeshNode>(MeshQueryRequest.FromQuery(q), jsonOptions)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(15), Observable.Empty<QueryResultChange<MeshNode>>())
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
        // 🚨 `context:create` on EVERY query, because this IS the create menu. A NodeType that
        // opted out of creation (Release, Build, ModuleBuild, Partition — `ExcludeFromContext:
        // ["create"]`) must not be offered, and the opt-out is only honoured when the query
        // names the context it is for. Without it the provider offered four types the Create
        // form has always withheld.
        const string CreateContext = "context:" + MeshContexts.Create;

        if (string.IsNullOrEmpty(nodePath))
        {
            // Root listing: no namespace bound. Surface every NodeType
            // definition so the create UI can offer the full menu — a catalog, mesh-wide by
            // nature, and it says so (#3202 — fan-out is opt-in).
            return [MeshWideQuery.Declare($"nodeType:NodeType {CreateContext}")];
        }

        // Q1: NodeTypes along the ancestor chain of <myself> — picks up
        // types defined under any namespace in the path's hierarchy.
        //
        // 🚨 There is deliberately NO root-namespace leg (`namespace: nodeType:NodeType`)
        // here, even though the Create form used to run one: `namespace:` with an empty value
        // leaves ParsedQuery.Path empty, which is exactly the shape the Postgres planner
        // REFUSES as unanchored (#3202) — so on a partitioned portal that leg has never
        // returned a row. Root-level built-ins reach the menu through the static bucket in
        // BuildInfos, which is where they actually live.
        var list = new List<string>(2)
        {
            $"nodeType:NodeType scope:selfAndAncestors namespace:{nodePath} {CreateContext}",
        };
        // Q2 (when applicable): NodeTypes under the parent's NodeType so
        // an instance can offer the children its type defines (e.g. an
        // ACME/Project instance can create ACME/Project/Todo).
        if (!string.IsNullOrEmpty(currentType)
            && !string.Equals(currentType, MeshNode.NodeTypePath, StringComparison.Ordinal))
        {
            list.Add($"namespace:{currentType} nodeType:NodeType {CreateContext}");
        }
        return list.ToArray();
    }

    /// <summary>
    /// The nodes behind the type paths a CONFIG source NAMES — <see cref="NodeTypeDefinition.CreatableTypes"/>
    /// on the parent type and <see cref="MeshConfiguration.GlobalCreatableTypes"/> — restricted to
    /// the ones the static registry does not already hold, keyed by path.
    ///
    /// <para>🚨 <b>Why this read exists.</b> A named path is added by
    /// <see cref="BuildInfoFromConfig"/> whether or not any query returned it — that is the whole
    /// point of a declaration, it reaches types no namespace-scoped query can. But a RUNTIME
    /// NodeType can carry <c>ExcludeFromContext: ["create"]</c> just as a platform one does, and
    /// without its node in hand that opt-out is invisible here: the create-filtered queries
    /// correctly omit the type and this path would synthesise it straight back in. So the paths a
    /// config source names, and only those, are looked up.</para>
    ///
    /// <para>🚨 <b>One anchored QUERY per path, never a point read.</b> A declared type MAY NOT
    /// EXIST — synthesising an entry for a path the mesh does not have yet is deliberate — and a
    /// point read of an absent node answers a routing NotFound that terminates the stream AND opens
    /// the storm-breaker on that path (Doc/Architecture/CqrsAndContentAccess). A <c>path:</c> query
    /// answers zero rows instead. One path per query keeps each one ANCHORED on its own partition;
    /// a <c>path:a|b|c</c> alternation names no first segment and would fan out over every schema
    /// (#3202).</para>
    /// </summary>
    private IObservable<ImmutableDictionary<string, MeshNode>> ResolveDeclaredTypeNodes(
        IMeshQueryCore? meshQueryCore, NodeTypeDefinition? parentDef)
    {
        var empty = ImmutableDictionary<string, MeshNode>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase);
        if (meshQueryCore is null)
            return Observable.Return(empty);

        var declared = (parentDef?.CreatableTypes ?? [])
            .Concat(meshConfiguration.GlobalCreatableTypes)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // A static registration carries its own ExcludeFromContext in memory — no read needed,
            // and every platform type that opts out of create is one of those.
            .Where(p => hub.ServiceProvider.FindStaticNode(p) is null)
            .ToArray();
        if (declared.Length == 0)
            return Observable.Return(empty);

        var lookups = declared.Select(path => meshQueryCore
            .Query<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{path} nodeType:NodeType"),
                hub.JsonSerializerOptions)
            .Take(1)
            // Same deadlock guard as QueryTypeNodes: a query that never emits its Initial frame
            // must not hold the Aggregate below open for ever.
            .Timeout(TimeSpan.FromSeconds(15), Observable.Empty<QueryResultChange<MeshNode>>())
            .Catch<QueryResultChange<MeshNode>, Exception>(
                _ => Observable.Empty<QueryResultChange<MeshNode>>()));

        return Observable.Merge(lookups)
            .SelectMany(change => change.Items)
            .Aggregate(empty, (acc, node) => acc.SetItem(node.Path, node));
    }

    /// <summary>
    /// Resolves the <see cref="NodeTypeDefinition"/> for <paramref name="currentType"/>.
    /// Static config (built-in types) first, then a live
    /// <c>GetMeshNodeStream</c> lookup so RUNTIME NodeTypes — which are not in
    /// <c>MeshConfiguration</c>'s static nodes — still surface their
    /// <c>CreatableTypes</c> / <c>IncludeGlobalTypes</c> settings.
    /// </summary>
    private IObservable<NodeTypeDefinition?> ResolveParentNodeTypeDefinition(string? currentType)
    {
        if (string.IsNullOrEmpty(currentType)
            || string.Equals(currentType, MeshNode.NodeTypePath, StringComparison.Ordinal))
            return Observable.Return<NodeTypeDefinition?>(null);

        var staticNode = hub.ServiceProvider.FindStaticNode(currentType);
        if (staticNode?.Content is NodeTypeDefinition staticDef)
            return Observable.Return<NodeTypeDefinition?>(staticDef);

        return hub.GetWorkspace().GetMeshNodeStream(currentType)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .Select(n => n.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions));
    }

    private static IReadOnlyList<CreatableTypeInfo> BuildInfos(
        IReadOnlyList<MeshNode> queryNodes,
        ImmutableDictionary<string, MeshNode> declaredNodes,
        MeshConfiguration meshConfiguration,
        IServiceProvider serviceProvider,
        string? currentType,
        NodeTypeDefinition? parentDef,
        System.Text.Json.JsonSerializerOptions options)
    {
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CreatableTypeInfo>();

        // When the parent NodeType defines an explicit CreatableTypes list it
        // is an authoritative WHITELIST: auto-discovery (the synced-query rows
        // + static NodeType registrations) is filtered down to that set. With
        // no explicit list every discovered NodeType is offered.
        var whitelist = parentDef?.CreatableTypes is { } ct
            ? new HashSet<string>(ct, StringComparer.OrdinalIgnoreCase)
            : null;
        bool Allowed(string path) => whitelist is null || whitelist.Contains(path);

        // 1. Query-returned NodeTypes (already deduped by Path upstream).
        foreach (var typeNode in queryNodes)
        {
            if (!Allowed(typeNode.Path)) continue;
            if (!added.Add(typeNode.Path)) continue;
            result.Add(BuildInfoFromMeshNode(typeNode, options));
        }

        // 2. Static IStaticNodeProvider-registered nodes that aren't persisted (so they never
        //    show up in the query above).
        //
        //    🚨 THE FILTER IS THE CREATE-CONTEXT OPT-OUT, never `NodeType == "NodeType"`.
        //    A built-in type registration is `AddMeshNodes(new MeshNode("Group") {
        //    HubConfiguration = … })` — the PATH is the type name and there is no self-typing
        //    stamp at all. Measured on a running mesh (#4040): filtering on
        //    `NodeType == MeshNode.NodeTypePath` kept 7 of 42 and silently dropped Markdown,
        //    Group, Role, Redirect, UiContribution, HomeTab, License and WhatsNew — every one
        //    of them a type the Create form has always offered. Shrinking the create menu is
        //    the same class of invisible failure as never reading CreatableTypes at all, so
        //    this bucket applies exactly the rule the form applied: everything the host
        //    registered, minus what opted out of `context:create`.
        foreach (var typeNode in serviceProvider.EnumerateStaticNodes())
        {
            if (IsExcludedFromCreate(typeNode, meshConfiguration)) continue;
            if (!Allowed(typeNode.Path)) continue;
            if (!added.Add(typeNode.Path)) continue;
            result.Add(BuildInfoFromMeshNode(typeNode, options));
        }

        // 3. JSON-based CreatableTypes from the parent's NodeType definition —
        //    resolved live by ResolveParentNodeTypeDefinition (works for
        //    runtime NodeTypes, not just static config).
        var includeGlobal = true;
        if (parentDef is not null)
        {
            includeGlobal = parentDef.IncludeGlobalTypes;
            if (parentDef.CreatableTypes is not null)
            {
                foreach (var typePath in parentDef.CreatableTypes)
                {
                    if (!added.Add(typePath)) continue;
                    var info = BuildInfoFromConfig(
                        typePath, declaredNodes, meshConfiguration, serviceProvider, options);
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
                var info = BuildInfoFromConfig(
                    typePath, declaredNodes, meshConfiguration, serviceProvider, options);
                if (info is not null) result.Add(info);
            }
        }

        // Order ascending — globals carry a high Order (e.g. 1000/1001) so they sort to the end
        // of the create menu; within one Order the list is alphabetical, which is how the
        // Create form has always presented its fixed items.
        return result
            .OrderBy(x => x.Order)
            .ThenBy(x => x.DisplayName ?? x.NodeTypePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 🚨 THE create-context exclusion, in the same two halves every query backend applies
    /// (<c>StorageAdapterMeshQueryProvider.IsExcludedByContext</c> /
    /// <c>StaticNodeQueryProvider.IsExcludedByContext</c>): the TYPE-level map built from the
    /// registered nodes, then the node's own <see cref="MeshNode.ExcludeFromContext"/>. Written
    /// once here so the menu and the queries that feed it cannot answer differently.
    /// </summary>
    private static bool IsExcludedFromCreate(MeshNode node, MeshConfiguration meshConfiguration) =>
        meshConfiguration.IsExcludedFromContext(node.NodeType, MeshContexts.Create)
        || node.IsExcludedFromContext(MeshContexts.Create);

    private static CreatableTypeInfo BuildInfoFromMeshNode(MeshNode node, System.Text.Json.JsonSerializerOptions options)
    {
        var def = node.ContentAs<NodeTypeDefinition>(options);
        var icon = def?.Emoji ?? node.Icon;
        return new CreatableTypeInfo(
            NodeTypePath: node.Path,
            DisplayName: node.Name ?? GetLastSegment(node.Path),
            Icon: icon,
            Description: def?.Description,
            Order: node.Order ?? 0);
    }

    /// <summary>
    /// A path named by <see cref="NodeTypeDefinition.CreatableTypes"/> or
    /// <see cref="MeshConfiguration.GlobalCreatableTypes"/>, as a <see cref="CreatableTypeInfo"/> —
    /// or <c>null</c> when the type it names has opted out of being created.
    ///
    /// <para>🚨 <b>A type's own opt-out beats a list that names it.</b>
    /// <c>ExcludeFromContext: ["create"]</c> is the TYPE saying instances of it are made by the
    /// platform and not by a person through this form (Release, Build, ModuleBuild, Partition all
    /// say it). A whitelist or a host's global list naming one of those must not resurrect it —
    /// the queries and the static bucket both honour the opt-out, and a config source that did not
    /// would be a hole in the one rule this provider exists to apply. The opt-out is read from the
    /// static registry, which is where every platform type that declares one lives.</para>
    /// </summary>
    private static CreatableTypeInfo? BuildInfoFromConfig(
        string typePath,
        ImmutableDictionary<string, MeshNode> declaredNodes,
        MeshConfiguration meshConfiguration,
        IServiceProvider serviceProvider,
        System.Text.Json.JsonSerializerOptions options)
    {
        // Static first (no read), then the node the declared-path lookup found. A path neither
        // holds is one the mesh does not have — synthesised below, deliberately: a declaration may
        // name a type an import has not landed yet.
        var node = serviceProvider.FindStaticNode(typePath)
                   ?? declaredNodes.GetValueOrDefault(typePath);
        if (node is not null)
            return IsExcludedFromCreate(node, meshConfiguration)
                ? null
                : BuildInfoFromMeshNode(node, options);
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
