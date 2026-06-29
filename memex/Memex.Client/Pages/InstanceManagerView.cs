using Memex.Client.Services;

namespace Memex.Client.Pages;

/// <summary>
/// The instance switcher/manager as a hostable <see cref="ContentView"/> inside <see cref="PortalShellPage"/>:
/// add an instance by base URL (+ optional API token), remove one, open one (which the shell makes the
/// current instance), or <b>import</b> nodes from an authenticated remote instance. The shell's title-bar
/// instance button navigates here for "connect new"; the per-row open button invokes <see cref="OnOpen"/>;
/// the per-row "Import…" button connects (if needed) and swaps in an <see cref="ImportFromInstanceView"/>.
/// </summary>
public sealed class InstanceManagerView : ContentView
{
    private readonly InstanceStore _store;
    private readonly RemoteImporter _importer;
    private readonly MeshConnector _connector;
    private readonly VerticalStackLayout _list;
    private readonly Entry _name = new() { Placeholder = "Name (optional)" };
    private readonly Entry _url = new() { Text = InstanceStore.PublicMemexUrl, Keyboard = Keyboard.Url };
    private readonly Entry _token = new() { Placeholder = "API token (mw_…) — optional", IsPassword = true };

    // The instance-manager's own content — restored when the import sub-view's "← Back" is pressed.
    private View _rootContent = null!;

    /// <summary>Invoked when the user opens an instance — the shell makes it current and shows its portal.</summary>
    public Action<MemexInstance>? OnOpen { get; set; }

    public InstanceManagerView(InstanceStore store, RemoteImporter importer, MeshConnector connector)
    {
        _store = store;
        _importer = importer;
        _connector = connector;

        _list = new VerticalStackLayout { Spacing = 8 };
        var add = new Button { Text = "Add instance" };
        add.Clicked += OnAdd;

        Content = _rootContent = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 20,
                Spacing = 16,
                Children =
                {
                    new Label { Text = "Your memex instances", FontSize = 22, FontAttributes = FontAttributes.Bold },
                    _list,
                    new BoxView { HeightRequest = 1, Color = Colors.Gray, Opacity = 0.4 },
                    new Label { Text = "Add an instance", FontAttributes = FontAttributes.Bold },
                    _name,
                    _url,
                    _token,
                    add,
                    new Label
                    {
                        Text = "Base URL + an optional API token. With a token, the local mesh joins that "
                             + "instance so it can be controlled from the remote mesh.",
                        FontSize = 12, TextColor = Colors.Gray
                    },
                }
            }
        };

        Refresh();
    }

    private void OnAdd(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_url.Text)) return;
        _store.Add(_name.Text ?? "", _url.Text, _token.Text);
        _name.Text = _token.Text = "";
        _url.Text = InstanceStore.PublicMemexUrl;
        Refresh();
    }

    private void Refresh()
    {
        _list.Clear();
        if (_store.Instances.Count == 0)
        {
            _list.Add(new Label { Text = "No instances yet — add one below.", TextColor = Colors.Gray });
            return;
        }

        foreach (var instance in _store.Instances)
        {
            var inst = instance;

            var open = new Button
            {
                Text = inst.IsLocal ? $"{inst.Name}  (this device)" : inst.Name,
                HorizontalOptions = LayoutOptions.Fill,
            };
            open.Clicked += (_, _) => OnOpen?.Invoke(inst);

            var row = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) },
            };
            row.Add(open, 0);

            // The Local (in-process) mesh is the app's own — not removable; remote instances get a ✕.
            if (!inst.IsLocal)
            {
                // Authenticated remote instances can be imported FROM — pull nodes into the local mesh.
                if (inst.IsAuthenticated)
                {
                    var import = new Button { Text = "Import…" };
                    import.Clicked += (_, _) => OpenImport(inst);
                    row.Add(import, 1);
                }

                var del = new Button { Text = "✕", WidthRequest = 44 };
                del.Clicked += (_, _) => { _store.Remove(inst); Refresh(); };
                row.Add(del, 2);
            }

            _list.Add(row);
            _list.Add(new Label
            {
                Text = inst.IsLocal
                    ? "in-process · monolith mesh on SQLite (default)"
                    : inst.IsAuthenticated ? inst.Url : $"{inst.Url}  (not signed in)",
                FontSize = 11,
                TextColor = Colors.Gray
            });
        }
    }

    /// <summary>
    /// Opens the import view for an authenticated remote instance: ensures the SignalR connection is
    /// established (and the instance persisted) via the existing <see cref="MeshConnector"/>, then swaps
    /// this view's content to an <see cref="ImportFromInstanceView"/> whose "← Back" restores the manager.
    /// </summary>
    private void OpenImport(MemexInstance inst)
    {
        if (!inst.IsAuthenticated || string.IsNullOrEmpty(inst.Token)) return;
        _connector.Connect(inst.Url, inst.Token!, inst.Name);
        Content = new ImportFromInstanceView(_importer, inst, () => Content = _rootContent);
    }
}
