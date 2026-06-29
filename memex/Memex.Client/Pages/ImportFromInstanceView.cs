using Memex.Client.Services;
using MeshWeaver.Mesh;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;

namespace Memex.Client.Pages;

/// <summary>
/// "Import from a remote instance": a search/browse box over a connected remote <see cref="MemexInstance"/>
/// (via <see cref="RemoteImporter"/>) with multi-select + an Import action that copies the selected node(s)
/// AND their descendant subtree into the LOCAL mesh under a chosen namespace (default the writable
/// <c>device-user</c> partition). Progress and the final count surface to a visible status label; errors are
/// shown, never swallowed. Hosted inside <see cref="InstanceManagerView"/>, which swaps it into its content
/// frame and supplies the back action.
/// </summary>
public sealed class ImportFromInstanceView : ContentView
{
    private readonly RemoteImporter _importer;
    private readonly MemexInstance _instance;

    private readonly Entry _search = new()
    {
        Placeholder = "Search the remote mesh… (e.g. nodeType:Story)",
        Text = RemoteImporter.DefaultQuery,
        ReturnType = ReturnType.Search,
        VerticalOptions = LayoutOptions.Center,
    };
    private readonly Entry _target = new()
    {
        Text = "device-user",
        Placeholder = "Local target namespace",
        VerticalOptions = LayoutOptions.Center,
    };
    private readonly VerticalStackLayout _results = new() { Spacing = 6 };
    private readonly Label _status = new() { FontSize = 12, TextColor = Colors.Gray };
    private readonly Button _importBtn;

    // path -> checked. Reset on every search; drives the Import button's enabled state.
    private readonly Dictionary<string, bool> _selected = new();
    private int _lastCount;
    private IDisposable? _searchSub;
    private IDisposable? _importSub;

    public ImportFromInstanceView(RemoteImporter importer, MemexInstance instance, Action onBack)
    {
        _importer = importer;
        _instance = instance;

        var back = new Button { Text = "← Back", HorizontalOptions = LayoutOptions.Start, BackgroundColor = Colors.Transparent };
        back.Clicked += (_, _) => onBack();

        var searchBtn = new Button { Text = "Search" };
        searchBtn.Clicked += (_, _) => RunSearch();
        _search.Completed += (_, _) => RunSearch();

        _importBtn = new Button { Text = "Import selected", IsEnabled = false };
        _importBtn.Clicked += (_, _) => RunImport();

        var searchRow = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        searchRow.Add(WrapBox(_search), 0);
        searchRow.Add(searchBtn, 1);

        var targetRow = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star) } };
        targetRow.Add(new Label { Text = "Import into", FontSize = 13, VerticalOptions = LayoutOptions.Center }, 0);
        targetRow.Add(WrapBox(_target), 1);

        var importRow = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star) } };
        importRow.Add(_importBtn, 0);
        importRow.Add(_status, 1);

        Content = new ScrollView
        {
            Padding = new Thickness(20, 16),
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    back,
                    new Label { Text = $"Import from {_instance.Name}", FontSize = 22, FontAttributes = FontAttributes.Bold },
                    new Label { Text = _instance.Url, FontSize = 12, TextColor = Colors.Gray },
                    searchRow,
                    targetRow,
                    importRow,
                    new BoxView { HeightRequest = 1, Color = Colors.Gray, Opacity = 0.25 },
                    _results,
                },
            },
        };

        // Default: list the remote's top-level nodes immediately.
        RunSearch();
    }

    private static Border WrapBox(View inner) => new()
    {
        StrokeThickness = 1,
        Stroke = Color.FromArgb("#3A3A3C"),
        BackgroundColor = Color.FromArgb("#1C1C1E"),
        Padding = new Thickness(10, 0),
        StrokeShape = new RoundRectangle { CornerRadius = 8 },
        Content = inner,
    };

    private void RunSearch()
    {
        _results.Clear();
        _results.Add(new ActivityIndicator { IsRunning = true, Margin = new Thickness(4, 12) });
        _status.Text = "Searching…";

        _searchSub?.Dispose();
        _searchSub = _importer.Search(_instance, _search.Text)
            .Subscribe(
                nodes => MainThread.BeginInvokeOnMainThread(() => RenderResults(nodes)),
                ex => MainThread.BeginInvokeOnMainThread(() =>
                {
                    _results.Clear();
                    _status.Text = $"Search failed: {ex.Message}";
                }));
    }

    private void RenderResults(IEnumerable<MeshNode> nodes)
    {
        var list = nodes.Where(n => !string.IsNullOrEmpty(n.Path)).ToList();
        _selected.Clear();
        _results.Clear();
        UpdateImportButton();

        if (list.Count == 0)
        {
            _status.Text = "";
            _results.Add(new Label { Text = "No nodes found.", TextColor = Colors.Gray, Margin = new Thickness(4, 8) });
            return;
        }

        foreach (var node in list)
            _results.Add(ResultRow(node));

        _status.Text = $"{list.Count} node(s). Select nodes to import (their subtree comes along).";
    }

    private View ResultRow(MeshNode node)
    {
        var path = node.Path;
        var name = node.Name ?? path;

        var check = new CheckBox { VerticalOptions = LayoutOptions.Center };
        check.CheckedChanged += (_, e) => { _selected[path] = e.Value; UpdateImportButton(); };

        var labels = new VerticalStackLayout
        {
            Spacing = 1,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = name, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation },
                new Label
                {
                    Text = string.IsNullOrEmpty(node.NodeType) ? path : $"{node.NodeType} · {path}",
                    FontSize = 11, TextColor = Colors.Gray, LineBreakMode = LineBreakMode.TailTruncation,
                },
            },
        };

        var row = new Grid { ColumnSpacing = 6, ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star) } };
        row.Add(check, 0);
        row.Add(labels, 1);

        return new Border
        {
            Padding = new Thickness(10, 6),
            StrokeThickness = 1,
            Stroke = Color.FromArgb("#3A3A3C"),
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Content = row,
        };
    }

    private void UpdateImportButton() => _importBtn.IsEnabled = _selected.Any(kv => kv.Value);

    private void RunImport()
    {
        var paths = _selected.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        if (paths.Count == 0) return;

        var targetNs = _target.Text?.Trim() ?? "device-user";
        _importBtn.IsEnabled = false;
        _lastCount = 0;
        _status.Text = "Importing…";

        _importSub?.Dispose();
        _importSub = _importer.Import(_instance, paths, targetNs)
            .Subscribe(
                count => MainThread.BeginInvokeOnMainThread(() =>
                {
                    _lastCount = count;
                    _status.Text = $"Imported {count} node(s)…";
                }),
                ex => MainThread.BeginInvokeOnMainThread(() =>
                {
                    _status.Text = $"Import failed: {ex.Message}";
                    UpdateImportButton();
                }),
                () => MainThread.BeginInvokeOnMainThread(() =>
                {
                    _status.Text = $"Import complete — {_lastCount} node(s) into {targetNs}.";
                    UpdateImportButton();
                }));
    }
}
