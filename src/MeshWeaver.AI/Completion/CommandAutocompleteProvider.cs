#nullable enable

using System.Reactive.Linq;
using MeshWeaver.AI.Commands;
using MeshWeaver.Data.Completion;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Provides autocomplete items for chat commands.
/// This provider requires a ChatCommandRegistry to be set before use.
/// </summary>
public class CommandAutocompleteProvider : IAutocompleteProvider
{
    private const int CommandCategoryPriority = 2000;

    private ChatCommandRegistry? _commandRegistry;

    /// <summary>
    /// Sets the command registry for autocomplete.
    /// </summary>
    public void SetCommandRegistry(ChatCommandRegistry registry)
    {
        _commandRegistry = registry;
    }

    /// <inheritdoc />
    public IObservable<AutocompleteItem> GetItems(string query, string? contextPath = null)
    {
        // No external I/O — pure in-memory enumeration. ToObservable on the
        // synchronous IEnumerable is the correct shape; no Channel / no async.
        if (_commandRegistry == null)
            return Observable.Empty<AutocompleteItem>();

        return _commandRegistry.GetAllCommands()
            .Select(cmd => new AutocompleteItem(
                Label: $"/{cmd.Name}",
                InsertText: $"/{cmd.Name} ",
                Description: cmd.Description,
                Category: "Commands",
                Priority: CommandCategoryPriority,
                Kind: AutocompleteKind.Command))
            .ToObservable();
    }
}
