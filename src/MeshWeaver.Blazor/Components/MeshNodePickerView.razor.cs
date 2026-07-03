using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Blazor view for <c>MeshNodePickerControl</c> — a combobox that lets the user search for and
/// select a mesh node by path, backed by one or more mesh queries plus optional pre-loaded
/// <c>Items</c>. Supports keyboard navigation, debounced remote search, DefaultToFirst
/// auto-selection, and both normal and thin/upward-opening layout modes.
/// </summary>
public partial class MeshNodePickerView : FormComponentBase<MeshNodePickerControl, MeshNodePickerView, string>
{
    [Inject]
    private IMeshService MeshQuery { get; set; } = default!;

    private MeshNode? _selectedNode;
    private bool _isSearchOpen;
    private bool _isLoading;
    private string _searchText = "";
    private ElementReference _textFieldElement;
    private List<MeshNode> _results = new();
    private List<MeshNode>? _cachedResults;
    private CancellationTokenSource? _loadCts;
    private IDisposable? _debounceSub;
    private int _inputKey;
    private const int DebounceMs = 200;

    // Keyboard navigation: the index into _results currently highlighted (pre-selected for Enter).
    // Reset to 0 (first row) whenever the dropdown opens or the result set changes.
    private int _highlightedIndex;

    // Default-to-first: when [MeshNode(DefaultToFirst = true)] and no value is set, load once and
    // auto-select the first result. _defaultApplied guards it to at most once per component.
    private bool _defaultApplied;
    private bool _selectFirstOnLoad;

    private MeshNode[]? BoundItems { get; set; }
    private bool HasItems => BoundItems is { Length: > 0 };

    // Client-side filtering: pre-loaded Items OR an explicit FilterInMemory opt-in on the
    // control (bounded sets like the access-subject picker). The base queries load once
    // WITHOUT the typed text; keystrokes filter the cached set diacritic-insensitively via
    // SearchText.Matches — server-side ILIKE would miss "Burgi" → "Bürgi" (issue #213).
    private bool UseClientFilter => HasItems || ViewModel?.FilterInMemory == true;

    // Compact "thin" rendering + open-upward dropdown, driven by the control's Layout/Open.
    private bool IsThin => ViewModel?.Layout == MeshNodePickerLayout.Thin;
    private bool OpensUp => ViewModel?.Open == MeshNodePickerOpenDirection.Up;

    /// <summary>
    /// Binds the pre-loaded <c>Items</c> collection from the view-model and delegates to the
    /// base class to wire up the common form data-binding pipeline.
    /// </summary>
    protected override void BindData()
    {
        DataBind(ViewModel.Items, x => x.BoundItems, ConvertItems);
        base.BindData();
    }

    private MeshNode[]? ConvertItems(object? value, MeshNode[]? defaultValue)
    {
        if (value is object[] arr && arr.Length > 0)
        {
            return arr.Select(item => item switch
            {
                MeshNode node => node,
                JsonElement je => je.Deserialize<MeshNode>(Hub.JsonSerializerOptions),
                _ => null
            }).Where(n => n != null).ToArray()!;
        }
        if (value is JsonElement je2 && je2.ValueKind == JsonValueKind.Array)
        {
            return je2.Deserialize<MeshNode[]>(Hub.JsonSerializerOptions);
        }
        return defaultValue;
    }

    /// <summary>
    /// After parameters are applied, resolves the display node when a non-empty <c>Value</c>
    /// arrives that does not match the current selection, or triggers DefaultToFirst
    /// auto-selection when the value is still empty and the option is enabled.
    /// </summary>
    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        if (!string.IsNullOrEmpty(Value) && (_selectedNode == null || _selectedNode.Path != Value))
        {
            ResolveSelectedNode();
        }
        else if (string.IsNullOrEmpty(Value)
                 && ViewModel is { DefaultToFirst: true }
                 && !_defaultApplied
                 && (GetQueries().Length > 0 || HasItems))
        {
            ApplyDefaultSelection();
        }
    }

    /// <summary>
    /// Opt-in default ([MeshNode(DefaultToFirst = true)]): when no value is selected, load results
    /// once and auto-select the first as the persisted default. Runs at most once per component;
    /// FinaliseLoadResults performs the selection when the batch arrives. Does not open the dropdown.
    /// </summary>
    private void ApplyDefaultSelection()
    {
        _defaultApplied = true;
        _selectFirstOnLoad = true;
        LoadResultsAsync();
    }

    /// <summary>
    /// After each render, focuses the search input field when the dropdown has just been opened
    /// so the user can start typing immediately without a manual click.
    /// </summary>
    /// <param name="firstRender">Whether this is the first render of the component.</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        if (_isSearchOpen && _textFieldElement.Id != null)
        {
            try
            {
                await _textFieldElement.FocusAsync();
            }
            catch
            {
                // Element may not be in DOM yet
            }
        }
    }

    private string[] GetQueries()
    {
        return ViewModel?.Queries ?? [];
    }

    private void OpenSearch()
    {
        _isSearchOpen = true;
        _highlightedIndex = 0;
        LoadResultsAsync();
        StateHasChanged();
    }

    private void CloseSearch()
    {
        _isSearchOpen = false;
        StateHasChanged();
    }

    /// <summary>
    /// Keyboard navigation of the open dropdown: ↑/↓ move the highlight (wrapping at the ends), Enter
    /// commits the highlighted row, Escape closes. The search box is single-line, so the arrow keys'
    /// default cursor-move is harmless; markup-level stopPropagation keeps Enter from bubbling to the
    /// chat composer's send. This is what makes the picker fully operable from the keyboard.
    /// </summary>
    private void OnKeyDown(KeyboardEventArgs e)
    {
        if (!_isSearchOpen)
            return;

        switch (e.Key)
        {
            case "ArrowDown":
                if (_results.Count > 0)
                    _highlightedIndex = (_highlightedIndex + 1) % _results.Count;
                StateHasChanged();
                break;
            case "ArrowUp":
                if (_results.Count > 0)
                    _highlightedIndex = (_highlightedIndex - 1 + _results.Count) % _results.Count;
                StateHasChanged();
                break;
            case "Enter":
                if (_highlightedIndex >= 0 && _highlightedIndex < _results.Count)
                    SelectNode(_results[_highlightedIndex]);
                break;
            case "Escape":
                CloseSearch();
                break;
        }
    }

    /// <summary>
    /// Capture input and fire debounced search — decoupled from rendering.
    /// Input is uncontrolled (no value binding), so Blazor never pushes back to the DOM.
    /// Re-renders from search results cannot interfere with typing.
    /// </summary>
    private void OnSearchInput(ChangeEventArgs e)
    {
        _searchText = e.Value?.ToString() ?? "";
        _isSearchOpen = true;

        // In-memory filter path — cheap, no debounce.
        if (UseClientFilter && _cachedResults != null)
        {
            _results = FilterCached(_searchText.Trim());
            _highlightedIndex = 0;
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        // Debounce remote queries. Do NOT call StateHasChanged here — typing must never
        // trigger a component re-render. LoadResultsAsync will render when results arrive.
        // Reactive debounce: each keystroke disposes the prior pending Observable.Timer
        // and arms a new one — same semantics as the old CTS-cancel + Task.Delay loop,
        // but stays on Rx scheduling rather than ad-hoc CancellationTokenSource churn.
        _debounceSub?.Dispose();
        _debounceSub = Observable.Timer(TimeSpan.FromMilliseconds(DebounceMs))
            .Subscribe(_ => LoadResultsAsync());
    }

    private void LoadResultsAsync()
    {
        // When the cached client-filter set is already loaded, filter in-memory
        if (UseClientFilter && _cachedResults != null)
        {
            _results = FilterCached(_searchText.Trim());
            _highlightedIndex = 0;
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        // Cancel any in-flight query so only the latest keystroke wins
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        // Only flash the spinner when there's nothing to show yet. Reopening a picker that already
        // holds results (the composer pre-loads its small catalog via DefaultToFirst) keeps showing
        // them and refreshes in the background — so the dropdown appears INSTANTLY instead of blanking
        // to a progress ring on every open (the "popup doesn't come instantly" complaint).
        if (_results.Count == 0)
        {
            _isLoading = true;
            _ = InvokeAsync(StateHasChanged);
        }

        var queries = GetQueries();
        if (queries.Length == 0)
        {
            FinaliseLoadResults(new List<MeshNode>(), cts);
            return;
        }

        var userText = _searchText.Trim();
        var observables = queries.Select(baseQuery =>
        {
            var fullQuery = UseClientFilter || string.IsNullOrEmpty(userText)
                ? baseQuery
                : $"{baseQuery} {userText}";
            return MeshQuery
                .Query<MeshNode>(MeshQueryRequest.FromQuery(fullQuery))
                .Take(1)
                .Select(c => (IReadOnlyList<MeshNode>)c.Items)
                .Catch<IReadOnlyList<MeshNode>, Exception>(
                    _ => Observable.Return<IReadOnlyList<MeshNode>>(new List<MeshNode>()));
        });

        // Subscribe — no await on hub round-trip. CombineLatest waits for the initial
        // set from every query before firing once. The Subscribe callback finishes the
        // load on the dispatcher via InvokeAsync.
        observables.CombineLatest()
            .Take(1)
            .Subscribe(allBatches =>
            {
                if (cts.Token.IsCancellationRequested) return;
                var queryResults = allBatches.SelectMany(batch => batch).ToList();
                FinaliseLoadResults(queryResults, cts);
            });
    }

    private void FinaliseLoadResults(List<MeshNode> queryResults, CancellationTokenSource cts)
    {
        if (cts.Token.IsCancellationRequested) return;

        try
        {
            // Merge Items + query results, deduplicate by Path (case-insensitive; Items take precedence)
            var items = BoundItems ?? [];
            var merged = items.AsEnumerable()
                .Concat(queryResults)
                .GroupBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (UseClientFilter)
            {
                // Cache for in-memory filtering
                _cachedResults = merged;
                _results = FilterCached(_searchText.Trim());
            }
            else
            {
                _results = merged;
            }

            // New result set → reset the keyboard highlight to the first (top) row.
            _highlightedIndex = 0;
        }
        catch
        {
            _results = new List<MeshNode>();
        }
        finally
        {
            _isLoading = false;
            _ = InvokeAsync(() =>
            {
                // DefaultToFirst: select the first result as the persisted default — only if still
                // unset (the user may have picked something while results loaded).
                if (_selectFirstOnLoad)
                {
                    _selectFirstOnLoad = false;
                    if (string.IsNullOrEmpty(Value) && _results.Count > 0)
                    {
                        SelectNode(_results[0]);
                        return;
                    }
                }
                StateHasChanged();
            });
        }
    }

    private List<MeshNode> FilterCached(string searchText)
    {
        if (_cachedResults == null) return new List<MeshNode>();
        if (string.IsNullOrEmpty(searchText)) return _cachedResults;

        // Diacritic- AND case-insensitive: "Burgi" matches "Bürgi" (SearchText.Fold).
        return _cachedResults
            .Where(n => SearchText.Matches(searchText, n.Name, n.Path, n.NodeType, n.Id))
            .ToList();
    }

    private void SelectNode(MeshNode node)
    {
        _selectedNode = node;
        Value = node.Path;
        _isSearchOpen = false;
        _searchText = "";
        _inputKey++; // force recreate of <input> to clear its DOM value
        // Directly update the data pointer — the debounced pipeline in
        // FormComponentBase uses Skip(1) which swallows single-selection updates.
        if (ViewModel?.Data is JsonPointerReference pointer)
            UpdatePointer(node.Path, pointer);
        StateHasChanged();
    }

    private void ClearSelection()
    {
        _selectedNode = null;
        Value = "";
        _searchText = "";
        _inputKey++; // force recreate of <input> to clear its DOM value
        if (ViewModel?.Data is JsonPointerReference pointer)
            UpdatePointer("", pointer);
        StateHasChanged();
    }

    private void ResolveSelectedNode()
    {
        if (string.IsNullOrEmpty(Value)) return;

        // First check Items for the selected node (avoids hub round-trip).
        var items = BoundItems ?? [];
        if (items.Length > 0)
        {
            _selectedNode = items.FirstOrDefault(n =>
                string.Equals(n.Path, Value, StringComparison.OrdinalIgnoreCase));
            if (_selectedNode != null)
            {
                StateHasChanged();
                return;
            }
        }

        // Reactive — Subscribe, never await (await on a hub round-trip is a 100%
        // deadlock; see Doc/Architecture/AsynchronousCalls.md).
        // When the node can't be resolved (unknown / moved / mismatched path — e.g. a
        // stale agent selection like "AgenticPension/Agent/Datenextraktion"), the combobox
        // must still show the friendly SHORT name (the last path segment), never the raw
        // full path. The Value retains the full path for resolution; only the DISPLAY is
        // shortened.
        Hub.GetMeshNode(Value, TimeSpan.FromSeconds(10))
            .Subscribe(
                node =>
                {
                    _selectedNode = node ?? PlaceholderNode(Value);
                    InvokeAsync(StateHasChanged);
                },
                _ =>
                {
                    _selectedNode = PlaceholderNode(Value);
                    InvokeAsync(StateHasChanged);
                });
    }

    /// <summary>
    /// A display-only stand-in for an unresolved selection: keeps the full path as the
    /// node Path/Id but shows the SHORT name (last path segment) in the combobox so the
    /// user sees "Datenextraktion", not "AgenticPension/Agent/Datenextraktion".
    /// </summary>
    private static MeshNode PlaceholderNode(string path)
    {
        var shortName = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        return MeshNode.FromPath(path) with { Name = shortName };
    }
}
