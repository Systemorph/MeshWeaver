using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Portal.Components;

public partial class SearchBar : IAsyncDisposable
{
    private const string SearchPlaceholder = "Type / to search, @ for references...";
    private const int MaxResults = 10;

    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    [Inject]
    public required IKeyCodeService KeyCodeService { get; set; }

    [Inject]
    public IMeshQuery? MeshQuery { get; set; }

    [Inject]
    public INavigationService? NavigationService { get; set; }

    private FluentTextField? textField;
    private string? searchTerm;
    private QuerySuggestion[] suggestions = [];
    private bool showDropdown;
    private int highlightedIndex = -1;
    private bool isLoading;
    private CancellationTokenSource? debounceCts;

    protected override void OnInitialized()
    {
        KeyCodeService.RegisterListener(OnKeyDownAsync);
    }

    public Task OnKeyDownAsync(FluentKeyCodeEventArgs? args)
    {
        if (args is not null && args.Key == KeyCode.Slash)
        {
            textField?.FocusAsync();
        }

        return Task.CompletedTask;
    }

    private async Task OnSearchTermChanged(string? value)
    {
        searchTerm = value;
        highlightedIndex = -1;

        if (string.IsNullOrWhiteSpace(value))
        {
            suggestions = [];
            showDropdown = false;
            isLoading = false;
            debounceCts?.Cancel();
            return;
        }

        // Debounce: cancel previous pending search
        debounceCts?.Cancel();
        debounceCts = new CancellationTokenSource();
        var ct = debounceCts.Token;

        isLoading = true;
        showDropdown = true;

        try
        {
            await Task.Delay(300, ct);
            if (ct.IsCancellationRequested) return;

            await AutocompleteAsync(value.Trim(), ct);
        }
        catch (OperationCanceledException)
        {
            // Expected when debounce cancels
        }
    }

    private async Task AutocompleteAsync(string input, CancellationToken ct)
    {
        if (MeshQuery == null)
        {
            isLoading = false;
            return;
        }

        try
        {
            string basePath;
            string prefix;

            if (input.StartsWith("@"))
            {
                var afterAt = input[1..];
                var lastSlash = afterAt.LastIndexOf('/');
                if (lastSlash >= 0)
                {
                    basePath = afterAt[..lastSlash];
                    prefix = afterAt[(lastSlash + 1)..];
                }
                else
                {
                    basePath = "";
                    prefix = afterAt;
                }
            }
            else
            {
                basePath = "";
                prefix = input;
            }

            var contextPath = NavigationService?.CurrentNamespace;
            var results = await MeshQuery
                .AutocompleteAsync(basePath, prefix, AutocompleteMode.RelevanceFirst, MaxResults, contextPath, ct)
                .ToArrayAsync(ct);

            if (!ct.IsCancellationRequested)
            {
                suggestions = results;
                isLoading = false;
                StateHasChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch
        {
            isLoading = false;
            suggestions = [];
        }
    }

    private void HandleKeyDown(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "ArrowDown":
                if (suggestions.Length > 0)
                {
                    showDropdown = true;
                    highlightedIndex = highlightedIndex < 0 ? 0 : (highlightedIndex + 1) % suggestions.Length;
                }
                break;

            case "ArrowUp":
                if (suggestions.Length > 0)
                {
                    showDropdown = true;
                    highlightedIndex = highlightedIndex <= 0
                        ? suggestions.Length - 1
                        : highlightedIndex - 1;
                }
                break;

            case "Enter":
                if (highlightedIndex >= 0 && highlightedIndex < suggestions.Length)
                {
                    NavigateToSuggestion(suggestions[highlightedIndex]);
                }
                else
                {
                    HandleSubmit();
                }
                break;

            case "Escape":
                showDropdown = false;
                highlightedIndex = -1;
                break;
        }
    }

    private void HandleSubmit()
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return;

        var trimmed = searchTerm.Trim();

        // Check if it starts with @ (reference syntax)
        if (trimmed.StartsWith("@"))
        {
            var afterAt = trimmed[1..];
            var spaceIndex = afterAt.IndexOf(' ');

            if (spaceIndex < 0)
            {
                var path = afterAt.TrimEnd('/');
                if (!string.IsNullOrEmpty(path))
                {
                    NavigationManager.NavigateTo($"/{path}");
                    ClearSearch();
                    return;
                }
            }
            else
            {
                var path = afterAt[..spaceIndex].TrimEnd('/');
                var query = afterAt[(spaceIndex + 1)..].Trim();

                if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(query))
                {
                    var searchQuery = $"namespace:{path} scope:descendants {query}";
                    var encodedQuery = Uri.EscapeDataString(searchQuery);
                    NavigationManager.NavigateTo($"/search?q={encodedQuery}");
                    ClearSearch();
                    return;
                }
            }
        }

        // Plain search query - navigate to search page which uses QueryAsync
        var encodedPlainQuery = Uri.EscapeDataString(trimmed);
        NavigationManager.NavigateTo($"/search?q={encodedPlainQuery}");
        ClearSearch();
    }

    private void NavigateToSuggestion(QuerySuggestion suggestion)
    {
        NavigationManager.NavigateTo($"/{suggestion.Path}");
        ClearSearch();
    }

    private void ClearSearch()
    {
        searchTerm = null;
        suggestions = [];
        showDropdown = false;
        highlightedIndex = -1;
        isLoading = false;
        debounceCts?.Cancel();
    }

    private async Task OnFocus()
    {
        if (suggestions.Length > 0)
        {
            showDropdown = true;
            return;
        }

        // When focused with no text, show top-level items
        if (string.IsNullOrWhiteSpace(searchTerm) && MeshQuery != null)
        {
            isLoading = true;
            showDropdown = true;
            try
            {
                var contextPath = NavigationService?.CurrentNamespace;
                suggestions = await MeshQuery
                    .AutocompleteAsync("", "", AutocompleteMode.RelevanceFirst, MaxResults, contextPath)
                    .ToArrayAsync();
                isLoading = false;
                StateHasChanged();
            }
            catch
            {
                isLoading = false;
                suggestions = [];
            }
        }
    }

    private async Task OnBlur()
    {
        await Task.Delay(200);
        showDropdown = false;
        highlightedIndex = -1;
        StateHasChanged();
    }

    private static string GetInitial(QuerySuggestion suggestion)
    {
        var name = suggestion.Name;
        return string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();
    }

    private static string GetNodeTypeDisplay(string? nodeType)
    {
        if (string.IsNullOrEmpty(nodeType))
            return string.Empty;

        var lastSlash = nodeType.LastIndexOf('/');
        return lastSlash >= 0 ? nodeType[(lastSlash + 1)..] : nodeType;
    }

    public ValueTask DisposeAsync()
    {
        KeyCodeService.UnregisterListener(OnKeyDownAsync, OnKeyDownAsync);
        debounceCts?.Cancel();
        debounceCts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
