using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Namotion.Reflection;
using System.Reflection;

namespace MeshWeaver.AI.Application.Layout;

public static class AgentDetailsArea
{
    public static LayoutDefinition AddAgentDetails(this LayoutDefinition layout)
    {
        return layout.WithView(nameof(AgentDetails), AgentDetails);
    }
    public static UiControl AgentDetails(LayoutAreaHost host, RenderingContext ctx)
    {        // Extract agent name from LayoutAreaReference.Id
        var agentName = ExtractAgentNameFromLayoutAreaId(host.Reference.Id);
        var agents = host.Hub.ServiceProvider.GetService<IEnumerable<IAgentDefinition>>()?.ToList() ?? [];
        var agent = agents.FirstOrDefault(a => a.AgentName == agentName);

        if (agent == null)
        {
            return Controls.Stack
                .WithView(Controls.Title("Agent Details", 2), "Title")
                .WithView(Controls.Text($"Agent '{agentName}' not found. Please verify the agent name and try again."), "ErrorMessage")
                .WithView(Controls.NavLink("← Back to Agent Overview", $"{host.Hub.Address}/Overview"), "BackLink");
        }

        return CreateAgentDetailsView(agent, host);
    }
    private static string ExtractAgentNameFromLayoutAreaId(object id)
    {
        // Extract agent name from the LayoutAreaReference.Id
        // Expecting Id to contain the agent name directly
        return id?.ToString() ?? "";
    }
    private static UiControl CreateAgentDetailsView(IAgentDefinition agent, LayoutAreaHost host)
    {
        var agents = host.Hub.ServiceProvider.GetService<IEnumerable<IAgentDefinition>>()?.ToList() ?? [];

        return Controls.Stack
            .WithView(Controls.NavLink("← Back to Agent Overview", $"{host.Hub.Address}/Overview"), "BackLink")
            .WithView(Controls.Title($"{agent.AgentName.Wordify()}", 1), "Title")
            .WithView(Controls.Text($"{agent.Description ?? "No description available"}"), "Description")
            .WithView(CreateInstructionsSection(agent), "Instructions")
            .WithView(CreateAttributesSection(agent), "Attributes")
            .WithView(CreatePluginsSection(agent), "Plugins")
            .WithView(CreateDelegationsSection(agent, agents, host), "Delegations");
    }
    private static UiControl CreateInstructionsSection(IAgentDefinition agent)
    {
        var instructions = agent.Instructions ?? "No instructions provided.";
        var hasInstructions = !string.IsNullOrEmpty(agent.Instructions);

        return Controls.Html($"""
            <div style='margin: 16px 0; padding: 16px; background: var(--color-canvas-subtle); border: 1px solid var(--color-border-default); border-radius: 6px;'>
                <h3 style='margin: 0 0 12px 0; color: var(--color-fg-default); font-size: 16px; font-weight: 600;'>Instructions</h3>
                <div style='background: var(--color-neutral-muted); padding: 12px; border-radius: 4px; border-left: 3px solid var(--color-accent-emphasis);'>
                    <pre style='margin: 0; font-family: var(--fontStack-monospace); font-size: 13px; line-height: 1.45; color: var(--color-fg-default); white-space: pre-wrap;'>{instructions}</pre>
                </div>
            </div>
            """);
    }
    private static UiControl CreateAttributesSection(IAgentDefinition agent)
    {
        var attributes = GetAgentAttributesForDisplay(agent);

        return Controls.Html($"""
            <div style='margin: 16px 0; padding: 16px; background: var(--color-canvas-subtle); border: 1px solid var(--color-border-default); border-radius: 6px;'>
                <h3 style='margin: 0 0 12px 0; color: var(--color-fg-default); font-size: 16px; font-weight: 600;'>Attributes</h3>
                <div style='display: flex; flex-wrap: wrap; gap: 8px;'>
                    {attributes}
                </div>
            </div>
            """);
    }
    private static UiControl CreatePluginsSection(IAgentDefinition agent)
    {
        // Try to get plugins from IAgentWithPlugins interface (new interface returning KernelPlugin)
        var kernelPlugins = new List<KernelPlugin>();
        if (agent is IAgentWithPlugins agentWithPlugins)
        {
            try
            {
                kernelPlugins = agentWithPlugins.GetPlugins()?.ToList() ?? [];
            }
            catch (Exception ex)
            {
                // In case plugin creation fails, log but continue
                System.Diagnostics.Debug.WriteLine($"Failed to get plugins for {agent.AgentName}: {ex.Message}");
            }
        }
        var pluginsList = kernelPlugins.Any()
            ? string.Join("", kernelPlugins.Select(plugin =>
            {
                var functionsHtml = string.Join("", plugin.Select(function =>
                {
                    var parametersHtml = function.Metadata.Parameters.Any()
                        ? string.Join("", function.Metadata.Parameters.Select(param =>
                            $"<div style='margin: 4px 0; padding: 6px; background: var(--color-neutral-muted); border-radius: 3px;'>" +
                            $"<code style='color: var(--color-accent-fg); font-weight: 600;'>{param.Name}</code>" +
                            $"<span style='color: var(--color-fg-muted); margin-left: 8px;'>({param.ParameterType?.Name ?? "string"})</span>" +
                            $"{(param.IsRequired ? "<span style='color: var(--color-danger-fg); margin-left: 4px; font-size: 10px;'>*required</span>" : "")}" +
                            $"{(!string.IsNullOrEmpty(param.Description) ? $"<div style='color: var(--color-fg-muted); font-size: 11px; margin-top: 2px;'>{param.Description}</div>" : "")}" +
                            $"</div>"))
                        : "<div style='color: var(--color-fg-muted); font-style: italic; font-size: 11px;'>No parameters</div>";

                    return $"<div style='margin: 8px 0; padding: 8px; background: var(--color-canvas-default); border: 1px solid var(--color-border-muted); border-radius: 4px;'>" +
                           $"<div style='font-weight: 600; color: var(--color-fg-default); margin-bottom: 4px;'>" +
                           $"<code style='color: var(--color-accent-emphasis);'>{function.Name}</code>" +
                           $"</div>" +
                           $"{(!string.IsNullOrEmpty(function.Description) ? $"<div style='color: var(--color-fg-default); font-size: 12px; margin-bottom: 6px;'>{function.Description}</div>" : "")}" +
                           $"<div style='margin-top: 6px;'>" +
                           $"<div style='font-size: 11px; font-weight: 600; color: var(--color-fg-muted); margin-bottom: 4px;'>Parameters:</div>" +
                           $"{parametersHtml}" +
                           $"</div>" +
                           $"</div>";
                }));

                return $"<li style='margin: 8px 0; padding: 12px; background: var(--color-canvas-subtle); border: 1px solid var(--color-border-default); border-radius: 6px;'>" +
                       $"<div style='font-weight: 600; color: var(--color-fg-default); margin-bottom: 8px; font-size: 14px;'>{plugin.Name}</div>" +
                       $"<div style='margin-top: 8px;'>{functionsHtml}</div>" +
                       $"</li>";
            }))
            : "<li style='margin: 4px 0; padding: 8px 12px; background: var(--color-attention-subtle); border: 1px solid var(--color-attention-muted); border-radius: 4px; color: var(--color-attention-fg);'>No plugins configured</li>";

        return Controls.Html($"""
            <div style='margin: 16px 0; padding: 16px; background: var(--color-canvas-subtle); border: 1px solid var(--color-border-default); border-radius: 6px;'>
                <h3 style='margin: 0 0 12px 0; color: var(--color-fg-default); font-size: 16px; font-weight: 600;'>Plugins</h3>
                <ul style='margin: 0; padding: 0; list-style: none;'>
                    {pluginsList}
                </ul>
            </div>
            """);
    }
    private static UiControl CreateDelegationsSection(IAgentDefinition agent, List<IAgentDefinition> agents, LayoutAreaHost host)
    {
        var delegationInfo = GetDelegationInfoForDisplay(agent, agents, host);

        return Controls.Html($"""
            <div style='margin: 16px 0; padding: 16px; background: var(--color-canvas-subtle); border: 1px solid var(--color-border-default); border-radius: 6px;'>
                <h3 style='margin: 0 0 12px 0; color: var(--color-fg-default); font-size: 16px; font-weight: 600;'>Delegations</h3>
                {delegationInfo}
            </div>
            """);
    }
    internal static string GetAgentAttributesForDisplay(IAgentDefinition agent)
    {
        var attributes = new List<string>();
        var type = agent.GetType();

        if (type.GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any())
            attributes.Add("<span style='display: inline-block; padding: 4px 8px; background: var(--color-success-subtle); color: var(--color-success-fg); border-radius: 4px; font-size: 12px; font-weight: 500;'>Default Agent</span>");

        if (type.GetCustomAttributes(typeof(ExposedInNavigatorAttribute), false).Any())
            attributes.Add("<span style='display: inline-block; padding: 4px 8px; background: var(--color-accent-subtle); color: var(--color-accent-fg); border-radius: 4px; font-size: 12px; font-weight: 500;'>Exposed in Navigator</span>");

        if (agent is IAgentWithDelegation)
            attributes.Add("<span style='display: inline-block; padding: 4px 8px; background: var(--color-done-subtle); color: var(--color-done-fg); border-radius: 4px; font-size: 12px; font-weight: 500;'>With Delegation</span>");

        if (agent is IAgentWithDelegations)
            attributes.Add("<span style='display: inline-block; padding: 4px 8px; background: var(--color-sponsors-subtle); color: var(--color-sponsors-fg); border-radius: 4px; font-size: 12px; font-weight: 500;'>With Delegations</span>");

        return attributes.Any() ? string.Join(" ", attributes) : "<span style='color: var(--color-fg-muted); font-style: italic;'>No special attributes</span>";
    }
    private static string? GetTypeXmlDocsSummary(Type type)
    {
        try
        {
            // First check for Description attribute
            var descriptionAttribute = type.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
            if (descriptionAttribute != null && !string.IsNullOrEmpty(descriptionAttribute.Description))
                return descriptionAttribute.Description;

            // Try to get XML documentation summary using Namotion.Reflection
            var summary = type.GetXmlDocsSummary();
            if (!string.IsNullOrEmpty(summary))
                return summary;

            return null;
        }
        catch
        {
            return null;
        }
    }

    internal static string GetDelegationInfoForDisplay(IAgentDefinition agent, List<IAgentDefinition> agents, LayoutAreaHost host)
    {
        var sections = new List<string>();

        // Section: Can delegate to
        if (agent is IAgentWithDelegation agentWithDelegation)
        {
            var delegationsList = agentWithDelegation.Delegations?.ToList() ?? [];
            if (delegationsList.Any())
            {
                var delegationsHtml = string.Join("", delegationsList.Select(d =>
                {
                    var targetAgent = agents.FirstOrDefault(a => a.AgentName == d.AgentName);
                    var agentLink = targetAgent != null
                        ? $"<a href='{host.Hub.Address}/AgentDetails/{d.AgentName}' style='color: var(--color-accent-fg); text-decoration: none; font-weight: 600;'>{d.AgentName}</a>"
                        : $"<span style='font-weight: 600; color: var(--color-accent-fg);'>{d.AgentName}</span>";

                    return $"<li style='margin: 8px 0; padding: 12px; background: var(--color-accent-subtle); border-radius: 4px; border-left: 3px solid var(--color-accent-emphasis);'>" +
                           $"<div style='margin-bottom: 4px;'>{agentLink}</div>" +
                           $"<div style='color: var(--color-fg-muted); font-size: 13px;'>{d.Instructions}</div>" +
                           $"</li>";
                }));

                sections.Add($"""
                    <div style='margin-bottom: 16px;'>
                        <h4 style='margin: 0 0 8px 0; color: var(--color-fg-default); font-size: 14px; font-weight: 600;'>Can delegate to:</h4>
                        <ul style='margin: 0; padding: 0; list-style: none;'>{delegationsHtml}</ul>
                    </div>
                    """);
            }
        }

        // Section: Exposes agents
        if (agent is IAgentWithDelegations agentWithDelegations)
        {
            var exposedAgents = agentWithDelegations.GetDelegationAgents()?.ToList() ?? [];
            if (exposedAgents.Any())
            {
                var exposedHtml = string.Join("", exposedAgents.Select(a =>
                {
                    var agentLink = $"<a href='{host.Hub.Address}/AgentDetails/{a.AgentName}' style='color: var(--color-done-fg); text-decoration: none; font-weight: 600;'>{a.AgentName}</a>";

                    return $"<li style='margin: 8px 0; padding: 12px; background: var(--color-done-subtle); border-radius: 4px; border-left: 3px solid var(--color-done-emphasis);'>" +
                           $"<div style='margin-bottom: 4px;'>{agentLink}</div>" +
                           $"<div style='color: var(--color-fg-muted); font-size: 13px;'>{a.Description}</div>" +
                           $"</li>";
                }));

                sections.Add($"""
                    <div style='margin-bottom: 16px;'>
                        <h4 style='margin: 0 0 8px 0; color: var(--color-fg-default); font-size: 14px; font-weight: 600;'>Exposes agents:</h4>
                        <ul style='margin: 0; padding: 0; list-style: none;'>{exposedHtml}</ul>
                    </div>
                    """);
            }
        }        // Section: Delegations from (agents that delegate TO this agent)
        var delegationsFromList = new List<(IAgentDefinition sourceAgent, string reason)>();

        // 1. Direct delegations from agents with IAgentWithDelegation
        var directDelegationsFrom = agents.OfType<IAgentWithDelegation>()
            .Where(a => a != agent && a.Delegations.Any(d => d.AgentName == agent.AgentName))
            .Select(a => ((IAgentDefinition)a, a.Delegations.First(d => d.AgentName == agent.AgentName).Instructions))
            .ToList();
        delegationsFromList.AddRange(directDelegationsFrom);

        // 2. Delegations from default agent if this agent has ExposedInNavigatorAttribute
        var defaultAgent = agents.FirstOrDefault(a => a.GetType().GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any());
        if (defaultAgent != null &&
            agent.GetType().GetCustomAttributes(typeof(ExposedInNavigatorAttribute), false).Any() &&
            defaultAgent != agent)
        {
            delegationsFromList.Add((defaultAgent, "Available for delegation via ExposedInNavigator attribute"));
        }

        if (delegationsFromList.Any())
        {
            var delegationsFromHtml = string.Join("", delegationsFromList.Select(item =>
            {
                var (sourceAgent, reason) = item;
                var agentLink = $"<a href='{host.Hub.Address}/AgentDetails/{sourceAgent.AgentName}' style='color: var(--color-success-fg); text-decoration: none; font-weight: 600;'>{sourceAgent.AgentName.Wordify()}</a>";

                return $"<li style='margin: 8px 0; padding: 12px; background: var(--color-success-subtle); border-radius: 4px; border-left: 3px solid var(--color-success-emphasis);'>" +
                       $"<div style='margin-bottom: 4px;'>{agentLink}</div>" +
                       $"<div style='color: var(--color-fg-muted); font-size: 13px;'>{reason}</div>" +
                       $"</li>";
            }));

            sections.Add($"""
                <div>
                    <h4 style='margin: 0 0 8px 0; color: var(--color-fg-default); font-size: 14px; font-weight: 600;'>Delegations from:</h4>
                    <ul style='margin: 0; padding: 0; list-style: none;'>{delegationsFromHtml}</ul>
                </div>
                """);
        }

        return sections.Any() ? string.Join("", sections) : "<div style='color: var(--color-fg-muted); font-style: italic; padding: 12px; background: var(--color-canvas-subtle); border-radius: 4px;'>No delegations configured</div>";
    }
}
