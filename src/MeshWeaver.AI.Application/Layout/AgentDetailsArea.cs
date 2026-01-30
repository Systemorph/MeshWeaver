using System.Text;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Application.Layout;

public static class AgentDetailsArea
{
    public static LayoutDefinition AddAgentDetails(this LayoutDefinition layout)
    {
        return layout.WithView(nameof(AgentDetails), AgentDetails);
    }

    public static async Task<UiControl?> AgentDetails(LayoutAreaHost host, RenderingContext ctx, CancellationToken ct)
    {
        // Extract agent name from LayoutAreaReference.Id
        var agentName = ExtractAgentNameFromLayoutAreaId(host.Reference.Id);
        var meshQuery = host.Hub.ServiceProvider.GetService<MeshWeaver.Mesh.Services.IMeshQuery>();
        if (meshQuery == null)
        {
            return Controls.Stack
                .WithView(Controls.Title("Agent Details", 2), "Title")
                .WithView(Controls.Text("Agent service not available."), "ErrorMessage");
        }

        // Load agents using AgentOrderingHelper
        var agentDisplayInfos = await AgentOrderingHelper.QueryAgentsAsync(meshQuery, host.Hub.JsonSerializerOptions, null, null);
        var agents = agentDisplayInfos.Select(a => a.AgentConfiguration).ToList();
        var agent = agents.FirstOrDefault(a => a.Id == agentName);

        if (agent == null)
        {
            return Controls.Stack
                .WithView(Controls.Title("Agent Details", 2), "Title")
                .WithView(Controls.Text($"Agent '{agentName}' not found. Please verify the agent name and try again."), "ErrorMessage")
                .WithView(Controls.NavLink("← Back to Agent Overview", $"{host.Hub.Address}/Overview"), "BackLink");
        }

        return await CreateAgentDetailsView(agent, agents, host);
    }

    private static string ExtractAgentNameFromLayoutAreaId(object? id)
    {
        // Extract agent name from the LayoutAreaReference.Id
        // Expecting Id to contain the agent name directly
        return id?.ToString() ?? "";
    }

    private static Task<UiControl?> CreateAgentDetailsView(AgentConfiguration agent, IReadOnlyList<AgentConfiguration> agents, LayoutAreaHost host)
    {
        var markdown = GenerateAgentDetailsMarkdown(agent, agents, host);

        return Task.FromResult<UiControl?>(Controls.Stack
            .WithView(Controls.NavLink("← Back to Agent Overview", $"{host.Hub.Address}/Overview"), "BackLink")
            .WithView(Controls.Markdown(markdown), "Content")
            .WithView(CreateDelegationsSection(agent, agents, host)));
    }

    private static string GenerateAgentDetailsMarkdown(AgentConfiguration agent, IReadOnlyList<AgentConfiguration> agents, LayoutAreaHost host)
    {
        var markdown = new StringBuilder();

        // Title and Description
        var displayName = agent.DisplayName ?? agent.Id.Wordify();
        markdown.AppendLine($"# {displayName}");
        markdown.AppendLine();
        if (!string.IsNullOrEmpty(agent.Description))
        {
            markdown.AppendLine(agent.Description);
            markdown.AppendLine();
        }

        // Instructions
        markdown.AppendLine("## Instructions");
        markdown.AppendLine();
        markdown.AppendLine(agent.Instructions ?? "*No instructions configured*");
        markdown.AppendLine();

        // Attributes
        markdown.AppendLine("## Attributes");
        markdown.AppendLine();
        var attributes = GetAgentAttributesMarkdown(agent);
        markdown.AppendLine(attributes);
        markdown.AppendLine();

        // Delegations
        markdown.AppendLine("## Delegations");

        return markdown.ToString();
    }

    private static string GetAgentAttributesMarkdown(AgentConfiguration agent)
    {
        var attributes = new List<string>();

        if (agent.IsDefault)
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(40, 167, 69, 0.2); color: #28a745; border-radius: 16px; font-size: 13px; font-weight: 600;'>🟢 Default Agent</span>");

        if (agent.ExposedInNavigator)
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(3, 102, 214, 0.2); color: #0366d6; border-radius: 16px; font-size: 13px; font-weight: 600;'>🔵 Exposed in Navigator</span>");

        if (agent.Delegations is { Count: > 0 })
        {
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(255, 193, 7, 0.2); color: #ffc107; border-radius: 16px; font-size: 13px; font-weight: 600;'>🟡 With Delegations</span>");
        }

        if (!string.IsNullOrEmpty(agent.ContextMatchPattern))
        {
            attributes.Add($"<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(108, 117, 125, 0.2); color: #6c757d; border-radius: 16px; font-size: 13px; font-weight: 600;'>⚡ Context: {agent.ContextMatchPattern}</span>");
        }

        return attributes.Any() ? string.Join("", attributes) : "*No special attributes*";
    }

    private static UiControl CreateDelegationsSection(AgentConfiguration agent, IReadOnlyList<AgentConfiguration> agents, LayoutAreaHost host)
    {
        var delegationInfo = GetDelegationInfoForDisplay(agent, agents, host);

        return Controls.Html($"""
            <div style='margin: 24px 0; padding: 0 4px;'>
                {delegationInfo}
            </div>
            """);
    }

    internal static string GetAgentAttributesForDisplay(AgentConfiguration agent)
    {
        var attributes = new List<string>();

        if (agent.IsDefault)
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(40, 167, 69, 0.2); color: #28a745; border-radius: 16px; font-size: 13px; font-weight: 600;'>🟢 Default Agent</span>");

        if (agent.ExposedInNavigator)
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(3, 102, 214, 0.2); color: #0366d6; border-radius: 16px; font-size: 13px; font-weight: 600;'>🔵 Exposed in Navigator</span>");

        if (agent.Delegations is { Count: > 0 })
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(255, 193, 7, 0.2); color: #ffc107; border-radius: 16px; font-size: 13px; font-weight: 600;'>🟡 With Delegation</span>");

        return attributes.Any() ? string.Join("", attributes) : "<span style='color: var(--neutral-foreground-rest, #6a737d); opacity: 0.7; font-style: italic;'>No special attributes</span>";
    }

    internal static string GetDelegationInfoForDisplay(AgentConfiguration agent, IReadOnlyList<AgentConfiguration> agents, LayoutAreaHost host)
    {
        var sections = new List<string>();
        var agentsById = agents.ToDictionary(a => a.Id);

        // Section: Can delegate to
        if (agent.Delegations is { Count: > 0 })
        {
            var delegationsHtml = string.Join("", agent.Delegations.Select(d =>
            {
                var targetId = d.AgentPath.Split('/').Last();
                var targetAgent = agentsById.GetValueOrDefault(targetId);
                var agentLink = targetAgent != null
                    ? $"<a href='{host.Hub.Address}/AgentDetails/{targetId}' style='color: #0171ff; text-decoration: none; font-weight: 600;'>{targetAgent.DisplayName ?? targetId}</a>"
                    : $"<strong style='color: #0171ff;'>{targetId}</strong>";

                return $"<li style='margin: 8px 0; padding: 12px; background: var(--code-block-background-color, #f6f8fa); border-left: 4px solid #0171ff; border-radius: 4px; border: 1px solid var(--code-block-border-color, #e1e4e8);'>" +
                       $"<div style='margin-bottom: 4px;'>{agentLink}</div>" +
                       $"<div style='color: var(--neutral-foreground-rest, #292b36); opacity: 0.7; font-size: 13px;'>{d.Instructions}</div>" +
                       $"</li>";
            }));

            sections.Add($"""
                <div style='margin-bottom: 20px;'>
                    <h3 style='margin: 0 0 12px 0; color: var(--neutral-foreground-rest, #292b36); font-size: 16px;'>Can delegate to:</h3>
                    <ul style='margin: 0; padding: 0; list-style: none;'>{delegationsHtml}</ul>
                </div>
                """);
        }

        // Section: Delegations from (agents that delegate TO this agent)
        var delegationsFromList = new List<(AgentConfiguration sourceAgent, string reason)>();

        // Direct delegations from agents with Delegations
        foreach (var otherAgent in agents.Where(a => a.Id != agent.Id && a.Delegations is { Count: > 0 }))
        {
            var delegation = otherAgent.Delegations!.FirstOrDefault(d => d.AgentPath.Split('/').Last() == agent.Id);
            if (delegation != null)
            {
                delegationsFromList.Add((otherAgent, delegation.Instructions ?? string.Empty));
            }
        }

        if (delegationsFromList.Any())
        {
            var delegationsFromHtml = string.Join("", delegationsFromList.Select(item =>
            {
                var (sourceAgent, reason) = item;
                var agentLink = $"<a href='{host.Hub.Address}/AgentDetails/{sourceAgent.Id}' style='color: #6f42c1; text-decoration: none; font-weight: 600;'>{sourceAgent.DisplayName ?? sourceAgent.Id.Wordify()}</a>";

                return $"<li style='margin: 8px 0; padding: 12px; background: var(--code-block-background-color, #f6f8fa); border-left: 4px solid #6f42c1; border-radius: 4px; border: 1px solid var(--code-block-border-color, #e1e4e8);'>" +
                       $"<div style='margin-bottom: 4px;'>{agentLink}</div>" +
                       $"<div style='color: var(--neutral-foreground-rest, #292b36); opacity: 0.7; font-size: 13px;'>{reason}</div>" +
                       $"</li>";
            }));

            sections.Add($"""
                <div>
                    <h3 style='margin: 0 0 12px 0; color: var(--neutral-foreground-rest, #292b36); font-size: 16px;'>Delegations from:</h3>
                    <ul style='margin: 0; padding: 0; list-style: none;'>{delegationsFromHtml}</ul>
                </div>
                """);
        }

        return sections.Any() ? string.Join("", sections) : "<div style='color: var(--neutral-foreground-rest, #6a737d); opacity: 0.7; font-style: italic; padding: 12px; background: var(--code-block-background-color, #f6f8fa); border: 1px solid var(--code-block-border-color, #e1e4e8); border-radius: 6px;'>No delegations configured</div>";
    }
}
