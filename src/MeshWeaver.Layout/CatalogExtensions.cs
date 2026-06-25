namespace MeshWeaver.Layout;

/// <summary>
/// Fluent builder for a <b>catalog</b> — a tabbed set of mesh searches, where each tab is a labelled
/// <see cref="MeshSearchControl"/> over a scoped query. It composes the existing skinned
/// <see cref="TabsControl"/> (the tab "title menu") with <see cref="MeshSearchControl"/> (which already
/// carries its own create / empty-state via <c>CreateNodeType</c>), so the user/home page is declared
/// rather than hand-rolled:
/// <code>
/// Controls.Tabs
///     .WithMeshSearch("Threads",     @namespace: "*/_Thread", scope: "descendants", nodeType: "Thread", createNodeType: "Thread")
///     .WithMeshSearch("Last Viewed", query: "sort:LastViewed-desc")
///     .WithMeshSearch("Last Edited", query: "sort:LastModified-desc")
/// </code>
/// Both the Blazor and native MAUI view packs render the resulting <see cref="TabsControl"/>.
/// </summary>
public static class CatalogExtensions
{
    /// <summary>
    /// Adds a tab labelled <paramref name="label"/> whose content is a <see cref="MeshSearchControl"/>
    /// scoped by the supplied filters. <paramref name="namespace"/>/<paramref name="scope"/>/
    /// <paramref name="nodeType"/> are composed into the search's hidden query (with any extra
    /// <paramref name="query"/> appended); set <paramref name="createNodeType"/> to offer a "New" action
    /// and an inviting empty state for that node type.
    /// </summary>
    public static TabsControl WithMeshSearch(
        this TabsControl tabs,
        string label,
        string? @namespace = null,
        string? scope = null,
        string? nodeType = null,
        string? query = null,
        string? createNodeType = null,
        string? createNamespace = null,
        string? placeholder = null)
    {
        var hiddenQuery = string.Join(" ", new[]
        {
            string.IsNullOrWhiteSpace(@namespace) ? null : $"namespace:{@namespace}",
            string.IsNullOrWhiteSpace(scope) ? null : $"scope:{scope}",
            string.IsNullOrWhiteSpace(nodeType) ? null : $"nodeType:{nodeType}",
            string.IsNullOrWhiteSpace(query) ? null : query!.Trim(),
        }.Where(part => part is not null));

        var search = new MeshSearchControl().WithHiddenQuery(hiddenQuery);
        if (!string.IsNullOrWhiteSpace(@namespace))
            search = search.WithNamespace(@namespace!);
        if (!string.IsNullOrWhiteSpace(createNodeType))
            search = search with { CreateNodeType = createNodeType };
        if (!string.IsNullOrWhiteSpace(createNamespace))
            search = search with { CreateNamespace = createNamespace };
        if (!string.IsNullOrWhiteSpace(placeholder))
            search = search.WithPlaceholder(placeholder!);

        // WithView(view, area) names the tab's area by `label`; TabsControl.CreateItemSkin turns that into
        // the tab's TabSkin label, so the tab title menu reads the supplied labels.
        return tabs.WithView(search, label);
    }
}
