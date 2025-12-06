#nullable enable

using System.Text.RegularExpressions;

namespace MeshWeaver.AI.Parsing;

/// <summary>
/// Result of parsing a chat message for agent references, model references, and commands.
/// </summary>
public record ParsedChatMessage
{
    /// <summary>
    /// The original message text.
    /// </summary>
    public required string OriginalText { get; init; }

    /// <summary>
    /// The message text with command removed (if command was present).
    /// </summary>
    public required string ProcessedText { get; init; }

    /// <summary>
    /// Agent name extracted from @agent:Name reference anywhere in message.
    /// </summary>
    public string? AgentReference { get; init; }

    /// <summary>
    /// Model name extracted from model:Name reference anywhere in message.
    /// </summary>
    public string? ModelReference { get; init; }

    /// <summary>
    /// Parsed command, if message starts with /.
    /// </summary>
    public ParsedCommand? Command { get; init; }

    /// <summary>
    /// Whether the message should be sent to the AI (false for commands that fully handle the message).
    /// </summary>
    public bool ShouldSendToAI => Command?.ShouldSendToAI ?? true;
}

/// <summary>
/// Represents a parsed slash command.
/// </summary>
public record ParsedCommand
{
    /// <summary>
    /// The command name (without the / prefix).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Arguments passed to the command.
    /// </summary>
    public required string[] Arguments { get; init; }

    /// <summary>
    /// The raw argument string after the command name.
    /// </summary>
    public required string RawArguments { get; init; }

    /// <summary>
    /// Whether the message should still be sent to AI after command processing.
    /// </summary>
    public bool ShouldSendToAI { get; init; } = false;
}

/// <summary>
/// Parses chat messages to extract agent references, model references, and commands.
/// </summary>
public class ChatPreParser
{
    // Pattern: @agent:AgentName (anywhere in message)
    private static readonly Regex AgentReferencePattern =
        new(@"@agent:(\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Pattern: @model:ModelName (anywhere in message, model name can contain hyphens and dots)
    private static readonly Regex ModelReferencePattern =
        new(@"@model:([\w\-\.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Pattern: /command at start of message
    private static readonly Regex CommandPattern =
        new(@"^/(\w+)(?:\s+(.*))?$", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Parses a chat message for agent references, model references, and commands.
    /// </summary>
    public ParsedChatMessage Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ParsedChatMessage
            {
                OriginalText = text ?? string.Empty,
                ProcessedText = text ?? string.Empty
            };
        }

        var trimmedText = text.Trim();
        string? agentReference = null;
        string? modelReference = null;
        ParsedCommand? command = null;
        var processedText = text;

        // Check for @agent:Name reference anywhere in message
        var agentMatch = AgentReferencePattern.Match(trimmedText);
        if (agentMatch.Success)
        {
            agentReference = agentMatch.Groups[1].Value;
        }

        // Check for model:Name reference anywhere in message
        var modelMatch = ModelReferencePattern.Match(trimmedText);
        if (modelMatch.Success)
        {
            modelReference = modelMatch.Groups[1].Value;
        }

        // Check for /command at start of message
        var commandMatch = CommandPattern.Match(trimmedText);
        if (commandMatch.Success)
        {
            var commandName = commandMatch.Groups[1].Value.ToLowerInvariant();
            var rawArgs = commandMatch.Groups[2].Success ? commandMatch.Groups[2].Value.Trim() : string.Empty;
            var args = string.IsNullOrEmpty(rawArgs)
                ? Array.Empty<string>()
                : rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            command = new ParsedCommand
            {
                Name = commandName,
                Arguments = args,
                RawArguments = rawArgs
            };

            // Commands consume the entire message by default
            processedText = string.Empty;
        }

        return new ParsedChatMessage
        {
            OriginalText = text,
            ProcessedText = processedText,
            AgentReference = agentReference,
            ModelReference = modelReference,
            Command = command
        };
    }
}
