using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

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

        return Controls.Stack
            .WithView(Controls.NavLink("← Back to Agent Overview", $"{host.Hub.Address}/Overview"), "BackLink")
            .WithView(Controls.Html("<div style='margin: 20px 0;'></div>"), "Spacer1")
            .WithView(Controls.Title($"{agent.Name.Wordify()}", 1), "Title")
            .WithView(Controls.Html($"<div style='margin: 16px 0; padding: 0 4px;'>{agent.Description}</div>"), "Description")
            .WithView(CreateInstructionsSection(agent), "Instructions")
            .WithView(CreateAttributesSection(agent), "Attributes")
            .WithView(CreatePluginsSection(agent), "Plugins")
            .WithView(CreateDelegationsSection(agent, agents, host), "Delegations");
    }
    private static UiControl CreateInstructionsSection(IAgentDefinition agent)
    {
        var instructions = agent.Instructions;

        return Controls.Html($"""
            <div style='margin: 24px 0; padding: 0 4px;'>
                <h2 style='margin-bottom: 16px;'>Instructions</h2>
                <pre style='background: var(--code-block-background-color, #f6f8fa); border: 1px solid var(--code-block-border-color, #e1e4e8); border-radius: 6px; padding: 16px; margin: 0; overflow-x: auto; font-family: "SFMono-Regular", Consolas, "Liberation Mono", Menlo, monospace; font-size: 13px; line-height: 1.45; white-space: pre-wrap;'>{instructions}</pre>
            </div>
            """);
    }
    private static UiControl CreateAttributesSection(IAgentDefinition agent)
    {
        var attributes = GetAgentAttributesForDisplay(agent);

        return Controls.Html($"""
            <div style='margin: 24px 0; padding: 0 4px;'>
                <h2 style='margin-bottom: 16px;'>Attributes</h2>
                <div style='line-height: 1.6;'>{attributes}</div>
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
                kernelPlugins = agentWithPlugins.GetPlugins(null!)?.ToList() ?? [];
            }
            catch (Exception ex)
            {
                // In case plugin creation fails, log but continue
                System.Diagnostics.Debug.WriteLine($"Failed to get plugins for {agent.Name}: {ex.Message}");
            }
        }
        var pluginsList = kernelPlugins.Any()
            ? string.Join("", kernelPlugins.Select(plugin =>
            {
                var functionsHtml = string.Join("", plugin.Select(function =>
                {
                    var parametersHtml = function.Metadata.Parameters.Any()
                        ? string.Join("", function.Metadata.Parameters.Select(param =>
                            $"<li style='margin: 4px 0;'><strong>{param.Name}</strong> ({param.ParameterType?.Name ?? "string"})" +
                            $"{(param.IsRequired ? " <em style='color: #d73a49;'>*required*</em>" : "")}" +
                            $"{(!string.IsNullOrEmpty(param.Description) ? $": {param.Description}" : "")}</li>"))
                        : "<li style='color: #6a737d; font-style: italic;'>No parameters</li>";

                    return $"<div style='margin: 16px 0; padding: 12px; background: var(--code-block-background-color, #f6f8fa); border: 1px solid var(--code-block-border-color, #e1e4e8); border-radius: 6px;'>" +
                           $"<h4 style='margin: 0 0 8px 0; color: #0366d6;'>{function.Name}</h4>" +
                           $"{(!string.IsNullOrEmpty(function.Description) ? $"<p style='margin: 0 0 12px 0; color: #586069;'>{function.Description}</p>" : "")}" +
                           $"<div><strong style='font-size: 13px; color: #24292e;'>Parameters:</strong></div>" +
                           $"<ul style='margin: 8px 0 0 20px; padding: 0;'>{parametersHtml}</ul>" +
                           $"</div>";
                }));

                return $"<div style='margin: 20px 0;'>" +
                       $"<h3 style='margin: 0 0 12px 0; color: #24292e;'>{plugin.Name}</h3>" +
                       $"{functionsHtml}" +
                       $"</div>";
            }))
            : "<div style='color: #6a737d; font-style: italic; padding: 12px; background: var(--code-block-background-color, #f6f8fa); border: 1px solid var(--code-block-border-color, #e1e4e8); border-radius: 6px;'>No plugins configured</div>";

        return Controls.Html($"""
            <div style='margin: 24px 0; padding: 0 4px;'>
                <h2 style='margin-bottom: 16px;'>Plugins</h2>
                {pluginsList}
            </div>
            """);
    }
    private static UiControl CreateDelegationsSection(IAgentDefinition agent, IReadOnlyDictionary<string, IAgentDefinition> agents, LayoutAreaHost host)
    {
        var delegationInfo = GetDelegationInfoForDisplay(agent, agents, host);

        return Controls.Html($"""
            <div style='margin: 24px 0; padding: 0 4px;'>
                <h2 style='margin-bottom: 16px;'>Delegations</h2>
                {delegationInfo}
            </div>
            """);
    }
    internal static string GetAgentAttributesForDisplay(IAgentDefinition agent)
    {
        var attributes = new List<string>();
        var type = agent.GetType();

        if (type.GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any())
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: #d4edda; color: #155724; border-radius: 16px; font-size: 13px; font-weight: 600;'>🟢 Default Agent</span>");

        if (type.GetCustomAttributes(typeof(ExposedInNavigatorAttribute), false).Any())
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: #cce5ff; color: #0366d6; border-radius: 16px; font-size: 13px; font-weight: 600;'>🔵 Exposed in Navigator</span>");

        if (agent is IAgentWithDelegations)
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: #fff3cd; color: #856404; border-radius: 16px; font-size: 13px; font-weight: 600;'>🟡 With Delegation</span>");

        if (agent is IAgentWithDelegations)
            attributes.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: #ffe6cc; color: #b45309; border-radius: 16px; font-size: 13px; font-weight: 600;'>🟠 With Delegations</span>");

        return attributes.Any() ? string.Join("", attributes) : "<span style='color: #6a737d; font-style: italic;'>No special attributes</span>";
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
                        ? $"<a href='{host.Hub.Address}/AgentDetails/{d.AgentName}' style='color: #0366d6; text-decoration: none; font-weight: 600;'>{d.AgentName}</a>"
                        : $"<strong style='color: #0366d6;'>{d.AgentName}</strong>";

                    return $"<li style='margin: 8px 0; padding: 12px; background: #f0f8ff; border-left: 4px solid #0366d6; border-radius: 4px;'>" +
                           $"<div style='margin-bottom: 4px;'>{agentLink}</div>" +
                           $"<div style='color: #586069; font-size: 13px;'>{d.Instructions}</div>" +
                           $"</li>";
                }));

                sections.Add($"""
                    <div style='margin-bottom: 20px;'>
                        <h3 style='margin: 0 0 12px 0; color: #24292e; font-size: 16px;'>Can delegate to:</h3>
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

                    return $"<li style='margin: 8px 0; padding: 12px; background: #f0fff4; border-left: 4px solid #28a745; border-radius: 4px;'>" +
                           $"<div style='margin-bottom: 4px;'>{agentLink}</div>" +
                           $"<div style='color: #586069; font-size: 13px;'>{a.Instructions}</div>" +
                           $"</li>";
                }));

                sections.Add($"""
                    <div style='margin-bottom: 20px;'>
                        <h3 style='margin: 0 0 12px 0; color: #24292e; font-size: 16px;'>Exposes agents:</h3>
                        <ul style='margin: 0; padding: 0; list-style: none;'>{exposedHtml}</ul>
                    </div>
                    """);
            }
        }

        // Section: Delegations from (agents that delegate TO this agent)
        var delegationsFromList = new List<(IAgentDefinition sourceAgent, string reason)>();

        // 1. Direct delegations from agents with IAgentWithDelegations
        var directDelegationsFrom = agents.Values.OfType<IAgentWithDelegations>()
            .Where(a => a != agent && a.Delegations.Any(d => d.AgentName == agent.Name))
            .Select(a => ((IAgentDefinition)a, a.Delegations.First(d => d.AgentName == agent.Name).Instructions))
            .ToList();
        delegationsFromList.AddRange(directDelegationsFrom);

        // 2. Delegations from default agent if this agent has ExposedInNavigatorAttribute
        var defaultAgent = agents.Values.FirstOrDefault(a => a.GetType().GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any());
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
                var agentLink = $"<a href='{host.Hub.Address}/AgentDetails/{sourceAgent.Name}' style='color: #6f42c1; text-decoration: none; font-weight: 600;'>{sourceAgent.Name.Wordify()}</a>";

                return $"<li style='margin: 8px 0; padding: 12px; background: #f8f4fd; border-left: 4px solid #6f42c1; border-radius: 4px;'>" +
                       $"<div style='margin-bottom: 4px;'>{agentLink}</div>" +
                       $"<div style='color: #586069; font-size: 13px;'>{reason}</div>" +
                       $"</li>";
            }));

            sections.Add($"""
                <div>
                    <h3 style='margin: 0 0 12px 0; color: #24292e; font-size: 16px;'>Delegations from:</h3>
                    <ul style='margin: 0; padding: 0; list-style: none;'>{delegationsFromHtml}</ul>
                </div>
                """);
        }

        return sections.Any() ? string.Join("", sections) : "<div style='color: #6a737d; font-style: italic; padding: 12px; background: var(--code-block-background-color, #f6f8fa); border: 1px solid var(--code-block-border-color, #e1e4e8); border-radius: 6px;'>No delegations configured</div>";
    }
}
