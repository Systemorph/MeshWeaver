using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Blazor.Services;
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
    public BlazorAutocompleteService? AutocompleteService { get; set; }

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
        if (AutocompleteService == null || string.IsNullOrWhiteSpace(query))
            return [];

        try
        {
            return await AutocompleteService.GetCompletionsAsync(query);
        }
        catch
        {
            return [];
        }
    }

    private async Task HandleSubmit()
    {
        if (monacoEditor == null) return;

        var text = await monacoEditor.GetValueAsync();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var trimmed = text.Trim();

        // Check if it starts with @ (reference syntax)
        if (trimmed.StartsWith("@"))
        {
            var afterAt = trimmed[1..];
            var spaceIndex = afterAt.IndexOf(' ');

            if (spaceIndex < 0)
            {
                // Just @path with no space - navigate directly
                var path = afterAt.TrimEnd('/');
                if (!string.IsNullOrEmpty(path))
                {
                    NavigationManager.NavigateTo($"/{path}");
                    await monacoEditor.ClearAsync();
                    return;
                }
            }
            else
            {
                // @path query - convert to namespace:path scope:descendants query
                var path = afterAt[..spaceIndex].TrimEnd('/');
                var query = afterAt[(spaceIndex + 1)..].Trim();

                if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(query))
                {
                    // Build search query with namespace filter and descendants scope
                    var searchQuery = $"namespace:{path} scope:descendants {query}";
                    var encodedQuery = Uri.EscapeDataString(searchQuery);
                    NavigationManager.NavigateTo($"/search?q={encodedQuery}");
                    return;
                }
            }
        }

        // Plain search query (no @ prefix)
        var encodedPlainQuery = Uri.EscapeDataString(trimmed);
        NavigationManager.NavigateTo($"/search?q={encodedPlainQuery}");
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
