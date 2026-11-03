using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Snapshot of the data model a NodeType's INSTANCES carry — the types registered
/// by the type's instance hub configuration (compiled assembly for dynamic types,
/// the registered delegate for static types), plus the JSON schema of the content type.
/// </summary>
internal sealed record NodeTypeInstanceModel(
    IReadOnlyList<ITypeDefinition> SeedTypes,
    IReadOnlyList<ITypeDefinition> AllTypes,
    string? ContentTypeName,
    string? SchemaJson);

/// <summary>
/// Resolves and renders the data model of a NodeType's instances on the NodeType's
/// own pages: the Mermaid class diagram + JSON schema section on the Overview, and
/// the <c>$Model</c> area (diagram / per-type detail). The NodeType definition hub's
/// own registry only knows <see cref="NodeTypeDefinition"/> — the instance model
/// comes from applying the type's instance hub configuration to a short-lived probe
/// hub — the same probe <see cref="InstanceSchemaProvider"/> answers the type's
/// <c>SchemaReference</c> (<c>@{type}/schema/</c>) from.
/// </summary>
internal static class NodeTypeDataModelAreas
{
    /// <summary>
    /// Content for the NodeType's <c>$Model</c> area (rendered inside the shared
    /// NodeType shell): no Id → the Mermaid class diagram of the instance data model;
    /// Id → the detail page for that type.
    /// </summary>
    internal static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext ctx)
    {
        var typeName = host.Reference.Id?.ToString();
        var linkBase = $"/{host.Hub.Address}/{MeshNodeLayoutAreas.ModelArea}";

        return GetInstanceModelStream(host).Select(model =>
        {
            if (model == null || model.AllTypes.Count == 0)
                return (UiControl?)Controls.Markdown(
                        "*No data model available — this NodeType has no compiled release "
                        + "and no registered instance configuration yet.*")
                    .WithStyle("padding: 24px;");

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                var typeDef = ResolveModelType(model, typeName!, host.Hub.Address.Path);
                return Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;")
                    .WithView(typeDef == null
                        ? Controls.Markdown($"# Type not found\n\nType '{typeName}' was not found in this NodeType's data model.")
                        : DataModelLayoutArea.RenderTypeDetails(typeDef, model.AllTypes, linkBase, catalogHref: null, host.ViewerLocale()));
            }

            return (UiControl?)Controls.Stack.WithWidth("100%").WithStyle("padding: 24px; gap: 8px;")
                .WithView(Controls.H2(host.Localize("ui.dataModel")).WithStyle("margin: 0;"))
                .WithView(BuildDiagramJsonTabs(model, linkBase, locale: host.ViewerLocale()));
        });
    }

    /// <summary>
    /// The type a <c>$Model</c> Id names in a NodeType's instance model: a registered type of that
    /// name, else — when the Id is the NodeType's OWN name (the last segment of its path, which is how
    /// a caller that knows only the type node addresses it, e.g. <c>Esl/…/DirCarrier</c> whose content
    /// type is <c>EslOfficeCarrier</c>) — the content type.
    /// </summary>
    internal static ITypeDefinition? ResolveModelType(NodeTypeInstanceModel model, string typeName, string nodeTypePath)
        => model.AllTypes.FirstOrDefault(td => td.Type.Name == typeName)
           ?? model.SeedTypes.FirstOrDefault(td => td.Type.Name == typeName)
           ?? (IsOwnName(typeName, nodeTypePath) && model.ContentTypeName is { } content
               ? model.SeedTypes.FirstOrDefault(td => td.Type.Name == content)
                 ?? model.AllTypes.FirstOrDefault(td => td.Type.Name == content)
               : null);

    private static bool IsOwnName(string typeName, string nodeTypePath)
        => string.Equals(typeName, nodeTypePath[(nodeTypePath.LastIndexOf('/') + 1)..], StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The NodeType's <c>$Schema</c> area: the JSON schema of the content its INSTANCES carry — the
    /// type its configuration declares with <c>WithContentType</c> — never the definition hub's own
    /// <see cref="NodeTypeDefinition"/>. With an Id, the schema of that type in the instance model
    /// (the NodeType's own name addresses the content type). Read from the hub's own
    /// <see cref="SchemaReference"/> stream, which <see cref="InstanceSchemaProvider"/> answers, so the
    /// area, <c>@{type}/schema/</c> and a <c>GetDataRequest</c> can never disagree.
    /// </summary>
    internal static IObservable<UiControl?> Schema(LayoutAreaHost host, RenderingContext _)
    {
        var typeName = host.Reference.Id?.ToString();
        var stream = host.Workspace.GetNullableStream(new SchemaReference(string.IsNullOrWhiteSpace(typeName) ? null : typeName));
        var locale = host.ViewerLocale();
        if (stream is null)
            return Observable.Return<UiControl?>(Controls.Markdown(
                $"*{LocalizationCatalog.Get("ui.schema.noDataContext", locale)}*"));
        return stream
            .Select(change => change.Value.As<SchemaInfo>(host.Hub.JsonSerializerOptions))
            .Select(info => (UiControl?)Controls.Markdown(RenderSchemaMarkdown(info, typeName, host.Hub.Address.Path, locale)))
            .StartWith(Controls.Body(host.Localize("ui.resolvingDataModel"))
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic; padding: 8px 0;"));
    }

    /// <summary>The markdown of the NodeType <c>$Schema</c> area for one schema answer, in the viewer's
    /// language (type names and paths stay as they are).</summary>
    internal static string RenderSchemaMarkdown(SchemaInfo? info, string? requestedType, string nodeTypePath, string? locale = null)
    {
        if (info is null || string.IsNullOrWhiteSpace(info.Schema) || info.Schema == "{}")
            return $"## {LocalizationCatalog.Get("ui.schema.heading", locale)}\n\n*"
                   + (string.IsNullOrWhiteSpace(requestedType)
                       ? LocalizationCatalog.Get("ui.schema.noContentType", locale)
                       : LocalizationCatalog.Get("ui.schema.typeNotInModel", locale, requestedType, nodeTypePath))
                   + "*";
        var heading = string.IsNullOrWhiteSpace(requestedType)
            ? LocalizationCatalog.Get("ui.schema.contentTypeHeading", locale, info.Type)
            : LocalizationCatalog.Get("ui.schema.typeHeading", locale, info.Type);
        return $"## {heading}\n\n```json\n{PrettyPrint(info.Schema)}\n```";
    }

    /// <summary>
    /// The <see cref="DataContext.SchemaProvider"/> of a NodeType definition hub: a
    /// <see cref="SchemaReference"/> on the type describes its INSTANCES' content, resolved the way an
    /// instance activation resolves it — the type's published build (or, for a static type, its
    /// registered instance configuration) applied to a short-lived probe hub. Nothing about it depends on
    /// an instance having been activated in this process.
    /// <para>Live: re-resolves when the published build changes, as the <c>$Model</c> area does. A type
    /// the instance model does not know falls back to the definition hub's own registry, so an explicit
    /// <c>schema/NodeTypeDefinition</c> (or <c>schema/MeshNode</c>) still describes the definition. An
    /// untyped reference on a type with no resolvable instance configuration answers the empty schema —
    /// never <c>NodeTypeDefinition</c> dressed up as the content type.</para>
    /// </summary>
    internal static IObservable<SchemaInfo>? InstanceSchemaProvider(IWorkspace workspace, string? typeName)
    {
        var hub = workspace.Hub;
        // A transient probe whose instance configuration is itself a NodeType view (the built-in
        // NodeType type, a type of types) gets this provider too — but a probe has no own node, so the
        // provider would never emit and the probe's schema and model reads would time out. A probe
        // answers from the registry it was configured with.
        if (TransientProbeAddresses.IsProbeAddress(hub.Address.Path))
            return null;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(NodeTypeDataModelAreas));
        var nodeTypePath = hub.Address.Path;
        IObservable<SchemaInfo> Fallback() => typeName is null
            ? Observable.Return(new SchemaInfo("", "{}"))
            : DataExtensions.ObserveRegistrySchema(hub, typeName);

        return Observable.Defer(() => workspace.GetMeshNodeStream())
            .Where(n => n is not null)
            .DistinctUntilChanged(n =>
            {
                var def = n!.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions);
                return (def?.LatestAssemblyCollection, def?.LatestAssemblyPath, def?.LastCompiledVersion);
            })
            .Select(node => ResolveInstanceHubConfig(hub, node!)
                .SelectMany(config => config == null
                    ? Observable.Return<SchemaInfo?>(null)
                    : ProbeInstanceSchema(hub, config, nodeTypePath, typeName))
                // A TEARDOWN is not a missing schema: rethrown, so the request path answers it as the
                // shutdown it is (and the caller reconnects to a fresh activation). Anything else —
                // an unloadable build, a probe that faulted — degrades to the fallback, logged.
                .Catch<SchemaInfo?, Exception>(ex =>
                {
                    if (DataExtensions.IsOwnerTeardown(ex))
                        return Observable.Throw<SchemaInfo?>(ex);
                    logger?.LogWarning(ex, "Instance schema resolution failed for {Path} (type {Type})",
                        nodeTypePath, typeName ?? "<content>");
                    return Observable.Return<SchemaInfo?>(null);
                })
                .SelectMany(info => IsEmptySchema(info) ? Fallback() : Observable.Return(info!)))
            .Switch();
    }

    private static bool IsEmptySchema(SchemaInfo? info)
        => info is null || string.IsNullOrWhiteSpace(info.Schema) || info.Schema == "{}";

    /// <summary>
    /// Applies the instance configuration to a short-lived probe hub and reads the probe's own
    /// <see cref="SchemaReference"/> for <paramref name="typeName"/> (null = the content type). The
    /// NodeType's own name, when it names no registered type, addresses the content type.
    /// </summary>
    internal static IObservable<SchemaInfo?> ProbeInstanceSchema(
        IMessageHub hub,
        Func<MessageHubConfiguration, MessageHubConfiguration> config,
        string nodeTypePath,
        string? typeName)
    {
        var probeAddress = new Address($"{TransientProbeAddresses.SchemaProbePrefix}{Guid.NewGuid():N}");
        // 🚨 AsTransientNodeProbe — see ProbeInstanceModel: data context yes, per-node control plane no.
        var probe = hub.GetHostedHub(probeAddress,
            c => config(c.AddData().WithNodeTypePath(nodeTypePath)).AsTransientNodeProbe());
        if (probe == null)
            return Observable.Return<SchemaInfo?>(null);

        IObservable<SchemaInfo?> Read(string? type)
            => (probe.GetWorkspace().GetNullableStream(new SchemaReference(type))
                    ?? Observable.Empty<ChangeItem<object>>())
                .Take(1)
                .Select(change => change.Value.As<SchemaInfo>(probe.JsonSerializerOptions));

        return ProbeStarted(probe)
            .SelectMany(_ => Read(typeName))
            .SelectMany(info => typeName is not null && IsEmptySchema(info) && IsOwnName(typeName, nodeTypePath)
                ? Read(null)
                : Observable.Return(info))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .DefaultIfEmpty(null)
            .Finally(probe.Dispose);
    }

    /// <summary>
    /// The "Data model" section on the NodeType Overview: section header linking to
    /// the <c>$Model</c> area, the Mermaid diagram, and the content type's JSON schema.
    /// </summary>
    internal static UiControl BuildOverviewSection(LayoutAreaHost host)
    {
        var hubAddress = host.Hub.Address;
        var modelHref = new LayoutAreaReference(MeshNodeLayoutAreas.ModelArea).ToHref(hubAddress);
        var linkBase = $"/{hubAddress}/{MeshNodeLayoutAreas.ModelArea}";

        return Controls.Stack.WithWidth("100%")
            .WithView(NodeTypeLayoutAreas.BuildSectionHeader("Data model", modelHref, "Open data model"))
            .WithView(
                (h, c) => GetInstanceModelStream(host)
                    .Select(model => RenderOverviewBody(model, linkBase))
                    .StartWith(Controls.Body(host.Localize("ui.resolvingDataModel"))
                        .WithStyle("color: var(--neutral-foreground-hint); font-style: italic; padding: 8px 0;")),
                "DataModelSection");
    }

    private static UiControl RenderOverviewBody(NodeTypeInstanceModel? model, string linkBase)
    {
        if (model == null || (model.SeedTypes.Count == 0 && model.SchemaJson == null))
            return Controls.Body(
                    "No compiled data model yet — create a release to see the type diagram and schema.")
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic; padding: 8px 0;");

        return BuildDiagramJsonTabs(model, linkBase);
    }

    /// <summary>
    /// The data-model view itself: a Mermaid class diagram with a tab to switch to
    /// the JSON schema and back. Tabs only render when both representations exist;
    /// a single representation renders bare.
    /// </summary>
    private static UiControl BuildDiagramJsonTabs(NodeTypeInstanceModel model, string linkBase, string? locale = null)
    {
        UiControl? diagram = model.SeedTypes.Count > 0
            ? Controls.Markdown(DataModelLayoutArea.BuildMermaidDiagram(
                    model.SeedTypes, model.AllTypes, linkBase, includeGroupHeadings: false))
                .WithStyle("width: 100%; overflow: auto;")
            : null;

        UiControl? schema = !string.IsNullOrWhiteSpace(model.SchemaJson) && model.SchemaJson != "{}"
            ? Controls.Markdown($"```json\n{PrettyPrint(model.SchemaJson!)}\n```")
                .WithStyle("width: 100%; overflow: auto;")
            : null;

        if (diagram != null && schema != null)
        {
            var jsonLabel = string.IsNullOrEmpty(model.ContentTypeName)
                ? "JSON"
                : $"JSON — {model.ContentTypeName}";
            return Controls.Tabs
                .WithView(diagram, s => s.WithLabel("Diagram"))
                .WithView(schema, s => s.WithLabel(jsonLabel));
        }

        return diagram ?? schema ?? Controls.Body(LocalizationCatalog.Get("ui.noDataModel", locale))
            .WithStyle("color: var(--neutral-foreground-hint); font-style: italic;");
    }

    private static string PrettyPrint(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? json;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>
    /// Live instance-model stream: re-resolves only when the published assembly
    /// reference changes (not on every node tick — source edits and compile status
    /// transitions don't re-probe).
    /// </summary>
    internal static IObservable<NodeTypeInstanceModel?> GetInstanceModelStream(LayoutAreaHost host)
    {
        var hub = host.Hub;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(NodeTypeDataModelAreas));

        return host.Workspace.GetMeshNodeStream()
            .Where(n => n is not null)
            .DistinctUntilChanged(n =>
            {
                var def = n.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions);
                return (def?.LatestAssemblyCollection, def?.LatestAssemblyPath, def?.LastCompiledVersion);
            })
            .Select(node => ResolveInstanceHubConfig(hub, node!)
                .SelectMany(config => config == null
                    ? Observable.Return<NodeTypeInstanceModel?>(null)
                    // This hub IS the NodeType hub (ResolveInstanceHubConfig matches its
                    // Address.Path against the compiled configurations' NodeType), so its path is
                    // the NodeType path the probe's WithContentType should register under.
                    : ProbeInstanceModel(hub, config, hub.Address.Path))
                .Catch<NodeTypeInstanceModel?, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "Instance data-model resolution failed for {Path}", hub.Address.Path);
                    return Observable.Return<NodeTypeInstanceModel?>(null);
                }))
            .Switch();
    }

    /// <summary>
    /// Resolves the hub configuration a NodeType applies to its INSTANCES.
    /// Dynamic types: the published assembly (LatestAssemblyCollection/Path via
    /// <see cref="IAssemblyStore"/> + <see cref="IMeshNodeCompilationService.GetConfigurationsFromExistingAssembly"/>)
    /// — same passive resolution as the SchemaReference handler; never kicks a compile.
    /// Static types: the in-process <see cref="MeshNode.HubConfiguration"/> delegate
    /// the definition node was registered with.
    /// </summary>
    private static IObservable<Func<MessageHubConfiguration, MessageHubConfiguration>?> ResolveInstanceHubConfig(
        IMessageHub hub, MeshNode node)
    {
        var def = node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions);
        var compilationService = hub.ServiceProvider.GetService<IMeshNodeCompilationService>();

        // 🚨 ADOPT-TIME IDENTITY GATE (#3472). This is one of the six load paths that never
        // consulted NodeTypeCompilationHelpers.HasUsableBuild, so a record naming an assembly
        // built for another framework generation was loaded here on nothing but the two
        // coordinate fields being populated. Refusing takes the branch a store miss already
        // takes — the node's own (static / previously bound) configuration — so a data-model
        // area degrades exactly as it does when the bytes are cold, never worse.
        if (def != null && NodeTypeBuildIdentity.Refuses(def))
        {
            // Level by healability, once per (type, record identity) — #5066.
            hub.ServiceProvider.GetRequiredService<NodeTypeAdoptionRefusalLog>().Report(
                hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(NodeTypeDataModelAreas)),
                "DataModelArea", node.Path, def,
                "The data-model area falls back to this node's own configuration.");
            return Observable.Return(node.HubConfiguration);
        }

        if (def != null
            && compilationService != null
            && !string.IsNullOrEmpty(def.LatestAssemblyCollection)
            && !string.IsNullOrEmpty(def.LatestAssemblyPath))
        {
            var hubPath = hub.Address.Path;
            var version = def.LastCompiledVersion ?? node.Version;
            var store = string.Equals(def.LatestAssemblyCollection, FrameworkAssemblyStore.CollectionName, StringComparison.Ordinal)
                ? (IAssemblyStore)FrameworkAssemblyStore.Instance
                : hub.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;

            return store.TryGetBuildPath(node.Path, version, def.LatestAssemblyPath, def.LatestAssemblyMvid)
                .SelectMany(localPath =>
                {
                    if (string.IsNullOrEmpty(localPath))
                        return Observable.Return(node.HubConfiguration);
                    return compilationService.GetConfigurationsFromExistingAssembly(localPath!, hubPath)
                        .Take(1)
                        .Select(result =>
                        {
                            var matching = result?.NodeTypeConfigurations
                                .FirstOrDefault(c => string.Equals(c.NodeType, hubPath, StringComparison.OrdinalIgnoreCase))
                                ?? result?.NodeTypeConfigurations.FirstOrDefault();
                            return matching?.HubConfiguration ?? node.HubConfiguration;
                        });
                });
        }

        return Observable.Return(node.HubConfiguration);
    }

    /// <summary>
    /// Applies the instance configuration to a short-lived hosted probe hub and
    /// snapshots the type registry / content type / JSON schema. The probe's own
    /// <see cref="SchemaReference"/> stream doubles as the init gate: it first emits once
    /// the probe's DataContext is fully built and safe to read.
    /// The probe is disposed after the snapshot; the snapshotted
    /// <see cref="ITypeDefinition"/>s stay valid (the assembly load context is owned
    /// by the compilation cache, not the probe).
    /// </summary>
    /// <param name="hub">The hub hosting the probe.</param>
    /// <param name="config">The instance configuration to apply.</param>
    /// <param name="nodeTypePath">The NodeType path this configuration belongs to, stamped so a
    /// <c>WithContentType</c> reached from the probe records an EXACT registry entry rather than a
    /// bare-name one (see <c>NodeTypePathHolder</c>). Optional: a caller with no NodeType in hand
    /// leaves the probe unstamped instead of recording a meaningless key.</param>
    internal static IObservable<NodeTypeInstanceModel?> ProbeInstanceModel(
        IMessageHub hub,
        Func<MessageHubConfiguration, MessageHubConfiguration> config,
        string? nodeTypePath = null)
    {
        var probeAddress = new Address($"{TransientProbeAddresses.ModelProbePrefix}{Guid.NewGuid():N}");
        // 🚨 AsTransientNodeProbe: this hub exists to be read and disposed. It must get the data
        // context (DataSources / TypeSources / SchemaReference are exactly what SnapshotModel
        // reads) but NOT the per-node control plane — the compile / release-request / sources
        // watchers and the compile-state mirror are month-scale machinery that, on a
        // microsecond-scale hub, only opened `sync/` sub-hubs and then faulted on teardown.
        var probe = hub.GetHostedHub(probeAddress,
            c => config(c.AddData().WithNodeTypePath(nodeTypePath)).AsTransientNodeProbe());
        if (probe == null)
            return Observable.Return<NodeTypeInstanceModel?>(null);

        // 🚨 THE INIT GATE is the probe reaching MessageHubRunLevel.Started — the moment its
        // initialization gates open, which is exactly when a message posted to it would have been
        // processed. This used to be a GetDataRequest the probe posted to ITSELF, whose reply
        // doubled as that gate (Doc/Architecture/DataPlaneMessagesAreStreamPlumbing). The schema
        // stream ALONE is not a gate: it can reduce off the initial empty store before the data
        // sources have initialized — measured in CI, where the snapshot ran and the probe was
        // disposed before a virtual source's provider had even subscribed.
        return ProbeStarted(probe)
            .SelectMany(_ => probe.GetWorkspace().GetNullableStream(new SchemaReference())
                ?? Observable.Empty<ChangeItem<object>>())
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            // .As<T>, never a cast: an object-typed stream payload may arrive as JSON.
            .Select(change => SnapshotModel(probe, change.Value.As<SchemaInfo>(probe.JsonSerializerOptions)))
            .DefaultIfEmpty(null)
            .Finally(probe.Dispose);
    }

    /// <summary>
    /// Emits once when <paramref name="probe"/> has STARTED (its initialization gates are open), and
    /// faults if it winds down first. <see cref="IMessageHub.RunLevelChanged"/> replays the current
    /// level, so a probe that is already started answers at once.
    /// </summary>
    internal static IObservable<System.Reactive.Unit> ProbeStarted(IMessageHub probe)
        => probe.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Started)
            .Take(1)
            .SelectMany(level => level == MessageHubRunLevel.Started
                ? Observable.Return(System.Reactive.Unit.Default)
                : Observable.Throw<System.Reactive.Unit>(new ObjectDisposedException(probe.Address.ToString(),
                    $"The probe reached {level} before it started.")));

    private static NodeTypeInstanceModel? SnapshotModel(IMessageHub probe, SchemaInfo? defaultSchema)
    {
        var workspace = probe.GetWorkspace();
        var registry = probe.TypeRegistry;

        var contentType = workspace.DataContext.DataSources
            .OfType<MeshDataSource>()
            .Select(ds => ds.ContentType)
            .FirstOrDefault(t => t != null);

        var allTypes = registry.Types
            .Select(kv => kv.Value)
            .Where(td => DataModelLayoutArea.IsEligibleDomainType(td.Type))
            .DistinctBy(td => td.Type)
            .ToList();

        var seeds = new List<ITypeDefinition>();
        if (contentType != null && registry.GetTypeDefinition(contentType) is { } contentDef)
            seeds.Add(contentDef);
        foreach (var typeSource in workspace.DataContext.TypeSources.Values)
        {
            var td = typeSource.TypeDefinition;
            if (td.Type == typeof(MeshNode)) continue;
            if (seeds.All(s => s.Type != td.Type)) seeds.Add(td);
        }

        string? schemaTypeName = null;
        string? schemaJson = null;
        if (contentType != null)
        {
            schemaTypeName = contentType.Name;
            schemaJson = DataExtensions.GenerateJsonSchema(probe, contentType.Name);
        }
        else if (defaultSchema != null && !string.IsNullOrWhiteSpace(defaultSchema.Schema) && defaultSchema.Schema != "{}")
        {
            schemaTypeName = defaultSchema.Type;
            schemaJson = defaultSchema.Schema;
        }

        if (seeds.Count == 0 && schemaJson == null)
            return null;

        return new NodeTypeInstanceModel(seeds, allTypes, schemaTypeName, schemaJson);
    }
}
