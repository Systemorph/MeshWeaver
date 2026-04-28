using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Components;

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
    private CancellationTokenSource? _debounceCts;
    private int _inputKey;
    private const int DebounceMs = 200;

    private MeshNode[]? BoundItems { get; set; }
    private bool HasItems => BoundItems is { Length: > 0 };

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

    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        if (!string.IsNullOrEmpty(Value) && (_selectedNode == null || _selectedNode.Path != Value))
        {
            ResolveSelectedNode();
        }
    }

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
        LoadResultsAsync();
        StateHasChanged();
    }

    private void CloseSearch()
    {
        _isSearchOpen = false;
        StateHasChanged();
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
        if (HasItems && _cachedResults != null)
        {
            _results = FilterCached(_searchText.Trim());
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        // Debounce remote queries. Do NOT call StateHasChanged here — typing must never
        // trigger a component re-render. LoadResultsAsync will render when results arrive.
        _debounceCts?.Cancel();
        var cts = _debounceCts = new CancellationTokenSource();
        _ = DebouncedSearchAsync(cts.Token);
    }

    private async Task DebouncedSearchAsync(CancellationToken ct)
    {
        try { await Task.Delay(DebounceMs, ct); }
        catch (TaskCanceledException) { return; }
        if (ct.IsCancellationRequested) return;
        LoadResultsAsync();
    }

    private void LoadResultsAsync()
    {
        // When Items are provided and already cached, filter in-memory
        if (HasItems && _cachedResults != null)
        {
            _results = FilterCached(_searchText.Trim());
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        // Cancel any in-flight query so only the latest keystroke wins
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        _isLoading = true;
        _ = InvokeAsync(StateHasChanged);

        var queries = GetQueries();
        if (queries.Length == 0)
        {
            FinaliseLoadResults(new List<MeshNode>(), cts);
            return;
        }

        var userText = _searchText.Trim();
        var observables = queries.Select(baseQuery =>
        {
            var fullQuery = HasItems || string.IsNullOrEmpty(userText)
                ? baseQuery
                : $"{baseQuery} {userText}";
            return MeshQuery
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(fullQuery))
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

            if (HasItems)
            {
                // Cache for in-memory filtering
                _cachedResults = merged;
                _results = FilterCached(_searchText.Trim());
            }
            else
            {
                _results = merged;
            }
        }
        catch
        {
            _results = new List<MeshNode>();
        }
        finally
        {
            _isLoading = false;
            _ = InvokeAsync(StateHasChanged);
        }
    }

    private List<MeshNode> FilterCached(string searchText)
    {
        if (_cachedResults == null) return new List<MeshNode>();
        if (string.IsNullOrEmpty(searchText)) return _cachedResults;

        return _cachedResults
            .Where(n =>
                (n.Name ?? "").Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || (n.Path ?? "").Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || (n.NodeType ?? "").Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || (n.Id ?? "").Contains(searchText, StringComparison.OrdinalIgnoreCase))
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
        Hub.GetMeshNode(Value, TimeSpan.FromSeconds(10))
            .Subscribe(
                node =>
                {
                    _selectedNode = node ?? new MeshNode(Value) { Name = Value };
                    InvokeAsync(StateHasChanged);
                },
                _ =>
                {
                    _selectedNode = new MeshNode(Value) { Name = Value };
                    InvokeAsync(StateHasChanged);
                });
    }
}
