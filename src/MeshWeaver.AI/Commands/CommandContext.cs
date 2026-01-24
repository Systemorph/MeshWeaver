#nullable enable

using MeshWeaver.AI.Parsing;

namespace MeshWeaver.AI.Commands;

/// <summary>
/// Context provided to commands during execution.
/// </summary>
public record CommandContext
{
    /// <summary>
    /// The parsed command information.
    /// </summary>
    public required ParsedCommand ParsedCommand { get; init; }

    /// <summary>
    /// Available agents by name.
    /// </summary>
    public required IReadOnlyDictionary<string, AgentDisplayInfo> AvailableAgents { get; init; }

    /// <summary>
    /// Currently selected agent.
    /// </summary>
    public AgentDisplayInfo? CurrentAgent { get; init; }

    /// <summary>
    /// Callback to set the current agent (for /agent command).
    /// </summary>
    public required Action<AgentDisplayInfo> SetCurrentAgent { get; init; }

    /// <summary>
    /// Available models with provider information.
    /// </summary>
    public IReadOnlyList<ModelInfo>? AvailableModels { get; init; }

    /// <summary>
    /// Currently selected model.
    /// </summary>
    public ModelInfo? CurrentModel { get; init; }

    /// <summary>
    /// Callback to set the current model (for /model command).
    /// </summary>
    public Action<ModelInfo>? SetCurrentModel { get; init; }

    /// <summary>
    /// Current agent context (address, layout area).
    /// </summary>
    public AgentContext? AgentContext { get; init; }

    /// <summary>
    /// Registry of all commands (for /help command).
    /// </summary>
    public ChatCommandRegistry? CommandRegistry { get; init; }
}

/// <summary>
/// Result of executing a command.
/// </summary>
public record CommandResult
{
    /// <summary>
    /// Whether the command executed successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Message to display to the user (e.g., error message or confirmation).
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Whether to proceed with sending the message to the AI.
    /// </summary>
    public bool ShouldSendToAI { get; init; } = false;

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static CommandResult Ok(string? message = null) =>
        new() { Success = true, Message = message };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static CommandResult Error(string message) =>
        new() { Success = false, Message = message };
}
