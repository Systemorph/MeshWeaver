#nullable enable

using MeshWeaver.AI.Parsing;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Commands;

/// <summary>
/// Context provided to a chat command during execution. Deliberately GENERIC — it carries no
/// per-command data (no agent/model/harness lists or setters). A command that needs to pick a
/// mesh node declares its query + target composer field and returns
/// <see cref="CommandResult.ShowPicker"/>; the host renders ONE generic node picker and writes
/// the chosen node's path back. So a module can register any <see cref="IChatCommand"/> (see
/// <see cref="MeshNodePickCommand"/> for the common "pick a node by query" case) WITHOUT touching
/// this type or the chat view.
/// </summary>
public record CommandContext
{
    /// <summary>The parsed command (name + arguments).</summary>
    public required ParsedCommand ParsedCommand { get; init; }

    /// <summary>
    /// The chat hub — a command can resolve the workspace / services from it for anything beyond
    /// the node-pick pattern. Optional so commands are unit-testable without a mesh.
    /// </summary>
    public IMessageHub? Hub { get; init; }

    /// <summary>The current navigation context path (used to scope node-pick queries). Optional.</summary>
    public string? ContextPath { get; init; }

    /// <summary>Current agent context (address, layout area).</summary>
    public AgentContext? AgentContext { get; init; }

    /// <summary>Registry of all commands (for the /help command).</summary>
    public ChatCommandRegistry? CommandRegistry { get; init; }
}

/// <summary>
/// A request from a command to the host to render a node picker: list the mesh nodes matching
/// <see cref="Query"/>, and on selection write the chosen node's PATH onto the composer field
/// <see cref="ComposerField"/> (a camelCase <c>ThreadComposer</c> property name, e.g. <c>harness</c>,
/// <c>agentName</c>, <c>modelName</c>). When <see cref="SearchTerm"/> is non-null the host pre-filters
/// to it and auto-selects an exact match (so <c>/model gpt-4o</c> switches without a click).
/// </summary>
public record NodePickerRequest(string Query, string ComposerField, string Title, string? SearchTerm = null);

/// <summary>
/// Result of executing a command.
/// </summary>
public record CommandResult
{
    /// <summary>Whether the command executed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Message to display to the user (error, confirmation, or picker fallback list).</summary>
    public string? Message { get; init; }

    /// <summary>Whether to proceed with sending the message to the AI.</summary>
    public bool ShouldSendToAI { get; init; } = false;

    /// <summary>
    /// Optional node-picker request. When set, the host renders the generic node picker for
    /// <see cref="NodePickerRequest.Query"/> and writes the selection to the composer field —
    /// the host wires the selection exactly as if the user had typed the command with that value.
    /// </summary>
    public NodePickerRequest? Picker { get; init; }

    /// <summary>Creates a successful result.</summary>
    public static CommandResult Ok(string? message = null) =>
        new() { Success = true, Message = message };

    /// <summary>Creates a failed result.</summary>
    public static CommandResult Error(string message) =>
        new() { Success = false, Message = message };

    /// <summary>Creates a successful result that asks the host to render the generic node picker.</summary>
    public static CommandResult ShowPicker(NodePickerRequest picker, string? message = null) =>
        new() { Success = true, Message = message, Picker = picker };
}
