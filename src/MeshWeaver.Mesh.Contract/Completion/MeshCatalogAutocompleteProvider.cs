using System.Reactive.Linq;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh.Completion;

/// <summary>
/// Provides autocomplete items for registered mesh nodes.
/// Returns items like "@app/", "@pricing/" based on registered MeshNodes
/// plus reserved prefixes (agent, model).
/// </summary>
public class MeshCatalogAutocompleteProvider : IAutocompleteProvider
{
    private readonly IServiceProvider serviceProvider;
    private const int PrefixCategoryPriority = 1800;

    /// <inheritdoc cref="MeshCatalogAutocompleteProvider"/>
    public MeshCatalogAutocompleteProvider(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<AutocompleteItem>> GetItems(string query, string? contextPath = null)
    {
        // Pure in-memory enumeration of registered mesh configuration nodes + reserved prefixes.
        var yielded = new HashSet<string>();
        var items = new List<AutocompleteItem>();

        var topLevelNodes = serviceProvider.EnumerateStaticNodes()
            .Where(n => n.Segments.Count == 1)
            .OrderBy(n => n.Order ?? int.MaxValue)
            .ThenBy(n => n.Name);

        {
            foreach (var node in topLevelNodes)
            {
                yielded.Add($"@{node.Path}/");
                items.Add(new AutocompleteItem(
                    Label: $"@{node.Path}/",
                    InsertText: $"@{node.Path}/",
                    Description: node.Name,
                    Category: "Prefixes",
                    Priority: PrefixCategoryPriority - (node.Order ?? 0),
                    Kind: AutocompleteKind.Other));
            }
        }

        if (!yielded.Contains("@agent/"))
        {
            items.Add(new AutocompleteItem(
                Label: "@agent/",
                InsertText: "@agent/",
                Description: "Select an AI agent",
                Category: "Prefixes",
                Priority: PrefixCategoryPriority,
                Kind: AutocompleteKind.Agent));
        }

        if (!yielded.Contains("@model/"))
        {
            items.Add(new AutocompleteItem(
                Label: "@model/",
                InsertText: "@model/",
                Description: "Select an AI model",
                Category: "Prefixes",
                Priority: PrefixCategoryPriority,
                Kind: AutocompleteKind.Other));
        }

        return Observable.Return((IReadOnlyCollection<AutocompleteItem>)items);
    }
}
