using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Portal.Components;

public partial class SearchBar : IAsyncDisposable
{
    private const string SearchPlaceholder = "Search the mesh... (e.g. nodeType:Story status:Open)";
    private const int MaxResults = 10;

    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    [Inject]
    public required IKeyCodeService KeyCodeService { get; set; }

    [Inject]
    public required IMessageHub Hub { get; set; }

    [Inject]
    public INavigationService? NavigationService { get; set; }

    private SearchHub? _searchHub;
    private ElementReference inputRef;
    private int _inputKey;
    private string? searchTerm;
    private QuerySuggestion[] suggestions = [];
    private bool showDropdown;
    private int highlightedIndex = -1;
    private bool isLoading;
    private string? _lastSearchedTerm;
    private bool _isFirstKeystroke = true;
    private CancellationTokenSource? debounceCts;

    protected override void OnInitialized()
    {
        KeyCodeService.RegisterListener(OnKeyDownAsync);
        _searchHub = new SearchHub(Hub);
    }

    public Task OnKeyDownAsync(FluentKeyCodeEventArgs? args)
    {
        if (args is not null && args.Key == KeyCode.Slash)
            _ = inputRef.FocusAsync();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Captures input value and fires search — completely decoupled from rendering.
    /// The native input is uncontrolled (no value binding), so Blazor never
    /// pushes values back to the DOM. Re-renders from search results cannot
    /// interfere with typing.
    /// </summary>
    private void OnInput(ChangeEventArgs e)
    {
        searchTerm = e.Value?.ToString();
        highlightedIndex = -1;

        debounceCts?.Cancel();

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            suggestions = [];
            showDropdown = false;
            isLoading = false;
            _isFirstKeystroke = true;
            return;
        }

        isLoading = true;
        showDropdown = true;

        // Clear stale results when the query diverges (e.g. start changed)
        if (!IsRefinement(searchTerm.Trim(), _lastSearchedTerm))
            suggestions = [];

        var cts = new CancellationTokenSource();
        debounceCts = cts;
        _ = DebounceAndSearchAsync(searchTerm.Trim(), cts.Token);
    }

    private async Task DebounceAndSearchAsync(string input, CancellationToken ct)
    {
        try
        {
            // Show loading dropdown immediately
            await InvokeAsync(StateHasChanged);

            if (!_isFirstKeystroke)
            {
                await Task.Delay(300, ct);
                if (ct.IsCancellationRequested) return;
            }
            _isFirstKeystroke = false;

            await StreamSearchResultsAsync(input, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected when debounce cancels
        }
    }

    private async Task StreamSearchResultsAsync(string input, CancellationToken ct)
    {
        if (_searchHub == null)
        {
            isLoading = false;
            return;
        }

        try
        {
            var results = new List<QuerySuggestion>();
            var contextPath = NavigationService?.CurrentNamespace;
            var firstBatchRendered = false;

            await foreach (var suggestion in _searchHub.SearchAsync(input, contextPath, MaxResults, ct))
            {
                if (ct.IsCancellationRequested) break;

                var idx = results.FindIndex(s => s.Score < suggestion.Score);
                if (idx < 0)
                    results.Add(suggestion);
                else
                    results.Insert(idx, suggestion);

                if (results.Count > MaxResults)
                    results.RemoveAt(results.Count - 1);

                // Render once when the first results arrive so the user sees
                // suggestions + progress bar (isLoading is still true).
                if (!firstBatchRendered)
                {
                    firstBatchRendered = true;
                    suggestions = results.ToArray();
                    await InvokeAsync(StateHasChanged);
                }
            }

            if (!ct.IsCancellationRequested)
            {
                _lastSearchedTerm = input;
                suggestions = results.ToArray();
                isLoading = false;
                await InvokeAsync(StateHasChanged);
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
                    StateHasChanged();
                }
                break;

            case "ArrowUp":
                if (suggestions.Length > 0)
                {
                    showDropdown = true;
                    highlightedIndex = highlightedIndex <= 0
                        ? suggestions.Length - 1
                        : highlightedIndex - 1;
                    StateHasChanged();
                }
                break;

            case "Enter":
                if (highlightedIndex >= 0 && highlightedIndex < suggestions.Length)
                    NavigateToSuggestion(suggestions[highlightedIndex]);
                else
                    HandleSubmit();
                break;

            case "Escape":
                showDropdown = false;
                highlightedIndex = -1;
                StateHasChanged();
                break;
        }
    }

    private void HandleSubmit()
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return;

        var trimmed = searchTerm.Trim();

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

        var encodedPlainQuery = Uri.EscapeDataString(trimmed);
        var hq = Uri.EscapeDataString("scope:descendants");
        NavigationManager.NavigateTo($"/search?q={encodedPlainQuery}&hq={hq}");
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
        _lastSearchedTerm = null;
        suggestions = [];
        showDropdown = false;
        highlightedIndex = -1;
        isLoading = false;
        _isFirstKeystroke = true;
        debounceCts?.Cancel();
        _inputKey++; // forces Blazor to recreate the <input>, clearing its DOM value
    }

    /// <summary>
    /// Returns true if the new query is a refinement of the previous one
    /// (i.e. starts with the same prefix). Stale results are kept visible
    /// while the refined search runs. Returns false when the start diverges,
    /// triggering an immediate clear of the dropdown.
    /// </summary>
    private static bool IsRefinement(string current, string? previous)
    {
        if (string.IsNullOrEmpty(previous))
            return false;
        return current.StartsWith(previous, StringComparison.OrdinalIgnoreCase)
            || previous.StartsWith(current, StringComparison.OrdinalIgnoreCase);
    }

    private void OnFocus()
    {
        if (suggestions.Length > 0)
        {
            showDropdown = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(searchTerm) && _searchHub != null)
        {
            isLoading = true;
            showDropdown = true;
            _ = LoadDefaultSuggestionsAsync();
        }
    }

    private async Task LoadDefaultSuggestionsAsync()
    {
        try
        {
            var contextPath = NavigationService?.CurrentNamespace;
            var results = new List<QuerySuggestion>();
            await foreach (var s in _searchHub!.SearchAsync(null, contextPath, MaxResults, CancellationToken.None))
                results.Add(s);
            suggestions = results.ToArray();
            isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
        catch
        {
            isLoading = false;
            suggestions = [];
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
