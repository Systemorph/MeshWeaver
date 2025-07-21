using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using System.Text;

namespace MeshWeaver.AI.Application.Layout;

public static class AgentDetailsArea
{
    public static LayoutDefinition AddAgentDetails(this LayoutDefinition layout)
    {
        return layout.WithView(nameof(AgentDetails), AgentDetails);
    }
    public static async Task<UiControl> AgentDetails(LayoutAreaHost host, RenderingContext ctx, CancellationToken ct)
    {        // Extract agent name from LayoutAreaReference.Id
        var agentName = ExtractAgentNameFromLayoutAreaId(host.Reference.Id);
        var agents = host.Hub.ServiceProvider.GetService<IEnumerable<IAgentDefinition>>()?.ToList() ?? [];
        var agent = agents.FirstOrDefault(a => a.Name == agentName);

        if (agent == null)
        {
            return Controls.Stack
                .WithView(Controls.Title("Agent Details", 2), "Title")
                .WithView(Controls.Text($"Agent '{agentName}' not found. Please verify the agent name and try again."), "ErrorMessage")
                .WithView(Controls.NavLink("← Back to Agent Overview", $"{host.Hub.Address}/Overview"), "BackLink");
        }

        return await CreateAgentDetailsView(agent, host, ct);
    }
    private static string ExtractAgentNameFromLayoutAreaId(object? id)
    {
        // Extract agent name from the LayoutAreaReference.Id
        // Expecting Id to contain the agent name directly
        return id?.ToString() ?? "";
    }
    private static async Task<UiControl> CreateAgentDetailsView(IAgentDefinition agent, LayoutAreaHost host, CancellationToken _)
    {
        var agents = await host.Hub.ServiceProvider.GetRequiredService<IAgentChatFactory>().GetAgentsAsync();

        var markdown = GenerateAgentDetailsMarkdown(agent, agents, host);

        return Controls.Stack
            .WithView(Controls.NavLink("← Back to Agent Overview", $"{host.Hub.Address}/Overview"), "BackLink")
            .WithView(Controls.Markdown(markdown), "Content")
            .WithView(CreateDelegationsSection(agent, agents, host));

    }

    private static string GenerateAgentDetailsMarkdown(IAgentDefinition agent, IReadOnlyDictionary<string, IAgentDefinition> agents, LayoutAreaHost host)
    {
        var markdown = new StringBuilder();

        // Title and Description
        markdown.AppendLine($"# {agent.Name.Wordify()}");
        markdown.AppendLine();
        if (!string.IsNullOrEmpty(agent.Description))
        {
            markdown.AppendLine(agent.Description);
            markdown.AppendLine();
        }

        // Instructions
        markdown.AppendLine("## Instructions");
        markdown.AppendLine();
        markdown.AppendLine("```markdown");
        markdown.AppendLine(agent.Instructions);
        markdown.AppendLine("```");
        markdown.AppendLine();

        // Attributes
        markdown.AppendLine("## Attributes");
        markdown.AppendLine();
        var attributes = GetAgentAttributesMarkdown(agent);
        markdown.AppendLine(attributes);
        markdown.AppendLine();

        // Plugins
        markdown.AppendLine("## Plugins");
        markdown.AppendLine();
        var plugins = GetPluginsMarkdown(agent);
        markdown.AppendLine(plugins);
        markdown.AppendLine();

        // Delegations
        markdown.AppendLine("## Delegations");

        return markdown.ToString();
    }
    private static string GetAgentAttributesMarkdown(IAgentDefinition agent)
    {
        var attributes = new List<string>();
        var type = agent.GetType();

        if (type.GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any())
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(40, 167, 69, 0.2); color: #28a745; border-radius: 16px; font-size: 13px; font-weight: 600;'>🟢 Default Agent</span>");

        if (type.GetCustomAttributes(typeof(ExposedInNavigatorAttribute), false).Any())
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(3, 102, 214, 0.2); color: #0366d6; border-radius: 16px; font-size: 13px; font-weight: 600;'>🔵 Exposed in Navigator</span>");

        if (agent is IAgentWithDelegations)
        {
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(255, 193, 7, 0.2); color: #ffc107; border-radius: 16px; font-size: 13px; font-weight: 600;'>🟡 With Delegations</span>");
        }

        return attributes.Any() ? string.Join("", attributes) : "*No special attributes*";
    }

    private static string GetPluginsMarkdown(IAgentDefinition agent)
    {
        // Try to get plugins from IAgentWithPlugins interface
        var kernelPlugins = new List<KernelPlugin>();
        if (agent is IAgentWithPlugins agentWithPlugins)
        {
            try
            {
                kernelPlugins = agentWithPlugins.GetPlugins(null!)?.ToList() ?? [];
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to get plugins for {agent.Name}: {ex.Message}");
            }
        }

        if (!kernelPlugins.Any())
        {
            return "*No plugins configured*";
        }

        var pluginsHtml = string.Join("", kernelPlugins.Select(plugin =>
        {
            var functionsCount = plugin.Count();
            var functionsText = functionsCount == 1 ? "function" : "functions";

            // Generate function details
            var functionsHtml = string.Join("", plugin.Select(function =>
            {
                var parameters = function.Metadata.Parameters;
                var parametersHtml = parameters.Any()
                    ? string.Join("", parameters.Select(p =>
                        $"<div style='margin: 4px 0; padding: 6px 8px; background: var(--code-block-background-color, #f6f8fa); border-radius: 3px; font-size: 12px; border: 1px solid var(--code-block-border-color, #e1e4e8);'>" +
                        $"<code style='color: #0171ff; font-weight: 600;'>{p.Name}</code>" +
                        $"<span style='color: var(--neutral-foreground-rest, #292b36); opacity: 0.8;'> ({p.ParameterType?.Name ?? "object"})</span>" +
                        (!string.IsNullOrEmpty(p.Description) ? $"<br/><span style='color: var(--neutral-foreground-rest, #292b36); opacity: 0.7; font-style: italic;'>{p.Description}</span>" : "") +
                        "</div>"))
                    : "<div style='font-size: 12px; color: var(--neutral-foreground-rest, #292b36); opacity: 0.6; font-style: italic;'>No parameters</div>";

                return $"<div style='margin: 8px 0; padding: 8px; border: 1px solid var(--code-block-border-color, #e1e4e8); border-radius: 4px; background: var(--neutral-layer-1, #ffffff);'>" +
                       $"<div style='font-weight: 600; color: var(--neutral-foreground-rest, #292b36); margin-bottom: 4px;'>{function.Name}</div>" +
                       (!string.IsNullOrEmpty(function.Description) ? $"<div style='color: var(--neutral-foreground-rest, #292b36); opacity: 0.8; font-size: 13px; margin-bottom: 6px;'>{function.Description}</div>" : "") +
                       $"<div style='margin-top: 6px;'><strong style='font-size: 12px; color: var(--neutral-foreground-rest, #292b36);'>Parameters:</strong></div>" +
                       parametersHtml +
                       "</div>";
            }));

            return $"<div style='margin: 12px 0; padding: 12px; background: var(--code-block-background-color, #f6f8fa); border-left: 4px solid #0171ff; border-radius: 4px; border: 1px solid var(--code-block-border-color, #e1e4e8);'>" +
                   $"<div style='font-weight: 600; color: var(--neutral-foreground-rest, #292b36); margin-bottom: 8px; font-size: 16px;'>{plugin.Name}</div>" +
                   $"<div style='color: var(--neutral-foreground-rest, #292b36); opacity: 0.7; font-size: 13px; margin-bottom: 12px;'>{functionsCount} {functionsText}</div>" +
                   functionsHtml +
                   "</div>";
        }));

        return pluginsHtml;
    }

    private static UiControl CreateDelegationsSection(IAgentDefinition agent, IReadOnlyDictionary<string, IAgentDefinition> agents, LayoutAreaHost host)
    {
        var delegationInfo = GetDelegationInfoForDisplay(agent, agents, host);

        return Controls.Html($"""
            <div style='margin: 24px 0; padding: 0 4px;'>                
                {delegationInfo}
            </div>
            """);
    }
    internal static string GetAgentAttributesForDisplay(IAgentDefinition agent)
    {
        var attributes = new List<string>();
        var type = agent.GetType();

        if (type.GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any())
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(40, 167, 69, 0.2); color: #28a745; border-radius: 16px; font-size: 13px; font-weight: 600;'>🟢 Default Agent</span>");

        if (type.GetCustomAttributes(typeof(ExposedInNavigatorAttribute), false).Any())
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(3, 102, 214, 0.2); color: #0366d6; border-radius: 16px; font-size: 13px; font-weight: 600;'>🔵 Exposed in Navigator</span>");

        if (agent is IAgentWithDelegations)
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(255, 193, 7, 0.2); color: #ffc107; border-radius: 16px; font-size: 13px; font-weight: 600;'>🟡 With Delegation</span>");

        if (agent is IAgentWithDelegations)
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(253, 126, 20, 0.2); color: #fd7e14; border-radius: 16px; font-size: 13px; font-weight: 600;'>🟠 With Delegations</span>");

        return attributes.Any() ? string.Join("", attributes) : "<span style='color: var(--neutral-foreground-rest, #6a737d); opacity: 0.7; font-style: italic;'>No special attributes</span>";
    }
    internal static string GetDelegationInfoForDisplay(IAgentDefinition agent, IReadOnlyDictionary<string, IAgentDefinition> agents, LayoutAreaHost host)
    {
        var sections = new List<string>();

        // Section: Can delegate to
        if (agent is IAgentWithDelegations agentWithDelegation)
        {
            var delegationsList = agentWithDelegation.Delegations.ToList();
            if (delegationsList.Any())
            {
                var delegationsHtml = string.Join("", delegationsList.Select(d =>
                {
                    var targetAgent = agents.GetValueOrDefault(d.AgentName);
                    var agentLink = targetAgent != null
                        ? $"<a href='{host.Hub.Address}/AgentDetails/{d.AgentName}' style='color: #0171ff; text-decoration: none; font-weight: 600;'>{d.AgentName}</a>"
                        : $"<strong style='color: #0171ff;'>{d.AgentName}</strong>";

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
        }

        // Section: Exposes agents
        if (agent is IAgentWithDelegations agentWithDelegations)
        {
            var exposedAgents = agentWithDelegations.Delegations.ToList();
            if (exposedAgents.Any())
            {
                var exposedHtml = string.Join("", exposedAgents.Select(a =>
                {
                    var agentLink = $"<a href='{host.Hub.Address}/AgentDetails/{a.AgentName}' style='color: #28a745; text-decoration: none; font-weight: 600;'>{a.AgentName}</a>";

                    return $"<li style='margin: 8px 0; padding: 12px; background: var(--code-block-background-color, #f6f8fa); border-left: 4px solid #28a745; border-radius: 4px; border: 1px solid var(--code-block-border-color, #e1e4e8);'>" +
                           $"<div style='margin-bottom: 4px;'>{agentLink}</div>" +
                           $"<div style='color: var(--neutral-foreground-rest, #292b36); opacity: 0.7; font-size: 13px;'>{a.Instructions}</div>" +
                           $"</li>";
                }));

                sections.Add($"""
                    <div style='margin-bottom: 20px;'>
                        <h3 style='margin: 0 0 12px 0; color: var(--neutral-foreground-rest, #292b36); font-size: 16px;'>Exposes agents:</h3>
                        <ul style='margin: 0; padding: 0; list-style: none;'>{exposedHtml}</ul>
                    </div>
                    """);
            }
        }

        // Section: Delegations from (agents that delegate TO this agent)
        var delegationsFromList = new List<(IAgentDefinition sourceAgent, string reason)>();

        // Direct delegations from agents with IAgentWithDelegations
        var directDelegationsFrom = agents.Values.OfType<IAgentWithDelegations>()
            .Where(a => a != agent && a.Delegations.Any(d => d.AgentName == agent.Name))
            .Select(a => ((IAgentDefinition)a, a.Delegations.First(d => d.AgentName == agent.Name).Instructions))
            .ToList();
        delegationsFromList.AddRange(directDelegationsFrom);

        if (delegationsFromList.Any())
        {
            var delegationsFromHtml = string.Join("", delegationsFromList.Select(item =>
            {
                var (sourceAgent, reason) = item;
                var agentLink = $"<a href='{host.Hub.Address}/AgentDetails/{sourceAgent.Name}' style='color: #6f42c1; text-decoration: none; font-weight: 600;'>{sourceAgent.Name.Wordify()}</a>";

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
