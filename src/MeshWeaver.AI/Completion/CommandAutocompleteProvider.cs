#nullable enable

using System.Text.Json;
using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.AI.Commands;
using MeshWeaver.Data;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Autocomplete for chat slash-commands, sourced from the <c>nodeType:Command</c> catalog with
/// namespace inheritance (<see cref="CommandNodeType.CommandQueries"/> — built-ins under the
/// <c>Command</c> namespace, plus any Command node defined in the context or the user's home and
/// their ancestors), PLUS any C# <see cref="IChatCommand"/> from the registry (e.g. <c>/help</c>).
/// Deduped by slash word.
/// </summary>
public class CommandAutocompleteProvider : IAutocompleteProvider
{
    private const int CommandCategoryPriority = 2000;
    private static readonly JsonSerializerOptions EmptyJsonOptions = new();

    private readonly IServiceProvider _serviceProvider;

    /// <inheritdoc cref="CommandAutocompleteProvider"/>
    public CommandAutocompleteProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<AutocompleteItem>> GetItems(string query, string? contextPath = null)
    {
        var registry = _serviceProvider.GetService<ChatCommandRegistry>();
        var registryCommands = registry?.GetAllCommands().ToList() ?? [];

        var workspace = _serviceProvider.GetService<IWorkspace>();
        var hub = _serviceProvider.GetService<IMessageHub>();
        if (workspace is null || hub is null)
            return Observable.Return(BuildItems([], registryCommands, hub));

        // nodeType:Command catalog with inheritance — cached by queryId so per-keystroke calls
        // reuse the same shared subscription. Built-in commands are served live under the
        // Command partition; Space/NodeType/user-defined ones come from the inherited scopes.
        var queries = CommandNodeType.CommandQueries(contextPath, null);
        return AgentPickerProjection.ObserveSnapshot(workspace, hub, $"command-autocomplete|{contextPath}", queries)
            .Select(snapshot => BuildItems(snapshot, registryCommands, hub));
    }

    private static IReadOnlyCollection<AutocompleteItem> BuildItems(
        IEnumerable<MeshNode> snapshot, IReadOnlyList<IChatCommand> registryCommands, IMessageHub? hub)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<AutocompleteItem>();

        foreach (var cmd in CommandNodeType.ProjectCommands(snapshot, hub?.JsonSerializerOptions ?? EmptyJsonOptions))
            if (seen.Add(cmd.Id))
                items.Add(Item(cmd.Id, cmd.Description));

        foreach (var cmd in registryCommands)
            if (seen.Add(cmd.Name))
                items.Add(Item(cmd.Name, cmd.Description));

        return items;
    }

    private static AutocompleteItem Item(string name, string? description) =>
        new(
            Label: $"/{name}",
            InsertText: $"/{name} ",
            Description: description ?? "",
            Category: "Commands",
            Priority: CommandCategoryPriority,
            Kind: AutocompleteKind.Command);
}
