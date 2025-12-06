#nullable enable

using System.Text.RegularExpressions;

namespace MeshWeaver.AI.Commands;

/// <summary>
/// Command to switch the current agent.
/// Usage: /agent @agent:AgentName or /agent AgentName
/// </summary>
public class AgentCommand : IChatCommand
{
    public string Name => "agent";
    public string Description => "Switch to a different agent for subsequent messages";
    public string Usage => "/agent @agent:Name or /agent Name";

    private static readonly Regex AgentRefPattern =
        new(@"@agent:(\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        if (context.ParsedCommand.Arguments.Length == 0)
        {
            // List available agents
            var agentNames = string.Join(", ", context.AvailableAgents.Keys.OrderBy(n => n));
            return Task.FromResult(CommandResult.Error(
                $"Usage: {Usage}\n\nAvailable agents: {agentNames}"));
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
            // Allow just the agent name without @agent: prefix
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
