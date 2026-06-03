using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Portal.Components;

public partial class SearchBar : IDisposable
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

    [Inject]
    public required IJSRuntime JSRuntime { get; set; }

    private IMeshService _meshService = default!;

    // Typed search terms flow through this subject; the reactive pipeline in
    // OnInitialized debounces, switches to the latest term's suggestion stream,
    // and binds the WHOLE collection per emission. No channels, no await foreach.
    private readonly Subject<string> _terms = new();
    private IDisposable? _searchSubscription;
    private IDisposable? _defaultsSubscription;

    private ElementReference inputRef;
    private int _inputKey;
    private string? searchTerm;
    private QuerySuggestion[] suggestions = [];
    private bool showDropdown;
    private int highlightedIndex = -1;
    private bool isLoading;

    /// <summary>One progressive snapshot of the suggestion set; <see cref="Settled"/>
    /// marks the frame after the source has quieted (drops the progress bar).</summary>
    private readonly record struct SearchFrame(IReadOnlyList<QuerySuggestion> Suggestions, bool Settled);

    protected override void OnInitialized()
    {
        KeyCodeService.RegisterListener(OnKeyDownAsync);
        _meshService = Hub.ServiceProvider.GetRequiredService<IMeshService>();

        // Debounce keystrokes, switch to the latest term's live suggestion stream,
        // and bind the entire collection on every emission. MeshSearch.Suggestions
        // re-emits as each source converges (progressive CombineLatest inside the
        // mesh query surface), so partial results render immediately and re-order
        // by score as more sources return. Switch() cancels the prior search.
        _searchSubscription = _terms
            .Throttle(TimeSpan.FromMilliseconds(250))
            .DistinctUntilChanged()
            .Select(term => MeshSearch
                .Suggestions(_meshService, term, NavigationService?.CurrentNamespace, MaxResults)
                // Run the search once and derive two signals from it:
                //  • progressive — bind each NON-EMPTY snapshot as sources converge
                //    (Settled:false keeps the progress bar on). Skipping the leading
                //    all-empty frame means a refinement search doesn't blank the
                //    current results before the new ones land.
                //  • settle — a 700ms-quiet throttle binds the final snapshot and
                //    drops the progress bar, including the genuinely-empty (no
                //    results) case so the dropdown clears instead of spinning forever.
                .Publish(shared => shared
                    .Where(list => list.Count > 0)
                    .Select(list => new SearchFrame(list, Settled: false))
                    .Merge(shared
                        .Throttle(TimeSpan.FromMilliseconds(700))
                        .Select(list => new SearchFrame(list, Settled: true)))))
            .Switch()
            .Subscribe(OnFrame);
    }

    private void OnFrame(SearchFrame frame) => InvokeAsync(() =>
    {
        suggestions = frame.Suggestions.ToArray();
        if (frame.Settled)
            isLoading = false;
        StateHasChanged();
    });

    public async Task OnKeyDownAsync(FluentKeyCodeEventArgs? args)
    {
        if (args is not null && args.Key == KeyCode.Slash)
        {
            var isEditing = await JSRuntime.InvokeAsync<bool>(
                "eval",
                "(() => { const el = document.activeElement; if (!el) return false; const tag = el.tagName; if (tag === 'TEXTAREA' || (tag === 'INPUT' && /^(text|search|url|email|tel|password)$/i.test(el.type))) return true; if (el.isContentEditable) return true; if (el.closest('.monaco-editor')) return true; return false; })()"
            );
            if (!isEditing)
                _ = inputRef.FocusAsync();
        }
    }

    /// <summary>
    /// Captures input value and feeds the reactive pipeline — decoupled from
    /// rendering. The native input is uncontrolled (no value binding), so Blazor
    /// never pushes values back to the DOM and re-renders from search results
    /// cannot interfere with typing.
    /// </summary>
    private void OnInput(ChangeEventArgs e)
    {
        searchTerm = e.Value?.ToString();
        highlightedIndex = -1;

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            suggestions = [];
            showDropdown = false;
            isLoading = false;
            return;
        }

        isLoading = true;
        showDropdown = true;
        _terms.OnNext(searchTerm.Trim());
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
        suggestions = [];
        showDropdown = false;
        highlightedIndex = -1;
        isLoading = false;
        _inputKey++; // forces Blazor to recreate the <input>, clearing its DOM value
    }

    private void OnFocus()
    {
        if (suggestions.Length > 0)
        {
            showDropdown = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            isLoading = true;
            showDropdown = true;
            _defaultsSubscription?.Dispose();
            // Empty box on focus: bind the recently-accessed default set (whole
            // collection per emission, same progressive surface).
            _defaultsSubscription = MeshSearch
                .Suggestions(_meshService, null, NavigationService?.CurrentNamespace, MaxResults)
                .Subscribe(list => InvokeAsync(() =>
                {
                    suggestions = list.ToArray();
                    if (list.Count > 0)
                        isLoading = false;
                    StateHasChanged();
                }));
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

    public void Dispose()
    {
        KeyCodeService.UnregisterListener(OnKeyDownAsync, OnKeyDownAsync);
        _searchSubscription?.Dispose();
        _defaultsSubscription?.Dispose();
        _terms.Dispose();
    }
}
