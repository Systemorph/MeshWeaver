using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Memex.Client.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;

namespace Memex.Client.Pages;

/// <summary>
/// The native home: a welcome banner, a horizontal tab strip (Threads · Spaces · My Items · Last Read ·
/// Last Edited), a catalog of node cards for the selected tab (with a "➕ New thread" empty state), and a
/// chat composer pinned to the bottom that starts a thread. Built natively over the REAL framework data
/// (<c>hub.GetQuery</c> / <c>hub.StartThread</c>) because the platform Activity area is CSS/reactive and its
/// catalog doesn't resolve in the native MAUI view pack.
/// </summary>
public sealed class ActivityDashboardView : ContentView
{
    private static readonly (string Name, string Query)[] Tabs =
    {
        ("Threads", "nodeType:Thread sort:LastModified-desc"),
        ("Spaces", "nodeType:Space sort:LastModified-desc"),
        ("My Items", "is:main sort:LastModified-desc"),
        ("Last Read", "sort:LastModified-desc"),
        ("Last Edited", "sort:LastModified-desc"),
    };

    private readonly IMessageHub _hub;
    private readonly HorizontalStackLayout _tabBar = new() { Spacing = 4, VerticalOptions = LayoutOptions.Center };
    private readonly NodeCardListView _cards;
    private readonly Entry _composer = new()
    {
        Placeholder = "Start a new thread…", ReturnType = ReturnType.Send,
        FontSize = 15, TextColor = Colors.White, VerticalOptions = LayoutOptions.Center,
    };
    private string _activeTab = Tabs[0].Name;

    /// <summary>Raised when a node card is tapped — the shell navigates to that node's area.</summary>
    public Action<MeshNode>? OnNodeSelected { get => _cards.OnNodeSelected; set => _cards.OnNodeSelected = value; }

    /// <summary>Raised when a thread is created (composer / "New thread") — the shell opens it.</summary>
    public Action<MeshNode>? OnThreadCreated { get; set; }

    public ActivityDashboardView(IMessageHub hub, string userName, string instanceName)
    {
        _hub = hub;
        _cards = new NodeCardListView(hub);

        var banner = new VerticalStackLayout
        {
            Spacing = 4,
            Padding = new Thickness(20, 16, 20, 8),
            Children =
            {
                new Label { Text = $"Welcome back, {userName}", FontSize = 24, FontAttributes = FontAttributes.Bold },
                new Label { Text = "💬 You can ask the agent to customize your home screen.", FontSize = 12, TextColor = Colors.Gray },
            },
        };

        foreach (var (name, _) in Tabs) _tabBar.Children.Add(TabButton(name));
        var tabScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            Padding = new Thickness(16, 0, 16, 6),
            Content = _tabBar,
        };

        _composer.Completed += (_, _) => SendComposer();
        var send = new Button { Text = "Send", FontSize = 14, BackgroundColor = Colors.RoyalBlue, TextColor = Colors.White, CornerRadius = 14, Padding = new Thickness(14, 0), HeightRequest = 34 };
        send.Clicked += (_, _) => SendComposer();
        var composerGrid = new Grid { ColumnSpacing = 6, ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        composerGrid.Add(_composer, 0);
        composerGrid.Add(send, 1);
        var composerBar = new Border
        {
            StrokeThickness = 1, Stroke = Color.FromArgb("#3A3A3C"),
            BackgroundColor = Color.FromArgb("#1C1C1E"),
            StrokeShape = new RoundRectangle { CornerRadius = 20 },
            Margin = new Thickness(16, 6, 16, 12), Padding = new Thickness(14, 3, 4, 3),
            Content = composerGrid,
        };

        var grid = new Grid
        {
            RowDefinitions =
            {
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto),
            },
        };
        grid.Add(banner, 0, 0);
        grid.Add(tabScroll, 0, 1);
        grid.Add(new ScrollView { Content = _cards, Padding = new Thickness(16, 0) }, 0, 2);
        grid.Add(composerBar, 0, 3);
        Content = grid;

        SelectTab(_activeTab);
    }

    private Button TabButton(string name)
    {
        var active = name == _activeTab;
        var btn = new Button
        {
            Text = name, FontSize = 14,
            FontAttributes = active ? FontAttributes.Bold : FontAttributes.None,
            TextColor = active ? Colors.RoyalBlue : Colors.Gray,
            BackgroundColor = Colors.Transparent, Padding = new Thickness(8, 4),
        };
        btn.Clicked += (_, _) => SelectTab(name);
        return btn;
    }

    private void SelectTab(string name)
    {
        _activeTab = name;
        _tabBar.Children.Clear();
        foreach (var (t, _) in Tabs) _tabBar.Children.Add(TabButton(t));

        // Threads gets a "➕ New thread" call-to-action when empty; other tabs just say they're empty.
        _cards.EmptyView = name == "Threads" ? BuildThreadsEmptyState : null;
        _cards.EmptyMessage = $"No {name.ToLowerInvariant()} yet.";
        _cards.SetQuery(Tabs.First(t => t.Name == name).Query);
    }

    private View BuildThreadsEmptyState()
    {
        var btn = new Button
        {
            Text = "➕ New thread", FontSize = 15, BackgroundColor = Colors.RoyalBlue, TextColor = Colors.White,
            CornerRadius = 8, Padding = new Thickness(16, 6), HorizontalOptions = LayoutOptions.Start,
        };
        // Open a thread immediately (visible effect); the user then types into it. Focusing the composer
        // alone reads as "no effect" on desktop (no soft keyboard), so create + open.
        btn.Clicked += (_, _) => _hub.StartThread(DeviceOnboarding.DeviceUserId, "New thread",
            onCreated: node => MainThread.BeginInvokeOnMainThread(() => OnThreadCreated?.Invoke(node)));
        return new VerticalStackLayout
        {
            Spacing = 10, Padding = new Thickness(4, 20),
            Children =
            {
                new Label { Text = "No threads yet — start one to get going.", TextColor = Colors.Gray },
                btn,
            },
        };
    }

    private void SendComposer()
    {
        var text = _composer.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        _composer.Text = "";
        // hub.StartThread is fire-and-forget (void); the thread node is created reactively and onCreated
        // fires with it. Open it, and refresh the Threads tab so it shows in the catalog.
        _hub.StartThread(DeviceOnboarding.DeviceUserId, text,
            onCreated: node => MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_activeTab == "Threads") SelectTab("Threads");
                OnThreadCreated?.Invoke(node);
            }));
    }
}
