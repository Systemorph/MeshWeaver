using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Memex.Client.Prefs;
using Memex.Client.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;

namespace Memex.Client.Pages;

/// <summary>
/// The app's portal shell — the native equivalent of the Blazor portal layout. A single <b>top bar</b>,
/// built from one consistent round-icon set, carries navigation (🏠 · ← →), the instance switcher, a real
/// <b>search bar</b> (mesh <c>hub.GetQuery</c>), the current node's <b>platform menus</b> pulled live from
/// <c>hub.GetMenu</c> — the mesh-node menu (☰) and the AI menu (✨) as top-level groups whose entries open
/// as sub-items — plus app <b>Settings</b> (⚙), a chat toggle (💬) and an account menu (👤). In portrait the
/// menu groups collapse into a single "⋯" overflow. The <b>content frame</b> is history-driven via
/// <see cref="NavigationService"/> (the single source of truth for "where we are"); a collapsible chat
/// panel hosts the on-device chat. No hardcoded side nav — destinations come from the platform.
/// </summary>
public sealed class PortalShellPage : ContentPage
{
    // The User node's default area (UserActivityLayoutAreas.ActivityArea) — home renders device-user/Activity.
    private const string UserActivityArea = "Activity";
    private const string NodeMenuContext = "Node";   // mesh-node menu (Edit/Files/Threads/Actions/…)
    private const string AiMenuContext = "AI";        // AI menu
    private const string IconBg = "#2C2C2E";

    private readonly IServiceProvider _services;
    private readonly InstanceStore _store;
    private readonly IMessageHub _hub;
    private readonly NavigationService _nav;
    private readonly IPreferencesService _prefs;

    private bool _started;
    private IDisposable? _zoomSub;

    private readonly Button _back = RoundIcon("←");
    private readonly Button _forward = RoundIcon("→");
    private readonly Button _instance = new()
    {
        FontSize = 14, FontAttributes = FontAttributes.Bold, CornerRadius = 16, HeightRequest = 34,
        Padding = new Thickness(14, 0), BackgroundColor = Color.FromArgb(IconBg), TextColor = Colors.White,
        VerticalOptions = LayoutOptions.Center,
    };
    private readonly Entry _search = new()
    {
        Placeholder = "Search the mesh…", ReturnType = ReturnType.Search, FontSize = 14,
        TextColor = Colors.White, VerticalOptions = LayoutOptions.Center,
    };

    // The platform menus for the current location (from hub.GetMenu): node + AI contexts.
    private readonly HorizontalStackLayout _menuBar = new() { Spacing = 4, VerticalOptions = LayoutOptions.Center };
    private IDisposable? _nodeSub;
    private IDisposable? _aiSub;
    private IReadOnlyList<NodeMenuItemDefinition> _nodeItems = [];
    private IReadOnlyList<NodeMenuItemDefinition> _aiItems = [];
    private string? _currentNodePath;

    private readonly ContentView _frame = new() { VerticalOptions = LayoutOptions.Fill, HorizontalOptions = LayoutOptions.Fill };
    private Border _chatColumn = null!;
    private MemexInstance? _current;

    public PortalShellPage(IServiceProvider services, InstanceStore store, IMessageHub hub, NavigationService nav, IPreferencesService prefs)
    {
        _services = services;
        _store = store;
        _hub = hub;
        _nav = nav;
        _prefs = prefs;
        _current = store.Instances.FirstOrDefault();
        Title = "Memex";

        // ── top bar (one consistent round-icon set) ────────────────────────────────────────────────────
        var home = RoundIcon("🏠");
        home.Clicked += (_, _) => NavigateHome();
        _back.Clicked += (_, _) => _nav.GoBack();
        _forward.Clicked += (_, _) => _nav.GoForward();
        _instance.Clicked += async (_, _) => await ShowInstanceSwitcherAsync();
        _search.Completed += (_, _) => RunSearch();
        var chatToggle = RoundIcon("💬");
        chatToggle.Clicked += (_, _) => _chatColumn.IsVisible = !_chatColumn.IsVisible;
        var account = RoundIcon("👤");
        account.Clicked += async (_, _) => await ShowAccountSheetAsync();

        var searchBox = new Border
        {
            StrokeThickness = 1,
            Stroke = Color.FromArgb("#3A3A3C"),
            BackgroundColor = Color.FromArgb("#1C1C1E"),
            Padding = new Thickness(12, 0),
            Margin = new Thickness(6, 0),
            StrokeShape = new RoundRectangle { CornerRadius = 17 },
            HeightRequest = 34,
            Content = _search,
            HorizontalOptions = LayoutOptions.Fill,
        };

        var bar = new Grid
        {
            Padding = new Thickness(8, 6),
            ColumnSpacing = 4,
            ColumnDefinitions =
            {
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
            },
        };
        bar.Add(home, 0);
        bar.Add(_back, 1);
        bar.Add(_forward, 2);
        bar.Add(_instance, 3);
        bar.Add(searchBox, 4);
        bar.Add(_menuBar, 5);
        bar.Add(chatToggle, 6);
        bar.Add(account, 7);

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

        // Re-flow the platform menus (group buttons ↔ "⋯" overflow) on rotation / resize.
        SizeChanged += (_, _) => RenderMenu();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;
        // Apply the resolved UI zoom across the whole app (Device→User→Space→System cascade).
        _zoomSub = _prefs.ApplyTo(this);
        // The shell renders whatever the navigation service says is "where we are", and reloads the menus.
        _nav.Current.Subscribe(loc => MainThread.BeginInvokeOnMainThread(() => Render(loc)));
        NavigateHome();
    }

    /// <summary>One consistent round icon button — the shell's single icon style, repeated everywhere.</summary>
    private static Button RoundIcon(string glyph, double size = 38, double font = 17) => new()
    {
        Text = glyph, FontSize = font,
        WidthRequest = size, HeightRequest = size, CornerRadius = (int)(size / 2),
        Padding = 0, BackgroundColor = Color.FromArgb(IconBg), TextColor = Colors.White,
        VerticalOptions = LayoutOptions.Center,
    };

    // ── navigation (delegates to the NavigationService — the source of truth for "where we are") ────────
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

    // ── platform menus (from hub.GetMenu — NOT hardcoded): node + AI contexts ──────────────────────────
    private void RefreshMenu(NavLocation entry)
    {
        _nodeSub?.Dispose(); _nodeSub = null;
        _aiSub?.Dispose(); _aiSub = null;
        _nodeItems = [];
        _aiItems = [];
        _currentNodePath = entry.NodePath;
        if (entry.NodePath is not null)
        {
            var addr = (Address)entry.NodePath;
            var areaRef = new LayoutAreaReference(entry.Area);
            // Same reactive API the Blazor portal uses; each re-emits when the viewer's permissions change.
            _nodeSub = _hub.GetMenu(addr, areaRef, NodeMenuContext).Subscribe(
                items => MainThread.BeginInvokeOnMainThread(() => { _nodeItems = items; RenderMenu(); }), _ => { });
            _aiSub = _hub.GetMenu(addr, areaRef, AiMenuContext).Subscribe(
                items => MainThread.BeginInvokeOnMainThread(() => { _aiItems = items; RenderMenu(); }), _ => { });
        }
        RenderMenu();
    }

    private void RenderMenu()
    {
        _menuBar.Children.Clear();
        var narrow = Width > 0 && Width < 820;
        if (narrow)
        {
            // Portrait / narrow: everything (node menu, AI menu, Settings) behind one "⋯" overflow.
            var overflow = RoundIcon("⋯");
            overflow.Clicked += async (_, _) => await ShowOverflowSheetAsync();
            _menuBar.Children.Add(overflow);
            return;
        }
        // Landscape: the mesh-node menu (☰) and AI menu (✨) as top-level groups, plus Settings (⚙).
        if (_nodeItems.Count > 0)
        {
            var node = RoundIcon("☰");
            node.Clicked += async (_, _) => await ShowGroupSheetAsync("Menu", _nodeItems);
            _menuBar.Children.Add(node);
        }
        if (_aiItems.Count > 0)
        {
            var ai = RoundIcon("✨");
            ai.Clicked += async (_, _) => await ShowGroupSheetAsync("AI", _aiItems);
            _menuBar.Children.Add(ai);
        }
        var settings = RoundIcon("⚙");
        settings.Clicked += async (_, _) => await OpenSettingsAsync();
        _menuBar.Children.Add(settings);
    }

    private async Task ShowOverflowSheetAsync()
    {
        const string menu = "☰ Menu", ai = "✨ AI", settings = "⚙ Settings";
        var options = new[] { _nodeItems.Count > 0 ? menu : null, _aiItems.Count > 0 ? ai : null, settings }
            .Where(o => o is not null).Cast<string>().ToArray();
        var pick = await DisplayActionSheet("More", "Cancel", null, options);
        switch (pick)
        {
            case menu: await ShowGroupSheetAsync("Menu", _nodeItems); break;
            case ai: await ShowGroupSheetAsync("AI", _aiItems); break;
            case settings: await OpenSettingsAsync(); break;
        }
    }

    private async Task ShowGroupSheetAsync(string title, IReadOnlyList<NodeMenuItemDefinition> items)
    {
        if (items.Count == 0) return;
        var labels = items.Select(i => i.Label).ToArray();
        var pick = await DisplayActionSheet(title, "Cancel", null, labels);
        var chosen = items.FirstOrDefault(i => i.Label == pick);
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

    private async Task OpenSettingsAsync()
    {
        var settings = _services.GetRequiredService<SettingsPage>();
        if (settings.ToolbarItems.Count == 0)
            settings.ToolbarItems.Add(new ToolbarItem("Done", null, async () => await Navigation.PopModalAsync()));
        await Navigation.PushModalAsync(new NavigationPage(settings));
    }

    // ── destinations ────────────────────────────────────────────────────────────────────────────────
    // Home = the user's own Activity area (device-user/Activity, the User node's default area) — a real
    // node area, so the platform node menu loads in the top bar. Not a hand-rolled dashboard.
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
