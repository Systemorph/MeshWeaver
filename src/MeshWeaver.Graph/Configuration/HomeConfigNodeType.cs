using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The DATA-DRIVEN default home config — a single well-known platform node an admin edits IN-PLATFORM
/// (no code change, no image roll) to change how every user's home catalog lists content. The node
/// lives in the <b>Admin</b> partition (<see cref="ConfigPath"/>) where platform admins have standing
/// write, and is <b>public-read</b> so every user's home can read it. The home reads it REACTIVELY (via
/// the shared, per-user-RLS, empty-on-absent <c>GetQuery</c> cache — never a point-read that could
/// NotFound-storm), so an admin's edit updates every open home live. When the node is absent — or any
/// field is unset — the shipped <see cref="Defaults"/> apply (<b>FirstLevel + Flat + LastAccessed</b>),
/// so nothing regresses without the node. See <see cref="HomeConfig"/>.
/// </summary>
public static class HomeConfigNodeType
{
    /// <summary>The NodeType discriminator.</summary>
    public const string NodeType = "HomeConfig";

    /// <summary>The singleton instance id.</summary>
    public const string NodeId = "HomeConfig";

    /// <summary>The Admin partition that holds the platform config node.</summary>
    public const string AdminPartition = "Admin";

    /// <summary>The well-known singleton path: <c>Admin/HomeConfig</c>.</summary>
    public const string ConfigPath = AdminPartition + "/" + NodeId;

    /// <summary>The shipped defaults, applied whenever the config node is absent or a field is unset.</summary>
    public static HomeConfig Defaults { get; } = new();

    /// <summary>
    /// Registers the HomeConfig node type + typed content, makes it public-read (every home reads it),
    /// wires the safe node-bound Edit form, and seeds the singleton with defaults so an admin has an
    /// editable node immediately.
    /// </summary>
    public static TBuilder AddHomeConfigType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        // Typed content so the home reads HomeConfig (not raw JSON) and the Edit form binds its fields.
        builder.ConfigureHub(config => config.WithType<HomeConfig>(nameof(HomeConfig)));
        // Platform config: readable by every user's home; only admins (Admin partition) can WRITE it.
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        // Seed the singleton with defaults (the GlobalSettings _Setting pattern) so it's editable at
        // once. If a backend doesn't materialise this Admin-partition seed, the in-code Defaults keep
        // the home correct and an admin can create the node in-platform.
        builder.AddMeshNodes(new MeshNode(NodeId, AdminPartition)
        {
            Name = "Home Page",
            NodeType = NodeType,
            State = MeshNodeState.Active,
            Content = new HomeConfig(),
            ExcludeFromContext = new HashSet<string> { "search", "create" },
        });
        return builder;
    }

    /// <summary>MeshNode definition for <c>nodeType:HomeConfig</c> — typed content + the node-bound Edit form.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Home Page",
        Icon = "/static/NodeTypeIcons/layout.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddDefaultLayoutAreas()
            .AddMeshDataSource(source => source.WithContentType<HomeConfig>())
            // Override the generic Edit with the standard node-bound content editor (data-binds each
            // field straight to the node and auto-persists — no /data replica, no Save button), gated
            // to writers only. This is the sanctioned MeshNodeContentEditor, not the property-editor.
            .AddLayout(layout => layout.WithView(MeshNodeLayoutAreas.EditArea, EditHomeConfig))
    };

    /// <summary>The admin Edit form for the home config — the node-bound content editor, writers only.</summary>
    public static IObservable<UiControl?> EditHomeConfig(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return host.Hub.GetEffectivePermissions(hubPath)
            .Select(permissions => permissions.HasFlag(Permission.Update)
                ? (UiControl?)MeshNodeContentEditorControl.ForType(hubPath, typeof(HomeConfig))
                : MeshNodeLayoutAreas.BuildAccessDenied(hubPath));
    }

    /// <summary>
    /// Live home config: reads the well-known node via the shared, per-user-RLS, empty-on-absent
    /// <c>GetQuery</c> cache (never a point-read → never NotFound-storms), maps it through
    /// <see cref="Effective"/>, and emits the <see cref="Defaults"/> immediately for the first paint,
    /// then the live node content. An admin editing the node re-emits, so every home updates live.
    /// </summary>
    public static IObservable<HomeConfig> Observe(IWorkspace workspace, JsonSerializerOptions options) =>
        Observe(workspace, options, ConfigPath);

    /// <summary>Reads the config at an explicit path (defaults to <see cref="ConfigPath"/>); used by tests.</summary>
    internal static IObservable<HomeConfig> Observe(IWorkspace workspace, JsonSerializerOptions options, string configPath) =>
        workspace
            .GetQuery($"home-config:{configPath}", $"path:{configPath} nodeType:{NodeType}")
            .Select(nodes => Effective(
                nodes.FirstOrDefault(n => string.Equals(n.NodeType, NodeType, System.StringComparison.OrdinalIgnoreCase)),
                options))
            .StartWith(Defaults)
            .DistinctUntilChanged();

    /// <summary>
    /// The effective config for a node: the saved <see cref="HomeConfig"/>, or <see cref="Defaults"/>
    /// when the node is absent or its content can't be read (a partial/empty node behaves as defaults).
    /// </summary>
    public static HomeConfig Effective(MeshNode? node, JsonSerializerOptions options) =>
        node?.Content switch
        {
            HomeConfig c => c,
            JsonElement je => TryDeserialize(je, options) ?? Defaults,
            _ => Defaults,
        };

    private static HomeConfig? TryDeserialize(JsonElement je, JsonSerializerOptions options)
    {
        try { return JsonSerializer.Deserialize<HomeConfig>(je.GetRawText(), options); }
        catch { return null; }
    }
}
