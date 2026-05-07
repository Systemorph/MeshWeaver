#nullable enable

using System.Text.RegularExpressions;

namespace MeshWeaver.AI.Commands;

/// <summary>
/// Command to switch the current agent.
/// Usage: /agent @agent/AgentName or /agent AgentName
/// </summary>
public class AgentCommand : IChatCommand
{
    public string Name => "agent";
    public string Description => "Switch to a different agent for subsequent messages";
    public string Usage => "/agent @agent/Name or /agent Name";

    private static readonly Regex AgentRefPattern =
        new(@"@agent/(\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        if (context.ParsedCommand.Arguments.Length == 0)
        {
            // No args → ask the host to render a mesh-node picker filtered to
            // nodeType:Agent. The host wires its selection back to
            // SetCurrentAgent, exactly as if the user had typed /agent <Name>
            // with that name. Falls back to the textual list of agents in the
            // message body so non-Blazor hosts (and the test harness) still
            // get a useful response.
            var agentNames = string.Join(", ", context.AvailableAgents.Keys.OrderBy(n => n));
            return Task.FromResult(CommandResult.ShowWidget(
                ChatWidget.AgentPicker,
                $"Pick an agent — or type `{Usage}`. Available: {agentNames}"));
        }

        // Parse agent name from argument
        var arg = context.ParsedCommand.RawArguments;
        string agentName;

        var match = AgentRefPattern.Match(arg);
        if (match.Success)
        {
            agentName = match.Groups[1].Value;
        }
        else
        {
            // Allow just the agent name without @agent/ prefix
            agentName = context.ParsedCommand.Arguments[0].TrimStart('@');
        }

        // Find the agent (case-insensitive)
        var found = context.AvailableAgents
            .FirstOrDefault(kvp => kvp.Key.Equals(agentName, StringComparison.OrdinalIgnoreCase));

        if (found.Value == null)
        {
            var availableNames = string.Join(", ", context.AvailableAgents.Keys.OrderBy(n => n));
            return Task.FromResult(CommandResult.Error(
                $"Agent '{agentName}' not found.\n\nAvailable agents: {availableNames}"));
        }

        // Switch to the agent
        context.SetCurrentAgent(found.Value);

        return Task.FromResult(CommandResult.Ok(
            $"Switched to agent: **{found.Value.Name}**\n\n_{found.Value.Description}_"));
    }
}
