using MeshWeaver.AI;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Application.Layout;

public static class AgentDetailsArea
{
    public static LayoutDefinition AddAgentDetails(this LayoutDefinition layout)
    {
        return layout.WithView(nameof(AgentDetails), AgentDetails);
    }

    public static UiControl AgentDetails(LayoutAreaHost host, RenderingContext ctx)
    {
        // Extract agent name from route parameters
        var agentName = ExtractAgentNameFromContext(ctx);
        var agents = host.Hub.ServiceProvider.GetService<IEnumerable<IAgentDefinition>>()?.ToList() ?? [];
        var agent = agents.FirstOrDefault(a => a.AgentName == agentName);

        if (agent == null)
        {
            return Controls.Stack
                .WithView(Controls.Title("Agent Details", 2), "Title")
                .WithView(Controls.Text($"Agent '{agentName}' not found."), "ErrorMessage")
                .WithView(Controls.NavLink("← Back to Agent Overview", $"{host.Hub.Address}/Overview"), "BackLink");
        }

        return CreateAgentDetailsView(agent, host);
    }

    private static string ExtractAgentNameFromContext(RenderingContext ctx)
    {
        // Extract agent name from the route context
        // Expecting route like "/AgentDetails/{AgentName}"
        var parts = ctx.Area.Split('/');
        return parts.Length >= 2 ? parts[^1] : "";
    }

    private static UiControl CreateAgentDetailsView(IAgentDefinition agent, LayoutAreaHost host)
    {
        return Controls.Stack
            .WithView(Controls.Title($"Agent Details: {agent.AgentName}", 2), "Title")
            .WithView(
                Controls.Stack
                    .WithView(Controls.Text($"**Name:** {agent.AgentName}"), "Name")
                    .WithView(Controls.Text($"**Description:** {agent.Description}"), "Description")
                    .WithView(Controls.Text($"**Type:** {agent.GetType().Name}"), "Type")
                    .WithView(CreateInstructionsSection(agent), "Instructions")
                    .WithView(CreateAttributesSection(agent), "Attributes")
                    .WithView(CreatePluginsSection(agent), "Plugins"),
                "Details"
            )
            .WithView(Controls.NavLink("← Back to Agent Overview", $"{host.Hub.Address}/Overview"), "BackLink");
    }

    private static UiControl CreateInstructionsSection(IAgentDefinition agent)
    {
        var instructions = agent.Instructions ?? "No instructions provided.";
        return Controls.Stack
            .WithView(Controls.Title("Instructions", 3), "Title")
            .WithView(Controls.Html($"<pre><code>{instructions}</code></pre>"), "Content");
    }

    private static UiControl CreateAttributesSection(IAgentDefinition agent)
    {
        var attributesText = GetAgentAttributes(agent);
        return Controls.Stack
            .WithView(Controls.Title("Attributes", 3), "Title")
            .WithView(Controls.Markdown(attributesText), "Content");
    }

    private static UiControl CreatePluginsSection(IAgentDefinition agent)
    {
        var pluginsText = GetAgentPlugins(agent);
        return Controls.Stack
            .WithView(Controls.Title("Plugins", 3), "Title")
            .WithView(Controls.Markdown(pluginsText), "Content");
    }

    internal static string GetAgentAttributes(IAgentDefinition agent)
    {
        var attributes = new List<string>();
        var type = agent.GetType();

        if (type.GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any())
            attributes.Add("- **Default Agent** - Entry point agent");

        if (type.GetCustomAttributes(typeof(ExposedInNavigatorAttribute), false).Any())
            attributes.Add("- **Exposed in Navigator** - Available for delegation");

        if (agent is IAgentWithDelegation)
            attributes.Add("- **With Delegation** - Can delegate to other agents");

        if (agent is IAgentWithDelegations)
            attributes.Add("- **With Delegations** - Exposes other agents for delegation");

        return attributes.Any() ? string.Join("\n", attributes) : "- No special attributes";
    }

    internal static string GetAgentPlugins(IAgentDefinition agent)
    {
        var plugins = agent.GetPlugins()?.ToList() ?? [];

        if (!plugins.Any())
            return "- No plugins configured";

        return string.Join("\n", plugins.Select(p => $"- {p.GetType().Name}"));
    }
}
