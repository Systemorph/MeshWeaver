using MeshWeaver.Blazor.Monaco;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Portal.Components;

public partial class SearchBar : IAsyncDisposable
{
    private const string SearchPlaceholder = "Type / to search, @ for references...";

    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    [Inject]
    public required IKeyCodeService KeyCodeService { get; set; }

    [Inject]
    public IMeshQuery? MeshQuery { get; set; }

    private MonacoEditorView? monacoEditor;
    private string? searchTerm;

    protected override void OnInitialized()
    {
        KeyCodeService.RegisterListener(OnKeyDownAsync);
    }

    public Task OnKeyDownAsync(FluentKeyCodeEventArgs? args)
    {
        if (args is not null && args.Key == KeyCode.Slash)
        {
            monacoEditor?.FocusAsync();
        }
        return Task.CompletedTask;
    }

    private async Task<CompletionItem[]> GetCompletionsAsync(string query)
    {
        if (MeshQuery == null || string.IsNullOrWhiteSpace(query))
            return [];

        try
        {
            // Handle @ reference mode
            if (query.StartsWith("@"))
            {
                return await GetReferenceCompletionsAsync(query.Substring(1));
            }

            // Standard search mode
            var request = new MeshQueryRequest
            {
                Query = $"*{query}* scope:descendants",
                Limit = 10
            };

            var results = await MeshQuery.QueryAsync<MeshNode>(request).ToArrayAsync();
            return results.Select(ToCompletionItem).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private async Task<CompletionItem[]> GetReferenceCompletionsAsync(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            // Just "@" - show all nodes
            var request = new MeshQueryRequest { Query = "scope:descendants", Limit = 10 };
            var results = await MeshQuery!.QueryAsync<MeshNode>(request).ToArrayAsync();
            return results.Select(ToCompletionItem).ToArray();
        }

        // Check for scope pattern (e.g., "data:MyType/Id1")
        var colonIndex = reference.IndexOf(':');
        if (colonIndex > 0)
        {
            var scope = reference.Substring(0, colonIndex);
            var remainder = reference.Substring(colonIndex + 1);
            var query = string.IsNullOrWhiteSpace(remainder)
                ? $"nodeType:*{scope}* scope:descendants"
                : $"nodeType:*{scope}* *{remainder}* scope:descendants";

            var request = new MeshQueryRequest { Query = query, Limit = 10 };
            var results = await MeshQuery!.QueryAsync<MeshNode>(request).ToArrayAsync();
            return results.Select(ToCompletionItem).ToArray();
        }

        // Check for path with trailing slash for sub-completions
        if (reference.EndsWith("/"))
        {
            var basePath = reference.TrimEnd('/');
            var suggestions = await MeshQuery!.AutocompleteAsync(basePath, "", 10).ToArrayAsync();
            return suggestions.Select(s => new CompletionItem
            {
                Label = s.Name,
                InsertText = $"@{s.Path}/",
                Description = s.NodeType ?? "",
                Detail = s.Path,
                Category = ""
            }).ToArray();
        }

        // Standard node search with wildcard
        var searchRequest = new MeshQueryRequest
        {
            Query = $"*{reference}* scope:descendants",
            Limit = 10
        };
        var searchResults = await MeshQuery!.QueryAsync<MeshNode>(searchRequest).ToArrayAsync();
        return searchResults.Select(ToCompletionItem).ToArray();
    }

    private static CompletionItem ToCompletionItem(MeshNode node) => new()
    {
        Label = node.Name ?? node.Id,
        InsertText = node.Name ?? node.Id,
        Description = TruncateDescription(node.Description),
        Detail = GetNodeTypeDisplay(node.NodeType),
        Category = node.Category ?? ""
    };

    private async Task HandleSubmit()
    {
        if (monacoEditor == null) return;

        var text = await monacoEditor.GetValueAsync();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var encodedQuery = Uri.EscapeDataString(text.Trim());
        NavigationManager.NavigateTo($"/search?q={encodedQuery}");
    }

    private static string TruncateDescription(string? description, int maxLength = 60)
    {
        if (string.IsNullOrEmpty(description))
            return string.Empty;

        if (description.Length <= maxLength)
            return description;

        return description.Substring(0, maxLength - 3) + "...";
    }

    private static string GetNodeTypeDisplay(string? nodeType)
    {
        if (string.IsNullOrEmpty(nodeType))
            return string.Empty;

        var lastSlash = nodeType.LastIndexOf('/');
        return lastSlash >= 0 ? nodeType.Substring(lastSlash + 1) : nodeType;
    }

    public ValueTask DisposeAsync()
    {
        KeyCodeService.UnregisterListener(OnKeyDownAsync, OnKeyDownAsync);
        return ValueTask.CompletedTask;
    }
}
