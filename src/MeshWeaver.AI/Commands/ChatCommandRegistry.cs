#nullable enable

using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Commands;

/// <summary>
/// Registry for chat commands. Provides lookup and execution of commands.
/// </summary>
public class ChatCommandRegistry
{
    private readonly Dictionary<string, IChatCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    public ChatCommandRegistry(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers a command.
    /// </summary>
    public void Register(IChatCommand command)
    {
        _commands[command.Name] = command;

        // Register aliases
        foreach (var alias in command.Aliases)
        {
            _commands[alias] = command;
        }

        _logger?.LogDebug("Registered command: /{Name}", command.Name);
    }

    /// <summary>
    /// Tries to get a command by name.
    /// </summary>
    public bool TryGetCommand(string name, out IChatCommand? command)
    {
        return _commands.TryGetValue(name, out command);
    }

    /// <summary>
    /// Gets all registered commands (without duplicates from aliases).
    /// </summary>
    public IReadOnlyList<IChatCommand> GetAllCommands()
    {
        return _commands.Values.Distinct().ToList();
    }

    /// <summary>
    /// Checks if a command exists.
    /// </summary>
    public bool HasCommand(string name)
    {
        return _commands.ContainsKey(name);
    }
}
