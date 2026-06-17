using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Settings tab that LISTS the agents available in each scope of the per-partition agent registry
/// (see <see cref="AgentPickerProjection.BuildAgentQuery"/> →
/// <c>namespace:{user}/Agent|{space}/Agent|Agent nodeType:Agent</c>):
/// <list type="bullet">
///   <item><b>Your agents</b> — <c>{user}/Agent</c>;</item>
///   <item><b>This space's agents</b> — <c>{space}/Agent</c> (the partition this settings page sits in);</item>
///   <item><b>Platform agents</b> — the bare <c>Agent</c> namespace, shown ONLY to platform admins
///         (<see cref="HubPermissionExtensions.IsGlobalAdmin(IMessageHub)"/>).</item>
/// </list>
/// Each agent links to its node. Reads run through <c>hub.GetQuery</c> (per-user RLS), so you only see
/// agents you have access to. Drop a new Agent under <c>{space}/Agent</c> or <c>{your-home}/Agent</c>
/// and it appears in that scope's chat <c>/agent</c> picker.
/// </summary>
public static class AgentsSettingsTab
{
    public const string TabId = "Agents";

    public static MessageHubConfiguration AddAgentsSettingsTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(
            new SettingsMenuItemDefinition(
                Id: TabId,
                Label: "Agents",
                ContentBuilder: BuildContent,
                Group: "AI",
                Icon: FluentIcons.Sparkle(),
                GroupIcon: FluentIcons.Sparkle(),
                Order: 215,
                RequiredPermission: Permission.Read));

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var userId = accessService?.Context?.ObjectId ?? "";
        // The space = the partition this settings page sits in (the node's, or the hub's).
        var space = AgentPickerProjection.PartitionOf(node?.Path)
                    ?? AgentPickerProjection.PartitionOf(host.Hub.Address.ToString());

        stack = stack.WithView(Controls.H2("Agents").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size:0.85rem; color:var(--neutral-foreground-hint); margin-bottom:16px;\">" +
            "Agents available in your chat. Drop an Agent under <code>{space}/Agent</code> or " +
            "<code>{your-home}/Agent</code> to make it available in that space, or just for you. " +
            "Platform defaults live in the <code>Agent</code> namespace.</p>"));

        // Your agents.
        if (!string.IsNullOrEmpty(userId))
            stack = stack.WithView(BuildAgentSection(host, "Your agents",
                $"{userId}/{AgentPickerProjection.AgentSubNamespace}"));

        // This space's agents — when the settings node sits in a space distinct from the user's home.
        if (!string.IsNullOrEmpty(space) && !string.Equals(space, userId, StringComparison.OrdinalIgnoreCase))
            stack = stack.WithView(BuildAgentSection(host, "This space's agents",
                $"{space}/{AgentPickerProjection.AgentSubNamespace}"));

        // Platform agents — only for platform admins (IsGlobalAdmin); everyone else sees nothing here.
        stack = stack.WithView((h, _) => h.Hub.IsGlobalAdmin()
            .Select(isAdmin => isAdmin
                ? (UiControl?)BuildAgentSection(h, "Platform agents (admin)", AgentPickerProjection.AgentRootNamespace)
                : Controls.Stack.WithWidth("100%"))
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        return stack;
    }

    /// <summary>Lists the agents directly in <paramref name="agentNamespace"/> (read-only thumbnails,
    /// each linking to the agent node). Empty namespace → a "none yet" hint.</summary>
    private static UiControl BuildAgentSection(LayoutAreaHost host, string title, string agentNamespace)
    {
        var section = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px; margin-bottom: 16px;");
        section = section.WithView(Controls.Html(
            $"<h3 style=\"margin:8px 0; font-size:1rem;\">{System.Web.HttpUtility.HtmlEncode(title)}</h3>"));
        section = section.WithView((h, _) =>
            h.Hub.GetQuery($"settings-agents:{agentNamespace}",
                    $"namespace:{agentNamespace} nodeType:{AgentNodeType.NodeType} sort:order")
                .Select(nodes =>
                {
                    var agents = nodes
                        .Where(n => string.Equals(n.NodeType, AgentNodeType.NodeType, StringComparison.OrdinalIgnoreCase)
                                    && !string.IsNullOrEmpty(n.Path))
                        .OrderBy(n => n.Order ?? 0).ThenBy(n => n.Name)
                        .ToList();
                    if (agents.Count == 0)
                        return (UiControl?)Controls.Html(
                            "<p style=\"color:var(--neutral-foreground-hint); font-size:0.85rem;\">None yet.</p>");
                    var container = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");
                    foreach (var a in agents)
                        container = container.WithView(MeshNodeThumbnailControl.FromNode(a, a.Path!));
                    return (UiControl?)container;
                }));
        return section;
    }
}
