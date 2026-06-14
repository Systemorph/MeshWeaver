#nullable enable

using System.Text.Json;
using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.AI.Commands;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Autocomplete for chat slash-commands. Lists the <c>nodeType:Command</c> catalog — the built-in
/// <c>/agent</c>, <c>/model</c>, <c>/harness</c> shipped as Command mesh nodes by
/// <see cref="BuiltInCommandProvider"/>, plus any other registered Command node — AND any C#
/// <see cref="IChatCommand"/> from the registry (e.g. <c>/help</c>). Deduped by slash word.
/// </summary>
public class CommandAutocompleteProvider : IAutocompleteProvider
{
    private const int CommandCategoryPriority = 2000;
    private readonly IServiceProvider _serviceProvider;

    /// <inheritdoc cref="CommandAutocompleteProvider"/>
    public CommandAutocompleteProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<AutocompleteItem>> GetItems(string query, string? contextPath = null)
    {
        // Pure in-memory enumeration — no external I/O.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<AutocompleteItem>();

        // Commands AS mesh nodes — the static Command catalog (built-ins + any other static
        // Command nodes). Their content is typed CommandDefinition, so the JsonSerializerOptions
        // is only the JsonElement-fallback and never actually used here.
        foreach (var cmd in CommandNodeType.ProjectCommands(
                     _serviceProvider.EnumerateStaticNodes(), EmptyJsonOptions))
            if (seen.Add(cmd.Id))
                items.Add(Item(cmd.Id, cmd.Description));

        // C# commands (e.g. /help) registered as IChatCommand.
        var registry = _serviceProvider.GetService<ChatCommandRegistry>();
        if (registry is not null)
            foreach (var cmd in registry.GetAllCommands())
                if (seen.Add(cmd.Name))
                    items.Add(Item(cmd.Name, cmd.Description));

        return Observable.Return((IReadOnlyCollection<AutocompleteItem>)items);
    }

    private static readonly JsonSerializerOptions EmptyJsonOptions = new();

    private static AutocompleteItem Item(string name, string? description) =>
        new(
            Label: $"/{name}",
            InsertText: $"/{name} ",
            Description: description ?? "",
            Category: "Commands",
            Priority: CommandCategoryPriority,
            Kind: AutocompleteKind.Command);
}
