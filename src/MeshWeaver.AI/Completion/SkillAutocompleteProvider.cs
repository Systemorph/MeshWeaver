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
        // Space/NodeType/user-defined skill comes from the inherited scopes — INCLUDING the chatting
        // user's own {user}/Skill (derived from the hub identity), so a user's personal skills appear
        // alongside the space's and the platform's. Cache key includes the user so the per-user
        // subscription isn't shared across identities.
        var accessService = _serviceProvider.GetService<AccessService>();
        var userHome = AgentPickerProjection.ResolveUserHome(accessService);
        var queries = BuildQueries(accessService, contextPath);
        return AgentPickerProjection.ObserveSnapshot(
                workspace, hub, $"skill-autocomplete|{contextPath}|{userHome}", queries)
            .Select(snapshot => BuildItems(snapshot, hub));
    }

    /// <summary>
    /// The skill-autocomplete query union: the platform <c>Skill</c> catalog plus the current space's
    /// <c>{space}/Skill</c> and the chatting user's <c>{user}/Skill</c> (derived from
    /// <paramref name="accessService"/> via <see cref="AgentPickerProjection.ResolveUserHome"/>), as one
    /// <c>namespace:A|B|C nodeType:Skill</c> exact-membership query with reserved partitions filtered —
    /// IDENTICAL inheritance to the agent / model registry. Extracted as a pure method so the union is
    /// unit-testable without a mesh (see <c>AgentPickerQueriesTest</c>). Was the bug: the provider passed
    /// a null userPath, so a user's OWN skills never appeared in autocomplete.
    /// </summary>
    internal static string[] BuildQueries(AccessService? accessService, string? contextPath)
        => SkillNodeType.SkillQueries(contextPath, AgentPickerProjection.ResolveUserHome(accessService));

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
