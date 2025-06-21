using MeshWeaver.AI;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Application.Layout;

public static class AgentOverviewArea
{
    public static LayoutDefinition AddAgentOverview(this LayoutDefinition layout)
    {
        return layout.WithView(nameof(Overview), Overview);
    }

    public static UiControl Overview(LayoutAreaHost host, RenderingContext ctx)
    {
        var agents = host.Hub.ServiceProvider.GetService<IEnumerable<IAgentDefinition>>()?.Where(a => a != null).ToList() ?? [];

        // Create view model for the diagram
        var viewModel = new AgentOverviewViewModel(agents, host.Hub.Address);
        var mermaidDiagram = viewModel.GenerateMermaidDiagram();

        return Controls.Stack
            .WithView(
                Controls.Markdown(
                    $"""
                    ## Agent Overview
                    ```mermaid
                    {mermaidDiagram}
                    ```
                    """), "DiagramArea")
            .WithView(
                CreateAgentCards(agents, host), "AgentCards"
            );
    }

    private static UiControl CreateAgentCards(List<IAgentDefinition> agents, LayoutAreaHost host)
    {
        var defaultAgent = agents.FirstOrDefault(a => a.GetType().GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any());

        var cardGrid = Controls.LayoutGrid
            .WithClass("agent-overview-grid")
            .WithSkin(skin => skin
                .WithAdaptiveRendering(true)
                .WithJustify(JustifyContent.Center)
                .WithSpacing(20));

        return agents.Aggregate(cardGrid, (grid, agent) =>
        {
            var isDefault = agent == defaultAgent;
            var isExposed = agent.GetType().GetCustomAttributes(typeof(ExposedInNavigatorAttribute), false).Any();
            var hasDelegation = agent is IAgentWithDelegation;
            var hasDelegations = agent is IAgentWithDelegations;

            var cardStyle = isDefault ? "agent-card agent-card-default" : "agent-card";

            var card = Controls.Stack
                .WithClass(cardStyle)
                .WithView(Controls.Title(agent.AgentName, 3), "AgentTitle")
                .WithView(Controls.Text(agent.Description), "AgentDescription")
                .WithView(CreateAgentBadges(isDefault, isExposed, hasDelegation, hasDelegations), "AgentBadges")
                .WithView(
                    Controls.NavLink("View Details", $"{host.Hub.Address}/AgentDetails/{agent.AgentName}"),
                    "DetailsButton"
                );

            return grid.WithView(card, skin => skin.WithLg(4).WithMd(6).WithSm(12));
        });
    }

    private static UiControl CreateAgentBadges(bool isDefault, bool isExposed, bool hasDelegation, bool hasDelegations)
    {
        var badges = new List<UiControl>();

        if (isDefault)
            badges.Add(Controls.Text("🏠 Default Agent").WithClass("badge badge-default"));

        if (isExposed)
            badges.Add(Controls.Text("🔗 Exposed in Navigator").WithClass("badge badge-exposed"));

        if (hasDelegation)
            badges.Add(Controls.Text("➡️ Can Delegate").WithClass("badge badge-delegation"));

        if (hasDelegations)
            badges.Add(Controls.Text("🎯 Exposes Agents").WithClass("badge badge-delegations"));

        return badges.Aggregate(Controls.Stack.WithClass("agent-badges"), (stack, badge) =>
            stack.WithView(badge));
    }
}

public class AgentOverviewViewModel
{
    private readonly List<IAgentDefinition> agents;
    private readonly IAgentDefinition? defaultAgent;
    private readonly object hubAddress;

    public AgentOverviewViewModel(List<IAgentDefinition> agents, object hubAddress)
    {
        this.agents = agents;
        this.hubAddress = hubAddress;
        this.defaultAgent = agents.FirstOrDefault(a => a.GetType().GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any());
    }

    public string GenerateMermaidDiagram()
    {
        var diagram = new List<string>
        {
            "graph TD"
        };

        // Add agent nodes
        foreach (var agent in agents)
        {
            var nodeId = GetNodeId(agent.AgentName);
            var isDefault = agent == defaultAgent;
            var description = EscapeForMermaid(agent.Description);

            // Color default agent differently
            var nodeStyle = isDefault ? "fill:#4CAF50,stroke:#2E7D32,color:#fff" : "fill:#2196F3,stroke:#1976D2,color:#fff";

            diagram.Add($"    {nodeId}[\"{agent.AgentName}<br/>{description}\"]");
            diagram.Add($"    {nodeId} --> |click| {nodeId}_Details[Agent Details]");
            diagram.Add($"    style {nodeId} {nodeStyle}");
            diagram.Add($"    style {nodeId}_Details fill:#f9f9f9,stroke:#ccc");
        }

        // Add delegation arrows from agents that implement IAgentWithDelegation
        foreach (var agent in agents.OfType<IAgentWithDelegation>())
        {
            var sourceNodeId = GetNodeId(agent.AgentName);
            foreach (var delegation in agent.Delegations)
            {
                var targetAgent = agents.FirstOrDefault(a => a.AgentName == delegation.AgentName);
                if (targetAgent != null)
                {
                    var targetNodeId = GetNodeId(targetAgent.AgentName);
                    var instruction = EscapeForMermaid(delegation.Instructions);
                    diagram.Add($"    {sourceNodeId} -->|{instruction}| {targetNodeId}");
                }
            }
        }

        // Add arrows from default agent to agents exposed in navigator
        if (defaultAgent != null)
        {
            var defaultNodeId = GetNodeId(defaultAgent.AgentName);
            foreach (var agent in agents.Where(a => a != defaultAgent && a.GetType().GetCustomAttributes(typeof(ExposedInNavigatorAttribute), false).Any()))
            {
                var exposedNodeId = GetNodeId(agent.AgentName);
                diagram.Add($"    {defaultNodeId} -.->|navigate| {exposedNodeId}");
            }
        }

        // Add click events for navigation
        foreach (var agent in agents)
        {
            var nodeId = GetNodeId(agent.AgentName);
            diagram.Add($"    click {nodeId} \"{hubAddress}/AgentDetails/{agent.AgentName}\"");
        }

        return string.Join("\n", diagram);
    }

    private static string GetNodeId(string agentName)
    {
        return agentName.Replace(" ", "_").Replace("-", "_");
    }

    private static string EscapeForMermaid(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        // Limit description length and escape special characters
        var truncated = text.Length > 50 ? text[..47] + "..." : text;
        return truncated
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\n", " ")
            .Replace("\r", "");
    }
}
