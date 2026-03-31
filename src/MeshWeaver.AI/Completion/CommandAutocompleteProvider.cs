#nullable enable

using System.Runtime.CompilerServices;
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
    public async IAsyncEnumerable<AutocompleteItem> GetItemsAsync(
        string query,
        string? contextPath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_commandRegistry == null)
            yield break;

        await Task.CompletedTask; // Satisfy async requirement

        foreach (var cmd in _commandRegistry.GetAllCommands())
        {
            yield return new AutocompleteItem(
                Label: $"/{cmd.Name}",
                InsertText: $"/{cmd.Name} ",
                Description: cmd.Description,
                Category: "Commands",
                Priority: CommandCategoryPriority,
                Kind: AutocompleteKind.Command
            );
        }
    }
}
