#nullable enable

using System.Text.Json;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Autocomplete for chat slash-skills, sourced from the <c>nodeType:Skill</c> catalog with namespace
/// inheritance (<see cref="SkillNodeType.SkillQueries"/> — built-ins under the <c>Skill</c> namespace,
/// plus any Skill node defined in the context or the user's home and their ancestors). Replaces the
/// retired <c>CommandAutocompleteProvider</c>; skills are declarative nodes, so there is no C# registry.
/// </summary>
public class SkillAutocompleteProvider : IAutocompleteProvider
{
    private const int SkillCategoryPriority = 2000;
    private static readonly JsonSerializerOptions EmptyJsonOptions = new();

    private readonly IServiceProvider _serviceProvider;

    /// <inheritdoc cref="SkillAutocompleteProvider"/>
    public SkillAutocompleteProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<AutocompleteItem>> GetItems(string query, string? contextPath = null)
    {
        var workspace = _serviceProvider.GetService<IWorkspace>();
        var hub = _serviceProvider.GetService<IMessageHub>();
        if (workspace is null || hub is null)
            return Observable.Return((IReadOnlyCollection<AutocompleteItem>)[]);

        // nodeType:Skill catalog with inheritance — cached by queryId so per-keystroke calls reuse the
        // same shared subscription. Built-in skills are served live under the Skill partition; any
        // Space/NodeType/user-defined skill comes from the inherited scopes.
        var queries = SkillNodeType.SkillQueries(contextPath, null);
        return AgentPickerProjection.ObserveSnapshot(workspace, hub, $"skill-autocomplete|{contextPath}", queries)
            .Select(snapshot => BuildItems(snapshot, hub));
    }

    private static IReadOnlyCollection<AutocompleteItem> BuildItems(IEnumerable<MeshNode> snapshot, IMessageHub? hub)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<AutocompleteItem>();
        foreach (var skill in SkillNodeType.ProjectSkills(snapshot, hub?.JsonSerializerOptions ?? EmptyJsonOptions))
            if (seen.Add(skill.Id))
                items.Add(Item(skill.Id, skill.Description));
        return items;
    }

    private static AutocompleteItem Item(string name, string? description) =>
        new(
            Label: $"/{name}",
            InsertText: $"/{name} ",
            Description: description ?? "",
            Category: "Commands",
            Priority: SkillCategoryPriority,
            Kind: AutocompleteKind.Command);
}
