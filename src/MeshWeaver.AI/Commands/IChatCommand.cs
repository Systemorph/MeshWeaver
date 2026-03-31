#nullable enable

namespace MeshWeaver.AI.Commands;

/// <summary>
/// Represents a chat command that can be executed by the user.
/// </summary>
public interface IChatCommand
{
    /// <summary>
    /// The command name (without the / prefix). Must be lowercase.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Short description of the command for help text.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Usage syntax for the command (e.g., "/agent @agent:Name").
    /// </summary>
    string Usage { get; }

    /// <summary>
    /// Optional aliases for the command.
    /// </summary>
    IReadOnlyList<string> Aliases => Array.Empty<string>();

    /// <summary>
    /// Executes the command.
    /// </summary>
    /// <param name="context">The command execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of command execution.</returns>
    Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default);
}
