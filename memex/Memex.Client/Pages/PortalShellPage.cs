using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Memex.Client.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;

namespace Memex.Client.Pages;

/// <summary>
/// The app's portal shell — the native equivalent of the Blazor portal layout. A single <b>top bar</b>
/// carries navigation (🏠 · ← → · instance switcher), a real <b>search bar</b> (mesh <c>hub.GetQuery</c>),
/// the current node's <b>platform menu</b> (pulled live from <c>hub.GetMenu</c> — NOT hardcoded — shown as
/// buttons in landscape, collapsed into a "⋯" overflow in portrait), a chat toggle, and an account menu.
/// The <b>content frame</b> is history-driven (browser-style back/forward); a collapsible <b>chat panel</b>
/// hosts the on-device chat. There is no hardcoded side nav: destinations come from the platform (node
/// menu items, search results, the user's own node area) — the shell only provides the chrome.
/// </summary>
public sealed class PortalShellPage : ContentPage
{
    // The User node's default area (UserActivityLayoutAreas.ActivityArea) — home renders device-user/Activity.
    private const string UserActivityArea = "Activity";

    private readonly IServiceProvider _services;
    private readonly InstanceStore _store;
    private readonly IMessageHub _hub;
    private readonly NavigationService _nav;

    private bool _started;

    private readonly Button _back = NavButton("←", 18);
    private readonly Button _forward = NavButton("→", 18);
    private readonly Button _instance = new() { FontSize = 15, FontAttributes = FontAttributes.Bold, BackgroundColor = Colors.Transparent };
    private readonly Entry _search = new() { Placeholder = "Search the mesh…", ReturnType = ReturnType.Search, FontSize = 14, VerticalOptions = LayoutOptions.Center };

    // The current node's platform menu (from hub.GetMenu) — buttons in landscape, "⋯" overflow in portrait.
    private readonly HorizontalStackLayout _menuBar = new() { Spacing = 2, VerticalOptions = LayoutOptions.Center };
    private readonly Button _menuOverflow = NavButton("⋯", 20);
    private IDisposable? _menuSub;
    private IReadOnlyList<NodeMenuItemDefinition> _menuItems = [];
    private string? _currentNodePath;

    private readonly ContentView _frame = new() { VerticalOptions = LayoutOptions.Fill, HorizontalOptions = LayoutOptions.Fill };
    private Border _chatColumn = null!;
    private MemexInstance? _current;

    public PortalShellPage(IServiceProvider services, InstanceStore store, IMessageHub hub, NavigationService nav)
    {
        _services = services;
        _store = store;
        _hub = hub;
        _nav = nav;
        _current = store.Instances.FirstOrDefault();
        Title = "Memex";

        // ── top bar ──────────────────────────────────────────────────────────────────────────────────
        var home = NavButton("🏠", 16);
        home.Clicked += (_, _) => NavigateHome();
        _back.Clicked += (_, _) => _nav.GoBack();
        _forward.Clicked += (_, _) => _nav.GoForward();
        _instance.Clicked += async (_, _) => await ShowInstanceSwitcherAsync();
        _search.Completed += (_, _) => RunSearch();
        _menuOverflow.Clicked += async (_, _) => await ShowMenuSheetAsync();
        var chatToggle = NavButton("💬", 16);
        chatToggle.Clicked += (_, _) => _chatColumn.IsVisible = !_chatColumn.IsVisible;
        var account = NavButton("👤", 16);
        account.Clicked += async (_, _) => await ShowAccountSheetAsync();

        var searchBox = new Border
        {
            StrokeThickness = 1,
            Stroke = Color.FromArgb("#3A3A3C"),
            BackgroundColor = Color.FromArgb("#1C1C1E"),
            Padding = new Thickness(10, 0),
            Margin = new Thickness(4, 0),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = _search,
            HorizontalOptions = LayoutOptions.Fill,
        };

        var bar = new Grid
        {
            Padding = new Thickness(6, 4),
            ColumnSpacing = 2,
            ColumnDefinitions =
            {
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
            },
        };
        bar.Add(home, 0);
        bar.Add(_back, 1);
        bar.Add(_forward, 2);
        bar.Add(_instance, 3);
        bar.Add(searchBox, 4);
        bar.Add(_menuBar, 5);
        bar.Add(_menuOverflow, 6);
        bar.Add(chatToggle, 7);
        bar.Add(account, 8);

        // ── chat side-panel (collapsible, hidden by default) ──────────────────────────────────────────
        _chatColumn = new Border
        {
            WidthRequest = 360,
            IsVisible = false,
            StrokeThickness = 1,
            Stroke = Color.FromArgb("#3A3A3C"),
            BackgroundColor = Color.FromArgb("#1C1C1E"),
            Content = _services.GetRequiredService<ChatView>(),
        };

        // ── body: [content][chat] ─────────────────────────────────────────────────────────────────────
        var body = new Grid
        {
            ColumnSpacing = 0,
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
        };
        body.Add(_frame, 0);
        body.Add(_chatColumn, 1);

        var root = new Grid { RowSpacing = 0, RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) } };
        root.Add(bar, 0, 0);
        var sep = new BoxView { HeightRequest = 1, Color = Colors.Gray, Opacity = 0.25 };
        root.Add(sep, 0, 1);
        root.Add(body, 0, 2);
        Content = root;

        // Re-flow the platform menu (buttons ↔ overflow) on rotation / resize.
        SizeChanged += (_, _) => RenderMenu();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;
        // The shell renders whatever the navigation service says is "where we are", and reloads the menu.
        _nav.Current.Subscribe(loc => MainThread.BeginInvokeOnMainThread(() => Render(loc)));
        NavigateHome();
    }

    private static Button NavButton(string text, double size) =>
        new() { Text = text, FontSize = size, WidthRequest = 40, BackgroundColor = Colors.Transparent };

    // ── navigation history (the content area) ─────────────────────────────────────────────────────────
    private void Navigate(string title, Func<View> build, string? nodePath = null, string area = "Overview")
        => _nav.Navigate(new NavLocation(title, nodePath, area, build));

    private void Render(NavLocation? location)
    {
        if (location is null) return;
        _frame.Content = location.Build();
        _back.IsEnabled = _nav.CanGoBack;
        _forward.IsEnabled = _nav.CanGoForward;
        _back.Opacity = _back.IsEnabled ? 1 : 0.35;
        _forward.Opacity = _forward.IsEnabled ? 1 : 0.35;
        _instance.Text = $"{_current?.Name ?? "Local"} ▾";
        RefreshMenu(location);
    }

    // ── platform node menu (from hub.GetMenu — NOT hardcoded) ─────────────────────────────────────────
    private void RefreshMenu(NavLocation entry)
    {
        _menuSub?.Dispose();
        _menuSub = null;
        _menuItems = [];
        _currentNodePath = entry.NodePath;
        if (entry.NodePath is null)
        {
            RenderMenu();
            return;
        }
        // Same reactive API the Blazor portal uses; re-emits when the viewer's permissions change.
        _menuSub = _hub.GetMenu((Address)entry.NodePath, new LayoutAreaReference(entry.Area), "Node")
            .Subscribe(
                items => MainThread.BeginInvokeOnMainThread(() => { _menuItems = items; RenderMenu(); }),
                _ => { });
    }

    private void RenderMenu()
    {
        _menuBar.Children.Clear();
        var narrow = Width > 0 && Width < 760;
        var hasItems = _menuItems.Count > 0;
        _menuOverflow.IsVisible = hasItems && narrow;
        _menuBar.IsVisible = hasItems && !narrow;
        if (!hasItems || narrow)
            return;
        foreach (var item in _menuItems)
            _menuBar.Children.Add(MenuButton(item));
    }

    private Button MenuButton(NodeMenuItemDefinition item)
    {
        var label = string.IsNullOrEmpty(item.Icon) ? item.Label : $"{item.Icon} {item.Label}";
        var btn = new Button { Text = label, FontSize = 13, BackgroundColor = Colors.Transparent, Padding = new Thickness(8, 4) };
        btn.Clicked += async (_, _) => await InvokeMenuItemAsync(item);
        return btn;
    }

    private async Task ShowMenuSheetAsync()
    {
        if (_menuItems.Count == 0) return;
        var labels = _menuItems.Select(i => i.Label).ToArray();
        var pick = await DisplayActionSheet("Menu", "Cancel", null, labels);
        var chosen = _menuItems.FirstOrDefault(i => i.Label == pick);
        if (chosen is not null) await InvokeMenuItemAsync(chosen);
    }

    private async Task InvokeMenuItemAsync(NodeMenuItemDefinition item)
    {
        if (item.Children is { Count: > 0 } children)
        {
            var labels = children.Select(c => c.Label).ToArray();
            var pick = await DisplayActionSheet(item.Label, "Cancel", null, labels);
            var chosen = children.FirstOrDefault(c => c.Label == pick);
            if (chosen is not null) await InvokeMenuItemAsync(chosen);
            return;
        }
        // Href → cross-node navigation; otherwise append the item's Area to the current node path.
        if (!string.IsNullOrEmpty(item.Href))
        {
            NavigateToNode(item.Href, item.Label, "Overview");
            return;
        }
        if (_currentNodePath is not null)
            NavigateToNode(_currentNodePath, item.Label, item.Area);
    }

    // ── destinations ────────────────────────────────────────────────────────────────────────────────
    // Home = the user's own Activity area, served by the platform at the user node (device-user/Activity) —
    // a real node area, so the platform node menu loads in the top bar. Not a hand-rolled dashboard.
    private void NavigateHome()
        => NavigateToNode(DeviceOnboarding.DeviceUserId, "Home", UserActivityArea);

    private void NavigateToNode(string nodePath, string title, string area)
        => Navigate(title, () => new NodeAreaView(_hub, nodePath, area), nodePath, area);

    private void RunSearch()
    {
        var query = _search.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return;
        Navigate($"Search: {query}",
            () => new SearchResultsView(_hub, query,
                node => NavigateToNode(node.Path, node.Name ?? node.Path, "Overview")));
    }

    private View BuildInstanceManager()
    {
        var view = _services.GetRequiredService<InstanceManagerView>();
        view.OnOpen = inst => { _current = inst; NavigateHome(); };
        return view;
    }

    private async Task ShowInstanceSwitcherAsync()
    {
        var names = _store.Instances.Select(i => i.Name).ToArray();
        const string connect = "➕ Connect new…";
        var pick = await DisplayActionSheet("Instances", "Cancel", null, names.Append(connect).ToArray());
        if (pick is null or "Cancel") return;
        if (pick == connect) { Navigate("Instances", BuildInstanceManager); return; }

        var chosen = _store.Instances.FirstOrDefault(i => i.Name == pick);
        if (chosen is not null) { _current = chosen; NavigateHome(); }
    }

    private async Task ShowAccountSheetAsync()
    {
        const string profile = "👤 My profile";
        const string voice = "🎙 Voice";
        const string instances = "🧩 Manage instances";
        var pick = await DisplayActionSheet("Account", "Cancel", null, profile, voice, instances);
        switch (pick)
        {
            case profile:
                NavigateToNode(DeviceOnboarding.DeviceUserId, "Profile", "Overview");
                break;
            case voice:
                Navigate("Voice", () => _services.GetRequiredService<VoiceView>());
                break;
            case instances:
                Navigate("Instances", BuildInstanceManager);
                break;
        }
    }
}
