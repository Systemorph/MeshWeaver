using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The built-in <c>Redirect</c> node type — a tombstone left at a path that MOVED, whose content is
/// a <see cref="NodeRedirect"/> naming the new location.
///
/// <para>Declaring one is pure data. A repo commits a node file at the retired path; no framework
/// change, no configuration lambda, and re-importing the same file is idempotent:</para>
///
/// <code>
/// {
///   "id": "UWDeepfield",
///   "namespace": "Reinsurance",
///   "nodeType": "Redirect",
///   "name": "UW Deepfield",
///   "content": {
///     "$type": "NodeRedirect",
///     "targetPath": "Reinsurance/Underwriting",
///     "scope": "Subtree",
///     "reason": "Merged into Underwriting"
///   }
/// }
/// </code>
///
/// <para>Browsing to <c>Reinsurance/UWDeepfield/Pricing/Rates</c> then lands on
/// <c>Reinsurance/Underwriting/Pricing/Rates</c>. The view below is what a viewer sees when the
/// redirect is NOT followed — an <see cref="RedirectScope.Exact"/> declaration reached by a deep
/// link, a cycle, the hop cap, or a target that no longer exists. It names the destination and links
/// to it rather than dead-ending, which is the whole point of leaving the tombstone behind.</para>
/// </summary>
public static class RedirectNodeType
{
    /// <summary>The NodeType value used to identify redirect declarations.</summary>
    public const string NodeType = NodeRedirectRules.NodeTypeName;

    /// <summary>Registers the built-in "Redirect" MeshNode on the mesh builder.</summary>
    public static TBuilder AddRedirectType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>Creates the MeshNode definition for the Redirect node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Redirect",
        Icon = "/static/NodeTypeIcons/link.svg",
        HubConfiguration = config => config
            .AddRedirectViews()
            .AddMeshDataSource(source => source
                .WithContentType<NodeRedirect>())
    };

    /// <summary>Registers the Redirect node's layout areas.</summary>
    public static MessageHubConfiguration AddRedirectViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(MeshNodeLayoutAreas.OverviewArea)
                .WithView(MeshNodeLayoutAreas.OverviewArea, Overview)
                .WithView(MeshNodeLayoutAreas.CreateNodeArea, CreateLayoutArea.Create)
                .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    /// <summary>
    /// The "this moved" page. Reached only when navigation did NOT follow the declaration, so it has
    /// to answer the viewer's question by itself: where did this go, and can I get there from here.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
        => host.Workspace.GetMeshNodeStream()
            .Select(node => (UiControl?)BuildOverview(host, node));

    private static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node)
    {
        var container = Controls.Stack.WithWidth("100%")
            .WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host))
            .WithView(MeshNodeLayoutAreas.BuildHeader(host, node, false));

        var redirect = node.ContentAs<NodeRedirect>(host.Hub.JsonSerializerOptions);
        var target = NodeRedirectRules.Normalize(redirect?.TargetPath);

        if (target.Length == 0)
        {
            // A declaration with no target is inert. Say so rather than rendering an empty page —
            // an author looking at this needs to know the node is the problem.
            return container.WithView(Controls.Markdown(host.Localize("redirect.noTarget")));
        }

        container = container.WithView(Controls.Markdown(
            $"{host.Localize("redirect.movedTo")}\n\n### [{target}](/{target})"));

        if (!string.IsNullOrWhiteSpace(redirect!.Reason))
            container = container.WithView(Controls.Markdown(redirect.Reason!));

        if (redirect.Scope == RedirectScope.Exact)
            container = container.WithView(Controls.Markdown(host.Localize("redirect.exactScopeHint")));

        return container;
    }
}
