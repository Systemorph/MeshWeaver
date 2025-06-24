using MeshWeaver.AI;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Utils;
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
        var mermaidDiagram = viewModel.GenerateMermaidDiagram(); return Controls.Stack
            .WithView(
                Controls.Markdown(
                    $"""
                    ## Agent Overview
                    ```mermaid
                    {mermaidDiagram}
                    ```
                    """), "DiagramArea");
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
        };        // Add agent nodes with proper font sizes and styling
        foreach (var agent in agents)
        {
            var nodeId = GetNodeId(agent.Name);
            var isDefault = agent == defaultAgent;
            var agentNameWordified = agent.Name.Wordify();
            var description = EscapeForMermaid(agent.Description);

            // Determine reachability and apply proper styling
            var nodeStyle = GetNodeStyle(agent, agents, defaultAgent);            // Create node with proper font sizes (14pt title, 12pt description) and left-aligned with consistent colors
            diagram.Add($"    {nodeId}[\"<div align='left'><b style='font-size:14pt;color:#ffffff'>{agentNameWordified}</b><br/><span style='font-size:12pt;color:#ffffff'>{description}</span></div>\"]");
            diagram.Add($"    style {nodeId} {nodeStyle}");
        }// Add delegation arrows from agents that implement IAgentWithDelegation
        foreach (var agent in agents.OfType<IAgentWithDelegation>())
        {
            var sourceNodeId = GetNodeId(agent.Name);
            foreach (var delegation in agent.Delegations)
            {
                var targetAgent = agents.FirstOrDefault(a => a.Name == delegation.AgentName);
                if (targetAgent != null)
                {
                    var targetNodeId = GetNodeId(targetAgent.Name);
                    var instruction = EscapeForMermaid(delegation.Instructions);
                    diagram.Add($"    {sourceNodeId} -->|\"delegate when: {instruction}\"| {targetNodeId}");
                }
            }
        }        // Add arrows from default agent to agents exposed in navigator
        if (defaultAgent != null)
        {
            var defaultNodeId = GetNodeId(defaultAgent.Name);
            foreach (var agent in agents.Where(a => a != defaultAgent && a.GetType().GetCustomAttributes(typeof(ExposedInNavigatorAttribute), false).Any()))
            {
                var exposedNodeId = GetNodeId(agent.Name);
                diagram.Add($"    {defaultNodeId} -.->|delegate| {exposedNodeId}");
            }
        }

        // Add click events for navigation to agent details
        foreach (var agent in agents)
        {
            var nodeId = GetNodeId(agent.Name);
            diagram.Add($"    click {nodeId} \"{hubAddress}/AgentDetails/{agent.Name}\"");
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

        // Don't truncate description - make it fully visible and left-aligned  
        return text
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\n", " ")
            .Replace("\r", "");
    }
    private static string GetNodeStyle(IAgentDefinition agent, List<IAgentDefinition> agents, IAgentDefinition? defaultAgent)
    {
        return agent switch
        {
            // Default agent - green
            _ when agent == defaultAgent => "fill:#4CAF50,stroke:#2E7D32,color:#fff",

            // Directly reachable from default agent via delegation
            _ when IsDirectlyReachableViaDelegation(agent, defaultAgent) => "fill:#2196F3,stroke:#1976D2,color:#fff",

            // Directly reachable from default agent via ExposedInNavigator attribute
            _ when IsExposedInNavigator(agent) => "fill:#FF9800,stroke:#F57C00,color:#fff",

            // Indirectly reachable (through other agents)
            _ when IsIndirectlyReachable(agent, agents, defaultAgent) => "fill:#9C27B0,stroke:#7B1FA2,color:#fff",

            // Not reachable
            _ => "fill:#757575,stroke:#424242,color:#fff"
        };
    }

    private static bool IsDirectlyReachableViaDelegation(IAgentDefinition agent, IAgentDefinition? defaultAgent)
    {
        if (defaultAgent is not IAgentWithDelegation delegatingAgent)
            return false;

        return delegatingAgent.Delegations.Any(d => d.AgentName == agent.Name);
    }

    private static bool IsExposedInNavigator(IAgentDefinition agent)
    {
        return agent.GetType().GetCustomAttributes(typeof(ExposedInNavigatorAttribute), false).Any();
    }

    private static bool IsIndirectlyReachable(IAgentDefinition agent, List<IAgentDefinition> agents, IAgentDefinition? defaultAgent)
    {
        // Check if the agent can be reached through other agents that are directly reachable
        var directlyReachableAgents = agents.Where(a =>
            a != defaultAgent &&
            (IsDirectlyReachableViaDelegation(a, defaultAgent) || IsExposedInNavigator(a))
        );

        foreach (var reachableAgent in directlyReachableAgents)
        {
            if (reachableAgent is IAgentWithDelegation delegating &&
                delegating.Delegations.Any(d => d.AgentName == agent.Name))
            {
                return true;
            }
        }

        return false;
    }
}
