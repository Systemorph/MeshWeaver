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
        var htmlDiagram = viewModel.GenerateHtmlDiagram();

        return Controls.Stack
            .WithView(Controls.Title("Agent Overview", 2))
            .WithView(
                Controls.Html($"""
                    <div style='margin: 12px 0; padding: 0 4px;'>
                    </div>
                    """), "Spacer")
            .WithView(
                Controls.Html(htmlDiagram), "DiagramArea");
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
    public string GenerateHtmlDiagram()
    {
        // Build a simple vertical layout with arrows and HTML boxes
        var html = new System.Text.StringBuilder();
        html.AppendLine("<div style='display: flex; flex-direction: column; align-items: center; gap: 32px; margin-top: 16px;'>");

        // Render all agents, showing arrows for delegations
        var rendered = new HashSet<string>();
        // Render default agent at the top
        var defaultAgentBox = agents.FirstOrDefault(a => a == defaultAgent);
        if (defaultAgentBox != null)
        {
            html.AppendLine(RenderAgentBox(defaultAgentBox, isDefault: true));
            rendered.Add(defaultAgentBox.Name);
        }

        // Render directly reachable agents (delegates to)
        var directlyReachable = agents.Where(a => IsDirectlyReachableViaDelegation(a, defaultAgent)).ToList();
        foreach (var agent in directlyReachable)
        {
            html.AppendLine(RenderArrow());
            html.AppendLine(RenderAgentBox(agent, isDefault: false));
            rendered.Add(agent.Name);
        }

        // Render all other agents (not default, not directly reachable)
        foreach (var agent in agents)
        {
            if (!rendered.Contains(agent.Name))
            {
                html.AppendLine(RenderArrow());
                html.AppendLine(RenderAgentBox(agent, isDefault: false));
            }
        }

        html.AppendLine("</div>");
        return html.ToString();
    }

    private string RenderAgentBox(IAgentDefinition agent, bool isDefault)
    {
        var name = agent.Name.Wordify();
        var description = agent.Description;
        string background, border, titleColor, descColor;
        if (isDefault)
        {
            background = "#f8f4fd";
            border = "#6f42c1";
            titleColor = "#6f42c1";
            descColor = "#24292e";
        }
        else
        {
            background = "#f0fff4";
            border = "#28a745";
            titleColor = "#155724";
            descColor = "#24292e";
        }
        var url = $"{hubAddress}/AgentDetails/{agent.Name}";
        return $"<a href='{url}' style='text-decoration:none;display:block;min-width:340px;max-width:480px;'>" +
               $"<div style='padding:24px 24px 18px 24px; background:{background}; border:2px solid {border}; border-radius:10px; box-shadow:0 2px 8px #0001; margin:0 0 0 0; transition:box-shadow 0.2s,border-color 0.2s;cursor:pointer;'>" +
               $"<div style='font-size:22px; font-weight:800; margin-bottom:8px; color:{titleColor};'>{name}</div>" +
               $"<div style='font-size:17px; line-height:1.22; color:{descColor};'>{description}</div>" +
               "</div></a>";
    }

    private string RenderArrow()
    {
        // Vertical flex with label and Unicode arrow, using a neutral color for visibility in both themes
        return @"<div style='display:flex; flex-direction:column; align-items:center; justify-content:center; margin:0; gap:2px;'>
            <span style='background:rgba(120,120,120,0.85); color:#fff; font-size:15px; padding:2px 12px; border-radius:6px;'>delegates to</span>
            <span style='font-size:28px; color:#888; user-select:none; line-height:1;'>↓</span>
        </div>";
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

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;

        return text.Substring(0, maxLength - 3) + "...";
    }
    // GetNodeStyle is no longer used

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
