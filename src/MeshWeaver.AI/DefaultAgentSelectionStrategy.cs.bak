using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;

namespace MeshWeaver.AI;

/// <summary>
/// Simple selection strategy that checks for explicit agent mentions or delegation patterns, otherwise uses default agent
/// </summary>
public class DefaultAgentSelectionStrategy(Agent defaultAgent, ILogger logger) : SelectionStrategy
{
    private readonly Agent _defaultAgent = defaultAgent;
    private readonly ILogger _logger = logger; private static readonly Regex AgentMentionRegex = new(@"^@(\w+)", RegexOptions.Compiled);
    private static readonly Regex DelegationRegex = new(@"DELEGATE_TO:\s*@(\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SectionDelegationRegex = new(@"§DELEGATE_TO:\s*([^§]+)§", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JsonDelegationRegex = new(@"\{""delegate_to"":\s*""([^""]+)""\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    protected override Task<Agent> SelectAgentAsync(IReadOnlyList<Agent> agents, IReadOnlyList<ChatMessageContent> history, CancellationToken cancellationToken = default)
    {
        if (!history.Any())
            return Task.FromResult(_defaultAgent);

        var lastMessage = history.Last();
        string content = lastMessage.Content ?? string.Empty;

        // Check for explicit user agent mention first (@Name at start of message)
        var mentionMatch = AgentMentionRegex.Match(content);
        if (mentionMatch.Success)
        {
            string mentionedAgentName = mentionMatch.Groups[1].Value;
            var matchingAgent = agents.FirstOrDefault(a =>
                a.Name!.Equals(mentionedAgentName, StringComparison.OrdinalIgnoreCase));

            if (matchingAgent != null)
            {
                _logger.LogInformation("Routing to explicitly mentioned agent: {Name}", mentionedAgentName);
                return Task.FromResult(matchingAgent);
            }
        }

        // Check for JSON delegation pattern first (preferred method)
        var jsonDelegationMatch = JsonDelegationRegex.Match(content);
        if (jsonDelegationMatch.Success)
        {
            string delegatedAgentName = jsonDelegationMatch.Groups[1].Value;
            var delegatedAgent = agents.FirstOrDefault(a =>
                a.Name!.Equals(delegatedAgentName, StringComparison.OrdinalIgnoreCase));

            if (delegatedAgent != null)
            {
                _logger.LogInformation("Routing to JSON delegated agent: {Name}", delegatedAgentName);
                return Task.FromResult(delegatedAgent);
            }
            else
            {
                // Log the failed JSON delegation attempt
                var availableAgents = string.Join(", ", agents.Select(a => a.Name));
                _logger.LogWarning("Failed JSON delegation to '{AttemptedAgent}'. Available agents: {AvailableAgents}",
                    delegatedAgentName, availableAgents);

                // Try to find a partial match
                var partialMatches = agents.Where(a =>
                    a.Name!.Contains(delegatedAgentName, StringComparison.OrdinalIgnoreCase) ||
                    delegatedAgentName.Contains(a.Name!, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (partialMatches.Any())
                {
                    _logger.LogInformation("Possible intended agents for JSON delegation: {PossibleMatches}",
                        string.Join(", ", partialMatches.Select(a => a.Name)));
                }
            }
        }        // Fallback: Check for agent delegation pattern (DELEGATE_TO: @Name or §DELEGATE_TO:Name§ anywhere in the message)
        var delegationMatch = DelegationRegex.Match(content);
        var sectionDelegationMatch = SectionDelegationRegex.Match(content);

        if (delegationMatch.Success)
        {
            string delegatedAgentName = delegationMatch.Groups[1].Value;
            var delegatedAgent = agents.FirstOrDefault(a =>
                a.Name!.Equals(delegatedAgentName, StringComparison.OrdinalIgnoreCase));

            if (delegatedAgent != null)
            {
                _logger.LogInformation("Agent delegation detected for: {Name}", delegatedAgentName);
                return Task.FromResult(delegatedAgent);
            }
        }

        if (sectionDelegationMatch.Success)
        {
            string delegatedAgentName = sectionDelegationMatch.Groups[1].Value.Trim();
            var delegatedAgent = agents.FirstOrDefault(a =>
                a.Name!.Equals(delegatedAgentName, StringComparison.OrdinalIgnoreCase));

            if (delegatedAgent != null)
            {
                _logger.LogInformation("Routing to delegated agent: {Name}", delegatedAgentName);
                return Task.FromResult(delegatedAgent);
            }
            else
            {
                // Log the failed delegation attempt with suggestions
                var availableAgents = string.Join(", ", agents.Select(a => a.Name));
                _logger.LogWarning("Failed delegation to '{AttemptedAgent}'. Available agents: {AvailableAgents}",
                    delegatedAgentName, availableAgents);

                // Try to find a partial match to help with debugging
                var partialMatches = agents.Where(a =>
                    a.Name!.Contains(delegatedAgentName, StringComparison.OrdinalIgnoreCase) ||
                    delegatedAgentName.Contains(a.Name!, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (partialMatches.Any())
                {
                    _logger.LogInformation("Possible intended agents: {PossibleMatches}",
                        string.Join(", ", partialMatches.Select(a => a.Name)));
                }
            }
        }

        // Default to the default agent (MeshNavigator)
        _logger.LogInformation("Routing to default agent: {Name}", _defaultAgent.Name);
        return Task.FromResult(_defaultAgent);
    }
}
