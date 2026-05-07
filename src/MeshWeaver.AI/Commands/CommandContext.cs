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
/// Discriminator for inline widgets a command can request the host to render
/// in addition to (or in lieu of) <see cref="CommandResult.Message"/>. The
/// host (<c>ThreadChatView</c>) is responsible for actually rendering the
/// widget — keeping this enum-typed avoids a Blazor / Layout dependency on
/// MeshWeaver.AI.
/// </summary>
public enum ChatWidget
{
    /// <summary>No inline widget — only the text message (if any) is shown.</summary>
    None = 0,

    /// <summary>
    /// Show a mesh-node picker filtered to <c>nodeType:Agent</c>; the host
    /// wires selection back to the equivalent of running
    /// <c>/agent &lt;name&gt;</c>.
    /// </summary>
    AgentPicker = 1,

    /// <summary>
    /// Show a model picker for the currently-available models. Models
    /// currently come from the <c>IChatClientFactory</c> registrations
    /// rather than mesh nodes — once they migrate, this becomes a
    /// node picker too.
    /// </summary>
    ModelPicker = 2,
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
    /// Optional inline widget the host should render (picker, list, etc.).
    /// Non-<see cref="ChatWidget.None"/> values shift the command's return
    /// shape from "show text" to "show interactive UI" — the host wires
    /// the resulting selection back to the corresponding setter
    /// (<see cref="CommandContext.SetCurrentAgent"/> /
    /// <see cref="CommandContext.SetCurrentModel"/>) just as if the user
    /// had typed <c>/agent &lt;name&gt;</c> with that value.
    /// </summary>
    public ChatWidget Widget { get; init; } = ChatWidget.None;

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

    /// <summary>
    /// Creates a successful result that asks the host to render an inline widget.
    /// </summary>
    public static CommandResult ShowWidget(ChatWidget widget, string? message = null) =>
        new() { Success = true, Message = message, Widget = widget };
}
