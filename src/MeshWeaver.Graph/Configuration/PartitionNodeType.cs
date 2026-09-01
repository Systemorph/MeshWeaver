using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Partition nodes in the graph.
/// Partitions live at Admin/Partition/{Name} and describe which base paths they serve.
/// </summary>
public static class PartitionNodeType
{
    /// <summary>The node-type identifier string for Partition nodes.</summary>
    public const string NodeType = "Partition";

    /// <summary>The namespace under which Partition nodes live (Admin/Partition).</summary>
    public const string Namespace = "Admin/Partition";

    /// <summary>
    /// Registers the Partition node type on the mesh builder: adds the MeshNode definition,
    /// wires the static node providers and the partition access rule, and grants public read.
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder to configure.</param>
    /// <returns>The same builder, to allow fluent chaining.</returns>
    public static TBuilder AddPartitionType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStaticNodeProvider, PartitionNodeProvider>();
            services.AddSingleton<IStaticNodeProvider, DefaultPartitionProvider>();
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new PartitionAccessRule(sp.GetRequiredService<IMessageHub>()));
            return services;
        });
        return builder;
    }

    private class PartitionNodeProvider : IStaticNodeProvider
    {
        public IEnumerable<MeshNode> GetStaticNodes()
        {
            yield return CreateMeshNode();
        }
    }

    /// <summary>
    /// Builds the MeshNode definition for the Partition node type, including its default
    /// namespace, excluded contexts, and hub configuration (data source, default layout
    /// areas, the Partitions global-settings menu item, and the search default area).
    /// </summary>
    /// <returns>The Partition MeshNode definition.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Partition",
        // A declaration declares itself a NodeType, never the type it declares — see the note on
        // UserNodeType.CreateMeshNode for what self-typing cost in production. The partition
        // enumerations happen to pin a namespace (`namespace:Admin/Partition nodeType:Partition`,
        // which this root-level node never matched), so this one was latent rather than live —
        // but any pathless `nodeType:Partition` read would have hit the same collision.
        NodeType = MeshNode.NodeTypePath,
        Icon = "/static/NodeTypeIcons/database.svg",
        ExcludeFromContext = new HashSet<string> { "create", "search", "content" },
        Content = new NodeTypeDefinition { DefaultNamespace = Namespace },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<PartitionDefinition>())
            .AddDefaultLayoutAreas()
            .AddGlobalSettingsMenuItems(
                new GlobalSettingsMenuItemDefinition(
                    Id: "Partitions",
                    Label: "Partitions",
                    ContentBuilder: PartitionSettingsTab.BuildPartitionsTab,
                    Icon: FluentIcons.Database(),
                    Order: 10)
                { LabelKey = "settings.partitions" })
            .AddLayout(layout => layout
                .WithDefaultArea(MeshNodeLayoutAreas.SearchArea))
    };

    /// <summary>
    /// Access rule: Read for all authenticated users; Create/Update require
    /// <see cref="Permission.Update"/> and Delete requires <see cref="Permission.Delete"/> —
    /// i.e. admins only.
    /// </summary>
    private class PartitionAccessRule(IMessageHub hub) : INodeTypeAccessRule
    {
        public string NodeType => PartitionNodeType.NodeType;

        public IReadOnlyCollection<NodeOperation> SupportedOperations =>
            [NodeOperation.Read, NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
        {
            if (context.Operation == NodeOperation.Read)
                return Observable.Return(!string.IsNullOrEmpty(userId));

            if (string.IsNullOrEmpty(userId))
                return Observable.Return(false);

            // 🚨 Delete demands Permission.Delete, not Update — the same demand the delete
            // handler's pre-flight used to add on top of this rule. Now that the pre-flight routes
            // THROUGH the rule (#2913) the rule is the only place the decision is taken, so it has
            // to carry it. Unchanged outcome for every caller.
            if (context.Operation == NodeOperation.Delete)
                return hub.CheckPermission(context.Node.Path, userId, Permission.Delete);

            // Only admins can create/update partitions
            return hub.CheckPermission(context.Node.Path, userId, Permission.Update);
        }
    }
}
