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
/// hub, the same pattern <c>MeshDataSourceExtensions.HandleNodeTypeSchemaRequest</c>
/// uses to answer <c>SchemaReference</c> requests.
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
                var typeDef = model.AllTypes.FirstOrDefault(td => td.Type.Name == typeName)
                    ?? model.SeedTypes.FirstOrDefault(td => td.Type.Name == typeName);
                return Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;")
                    .WithView(typeDef == null
                        ? Controls.Markdown($"# Type not found\n\nType '{typeName}' was not found in this NodeType's data model.")
                        : DataModelLayoutArea.RenderTypeDetails(typeDef, model.AllTypes, linkBase));
            }

            return (UiControl?)Controls.Stack.WithWidth("100%").WithStyle("padding: 24px; gap: 8px;")
                .WithView(Controls.H2("Data model").WithStyle("margin: 0;"))
                .WithView(BuildDiagramJsonTabs(model, linkBase));
        });
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
                    .StartWith(Controls.Body("Resolving data model…")
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
    private static UiControl BuildDiagramJsonTabs(NodeTypeInstanceModel model, string linkBase)
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

        return diagram ?? schema ?? Controls.Body("No data model available.")
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
                    : ProbeInstanceModel(hub, config))
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

            return store.TryGetAssemblyPath(node.Path, version)
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
    /// snapshots the type registry / content type / JSON schema. The initial
    /// <see cref="GetDataRequest"/> round-trip doubles as the init gate: by the time
    /// the response arrives, the probe's DataContext is fully built and safe to read.
    /// The probe is disposed after the snapshot; the snapshotted
    /// <see cref="ITypeDefinition"/>s stay valid (the assembly load context is owned
    /// by the compilation cache, not the probe).
    /// </summary>
    private static IObservable<NodeTypeInstanceModel?> ProbeInstanceModel(
        IMessageHub hub,
        Func<MessageHubConfiguration, MessageHubConfiguration> config)
    {
        var probeAddress = new Address($"$model-probe/{Guid.NewGuid():N}");
        var probe = hub.GetHostedHub(probeAddress, c => config(c.AddData()));
        if (probe == null)
            return Observable.Return<NodeTypeInstanceModel?>(null);

        var delivery = probe.Post(new GetDataRequest(new SchemaReference()))!;
        return probe.Observe(delivery)
            .Select(d => d.Message)
            .OfType<GetDataResponse>()
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .Select(response => SnapshotModel(probe, response.Data as SchemaInfo))
            .Finally(probe.Dispose);
    }

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
