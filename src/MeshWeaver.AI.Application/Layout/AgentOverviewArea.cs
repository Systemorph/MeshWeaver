#nullable enable
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
        var mermaidDiagram = viewModel.GenerateMermaidDiagram();

        var markdown = $"""
            ## Agent Overview

            ```mermaid
            {mermaidDiagram}
            ```
            """;

        return Controls.Markdown(markdown);
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
        var mermaid = new System.Text.StringBuilder();
        mermaid.AppendLine("flowchart TD");

        // Add nodes for all agents
        var defaultAgentNodes = new List<string>();
        var regularAgentNodes = new List<string>();
        var unreachableAgentNodes = new List<string>();

        foreach (var agent in agents)
        {
            var nodeId = GetNodeId(agent.Name);
            var name = EscapeForMermaid(agent.Name.Wordify());
            var description = EscapeForMermaid(agent.Description ?? string.Empty);
            var isDefault = agent == defaultAgent;
            var isDirectlyReachable = IsDirectlyReachableFromDefault(agent, defaultAgent);

            // Create a clickable node with name and description
            var nodeLabel = $"{name}<br/><small>{TruncateText(description, 60)}</small>";

            mermaid.AppendLine($"    {nodeId}[\"{nodeLabel}\"]");

            // Collect nodes for styling
            if (isDefault)
                defaultAgentNodes.Add(nodeId);
            else if (isDirectlyReachable)
                regularAgentNodes.Add(nodeId);
            else
                unreachableAgentNodes.Add(nodeId);
        }

        // Add edges for delegations
        var processedEdges = new HashSet<string>();

        // Start with default agent if it exists
        if (defaultAgent != null)
        {
            var defaultNodeId = GetNodeId(defaultAgent.Name);

            // Add delegations from default agent to explicitly configured delegations
            if (defaultAgent is IAgentWithDelegations defaultDelegating)
            {
                foreach (var delegation in defaultDelegating.Delegations)
                {
                    var targetAgent = agents.FirstOrDefault(a => a.Name == delegation.AgentName);
                    if (targetAgent != null)
                    {
                        var targetNodeId = GetNodeId(targetAgent.Name);
                        var edgeKey = $"{defaultNodeId}->{targetNodeId}";

                        if (!processedEdges.Contains(edgeKey))
                        {
                            mermaid.AppendLine($"    {defaultNodeId} -->|delegates to| {targetNodeId}");
                            processedEdges.Add(edgeKey);
                        }
                    }
                }
            }

            // Add delegations from default agent to agents with ExposedInNavigatorAttribute
            foreach (var agent in agents)
            {
                if (agent != defaultAgent && IsExposedInNavigator(agent))
                {
                    var targetNodeId = GetNodeId(agent.Name);
                    var edgeKey = $"{defaultNodeId}->{targetNodeId}";

                    if (!processedEdges.Contains(edgeKey))
                    {
                        mermaid.AppendLine($"    {defaultNodeId} -->|delegates to| {targetNodeId}");
                        processedEdges.Add(edgeKey);
                    }
                }
            }
        }

        // Add delegations from other agents
        foreach (var agent in agents)
        {
            if (agent is IAgentWithDelegations delegating && agent != defaultAgent)
            {
                var sourceNodeId = GetNodeId(agent.Name);

                foreach (var delegation in delegating.Delegations)
                {
                    var targetAgent = agents.FirstOrDefault(a => a.Name == delegation.AgentName);
                    if (targetAgent != null)
                    {
                        var targetNodeId = GetNodeId(targetAgent.Name);
                        var edgeKey = $"{sourceNodeId}->{targetNodeId}";

                        if (!processedEdges.Contains(edgeKey))
                        {
                            mermaid.AppendLine($"    {sourceNodeId} -->|delegates to| {targetNodeId}");
                            processedEdges.Add(edgeKey);
                        }
                    }
                }
            }
        }

        // Add click events for navigation
        foreach (var agent in agents)
        {
            var nodeId = GetNodeId(agent.Name);
            var url = $"{hubAddress}/AgentDetails/{agent.Name}";
            mermaid.AppendLine($"    click {nodeId} \"{url}\" \"Open {agent.Name} details\"");
        }

        // Add styling
        mermaid.AppendLine();
        mermaid.AppendLine("    classDef defaultAgent fill:#f8f4fd,stroke:#6f42c1,stroke-width:3px,color:#6f42c1");
        mermaid.AppendLine("    classDef regularAgent fill:#f0fff4,stroke:#28a745,stroke-width:2px,color:#155724");
        mermaid.AppendLine("    classDef unreachableAgent fill:#fff5f5,stroke:#dc3545,stroke-width:2px,color:#721c24");

        // Apply styles to nodes
        if (defaultAgentNodes.Any())
        {
            mermaid.AppendLine($"    class {string.Join(",", defaultAgentNodes)} defaultAgent");
        }
        if (regularAgentNodes.Any())
        {
            mermaid.AppendLine($"    class {string.Join(",", regularAgentNodes)} regularAgent");
        }
        if (unreachableAgentNodes.Any())
        {
            mermaid.AppendLine($"    class {string.Join(",", unreachableAgentNodes)} unreachableAgent");
        }

        return mermaid.ToString();
    }

    private static string GetNodeId(string agentName)
    {
        return agentName.Replace(" ", "_").Replace("-", "_").Replace(".", "_");
    }

    private static string EscapeForMermaid(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        return text
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\n", " ")
            .Replace("\r", "")
            .Replace("[", "&#91;")
            .Replace("]", "&#93;");
    }

    private static string TruncateText(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;

        return text.Substring(0, maxLength - 3) + "...";
    }

    private static bool IsDirectlyReachableViaDelegation(IAgentDefinition agent, IAgentDefinition? defaultAgent)
    {
        if (defaultAgent is not IAgentWithDelegations delegatingAgent)
            return false;

        return delegatingAgent.Delegations.Any(d => d.AgentName == agent.Name);
    }

    private static bool IsExposedInNavigator(IAgentDefinition agent)
    {
        return agent.GetType().GetCustomAttributes(typeof(ExposedInNavigatorAttribute), false).Any();
    }

    private static bool IsDirectlyReachableFromDefault(IAgentDefinition agent, IAgentDefinition? defaultAgent)
    {
        if (defaultAgent == null || agent == defaultAgent)
            return false;

        // Check if agent is in explicit delegations
        var isExplicitlyDelegated = IsDirectlyReachableViaDelegation(agent, defaultAgent);

        // Check if agent is exposed in navigator
        var isExposedInNavigator = IsExposedInNavigator(agent);

        return isExplicitlyDelegated || isExposedInNavigator;
    }
}
