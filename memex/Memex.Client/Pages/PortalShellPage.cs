using System.Collections.Immutable;
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
/// The app's responsive "full monty" portal shell — the native equivalent of the Fluent Blazor portal
/// (<c>PortalLayoutBase</c>). A single round-icon <b>title bar</b> carries navigation (🏠 · ← →), a real
/// <b>search</b> bar, and the provider-driven menus, then a collapsible <b>chat side-panel</b>.
///
/// <para>Every menu is <b>provider-driven</b> — the shell hardcodes no item list. The platform menus
/// (<b>Node</b> 🧊, <b>Mesh</b> ▦, <b>AI</b> ✨) come live from <c>hub.GetMenu(path, area, context)</c>
/// (re-emitting on permission change); the app menus (<b>Settings</b> ⚙, <b>User</b> 👤) come from
/// <see cref="IClientMenuProvider"/>s. All five render through the SAME DevExpress
/// native <c>DisplayActionSheet</c> mechanism (dependency-free, works fully offline), nested
/// <c>Children</c> opening a sub-sheet.</para>
///
/// <para><b>Responsive</b>: wide (≥ <see cref="NarrowThreshold"/> px) shows the menu buttons inline;
/// narrow collapses them behind a hamburger (☰) that opens a single drawer popup listing every context's
/// items — mirroring the Blazor desktop/mobile split. The content frame is history-driven via
/// <see cref="NavigationService"/> (the single source of truth for "where we are").</para>
/// </summary>
public sealed class PortalShellPage : ContentPage
{
    // The User node's default area (UserActivityLayoutAreas.ActivityArea) — home renders device-user/Activity.
    private const string UserActivityArea = "Activity";
    private const string NodeMenuContext = "Node";   // mesh-node menu (Edit/Files/Threads/Actions/…)
    private const string MeshMenuContext = "Mesh";    // mesh-level menu (Create/Import/Export)
    private const string AiMenuContext = "AI";        // AI menu (Threads/Models/Agents/Skills)
    private const string SettingsContext = "Settings";
    private const string UserContext = "User";
    private const string IconBg = "#2C2C2E";
    private const string SurfaceBg = "#2C2C2E";
    private const string BorderColor = "#3A3A3C";
    private const double NarrowThreshold = 820;       // < this width → menus collapse behind a hamburger

    // The ordered set of menu contexts + their round-button glyph. Node/Mesh/AI come from hub.GetMenu;
    // Settings/User come from the IClientMenuProviders. Adding a context here is the ONLY shell change a
    // new menu needs — the items themselves always come from a provider.
    private static readonly (string Context, string Glyph)[] MenuContexts =
    [
        (NodeMenuContext, "🧊"),
        (MeshMenuContext, "▦"),
        (AiMenuContext, "✨"),
        (SettingsContext, "⚙"),
        (UserContext, "👤"),
    ];

    private readonly IServiceProvider _services;
    private readonly IMessageHub _hub;
    private readonly NavigationService _nav;
    private readonly IPreferencesService _prefs;

    // App-level (client) menus, grouped by context — Settings, User. The platform Node/Mesh/AI menus
    // arrive reactively from hub.GetMenu instead (see RefreshMenu).
    private readonly ImmutableDictionary<string, IReadOnlyList<NodeMenuItemDefinition>> _clientMenus;

    private bool _started;
    private IDisposable? _zoomSub;

    private readonly Button _back = RoundIcon("←");
    private readonly Button _forward = RoundIcon("→");
    private readonly Entry _search = new()
    {
        Placeholder = "Search the mesh…", ReturnType = ReturnType.Search, FontSize = 14,
        TextColor = Colors.White, VerticalOptions = LayoutOptions.Center,
    };

    // The reflowing menu-button area (group buttons ↔ "☰" hamburger), rebuilt on every menu change / resize.
    private readonly HorizontalStackLayout _menuBar = new() { Spacing = 4, VerticalOptions = LayoutOptions.Center };
    private IDisposable? _nodeSub;
    private IDisposable? _meshSub;
    private IDisposable? _aiSub;
    private IReadOnlyList<NodeMenuItemDefinition> _nodeItems = [];
    private IReadOnlyList<NodeMenuItemDefinition> _meshItems = [];
    private IReadOnlyList<NodeMenuItemDefinition> _aiItems = [];
    private string? _currentNodePath;
    private string? _currentNodeTitle;

    private readonly ContentView _frame = new() { VerticalOptions = LayoutOptions.Fill, HorizontalOptions = LayoutOptions.Fill };
    private readonly Border _chatColumn;

    public PortalShellPage(
        IServiceProvider services, IMessageHub hub, NavigationService nav, IPreferencesService prefs,
        IEnumerable<IClientMenuProvider> clientMenus)
    {
        _services = services;
        _hub = hub;
        _nav = nav;
        _prefs = prefs;
        // One entry per context; multiple providers for the same context concatenate (sorted by Order).
        _clientMenus = clientMenus
            .GroupBy(p => p.Context)
            .ToImmutableDictionary(
                g => g.Key,
                g => (IReadOnlyList<NodeMenuItemDefinition>)g
                    .SelectMany(p => p.GetItems())
                    .OrderBy(i => i.Order)
                    .ToImmutableArray());
        Title = "Memex";

        // ── title bar: home · back · forward · search · [menus] · chat-toggle ───────────────────────────
        var home = RoundIcon("🏠");
        home.Clicked += (_, _) => NavigateHome();
        _back.Clicked += (_, _) => _nav.GoBack();
        _forward.Clicked += (_, _) => _nav.GoForward();
        _search.Completed += (_, _) => RunSearch();
        var chatToggle = RoundIcon("💬");
        chatToggle.Clicked += (_, _) => ToggleChat();

        var searchBox = new Border
        {
            StrokeThickness = 1,
            Stroke = Color.FromArgb(BorderColor),
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
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto),
            },
        };
        bar.Add(home, 0);
        bar.Add(_back, 1);
        bar.Add(_forward, 2);
        bar.Add(searchBox, 3);
        bar.Add(_menuBar, 4);
        bar.Add(chatToggle, 5);

        // ── chat side-panel (collapsible, hidden by default) ──────────────────────────────────────────
        _chatColumn = new Border
        {
            WidthRequest = 360,
            IsVisible = false,
            StrokeThickness = 1,
            Stroke = Color.FromArgb(BorderColor),
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
        root.Add(new BoxView { HeightRequest = 1, Color = Colors.Gray, Opacity = 0.25 }, 0, 1);
        root.Add(body, 0, 2);
        Content = root;

        // Re-flow the menu buttons (inline group buttons ↔ "☰" hamburger) on rotation / resize.
        SizeChanged += (_, _) => RenderMenu();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;
        // Zoom the CONTENT FRAME only — scaling the whole page (ApplyTo) pushes the right-side title-bar
        // menus off-screen. The content grows; the chrome stays full-width so every menu stays reachable.
        _zoomSub = _prefs.Resolved
            .Select(p => p.ZoomLevel)
            .DistinctUntilChanged()
            .Subscribe(zoom => MainThread.BeginInvokeOnMainThread(() =>
            {
                _frame.AnchorX = 0;
                _frame.AnchorY = 0;
                _frame.Scale = zoom;
            }));
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
        _currentNodeTitle = location.Title;
        RefreshMenu(location);
    }

    // ── platform menus (from hub.GetMenu — NOT hardcoded): Node + Mesh + AI contexts ───────────────────
    private void RefreshMenu(NavLocation entry)
    {
        _nodeSub?.Dispose(); _nodeSub = null;
        _meshSub?.Dispose(); _meshSub = null;
        _aiSub?.Dispose(); _aiSub = null;
        _nodeItems = [];
        _meshItems = [];
        _aiItems = [];
        _currentNodePath = entry.NodePath;
        if (entry.NodePath is not null)
        {
            var addr = (Address)entry.NodePath;
            var areaRef = new LayoutAreaReference(entry.Area);
            // Same reactive API the Blazor portal uses; each re-emits when the viewer's permissions change.
            _nodeSub = _hub.GetMenu(addr, areaRef, NodeMenuContext).Subscribe(
                items => MainThread.BeginInvokeOnMainThread(() => { _nodeItems = items; RenderMenu(); }), _ => { });
            _meshSub = _hub.GetMenu(addr, areaRef, MeshMenuContext).Subscribe(
                items => MainThread.BeginInvokeOnMainThread(() => { _meshItems = items; RenderMenu(); }), _ => { });
            _aiSub = _hub.GetMenu(addr, areaRef, AiMenuContext).Subscribe(
                items => MainThread.BeginInvokeOnMainThread(() => { _aiItems = items; RenderMenu(); }), _ => { });
        }
        RenderMenu();
    }

    /// <summary>The items for a context: Node/Mesh/AI from the live hub menu, Settings/User from providers.</summary>
    private IReadOnlyList<NodeMenuItemDefinition> ItemsFor(string context) => context switch
    {
        NodeMenuContext => _nodeItems,
        MeshMenuContext => _meshItems,
        AiMenuContext => _aiItems,
        _ => _clientMenus.TryGetValue(context, out var items) ? items : [],
    };

    private void RenderMenu()
    {
        _menuBar.Children.Clear();
        var narrow = Width > 0 && Width < NarrowThreshold;
        if (narrow)
        {
            // Narrow: everything (Node/Mesh/AI/Settings/User) behind one "☰" drawer — the mobile split.
            var hamburger = RoundIcon("☰");
            hamburger.Clicked += async (_, _) => await ShowDrawerSheetAsync();
            _menuBar.Children.Add(hamburger);
            return;
        }
        // Wide: one round button per context that currently has items; tap opens its native action sheet.
        foreach (var (context, glyph) in MenuContexts)
        {
            var items = ItemsFor(context);
            if (items.Count == 0) continue;
            var button = RoundIcon(glyph);
            var ctx = context;
            var ctxItems = items;
            button.Clicked += async (_, _) => await ShowContextSheetAsync(ctx, ctxItems);
            _menuBar.Children.Add(button);
        }
    }

    // ── native menus (DisplayActionSheet — no dependency, works fully offline) ────────────────────────────

    /// <summary>One context's menu as a native action sheet of its items.</summary>
    private async Task ShowContextSheetAsync(string title, IReadOnlyList<NodeMenuItemDefinition> items)
    {
        if (items.Count == 0) return;
        var labels = items.Select(LabelFor).ToArray();
        var pick = await DisplayActionSheet(title, "Cancel", null, labels);
        var chosen = items.FirstOrDefault(i => LabelFor(i) == pick);
        if (chosen is not null) await InvokeItemAsync(chosen);
    }

    /// <summary>Narrow-mode drawer: pick a context, then its items.</summary>
    private async Task ShowDrawerSheetAsync()
    {
        var contexts = MenuContexts.Where(c => ItemsFor(c.Context).Count > 0).ToArray();
        if (contexts.Length == 0) return;
        var labels = contexts.Select(c => $"{c.Glyph}  {c.Context}").ToArray();
        var pick = await DisplayActionSheet("Menu", "Cancel", null, labels);
        var chosen = contexts.FirstOrDefault(c => $"{c.Glyph}  {c.Context}" == pick);
        if (chosen.Context is not null)
            await ShowContextSheetAsync(chosen.Context, ItemsFor(chosen.Context));
    }

    /// <summary>Leaf item → navigate; item with <c>Children</c> → a nested action sheet.</summary>
    private async Task InvokeItemAsync(NodeMenuItemDefinition item)
    {
        if (item.Children is { Count: > 0 } children)
        {
            var labels = children.Select(LabelFor).ToArray();
            var pick = await DisplayActionSheet(item.Label, "Cancel", null, labels);
            var chosen = children.FirstOrDefault(c => LabelFor(c) == pick);
            if (chosen is not null) await InvokeItemAsync(chosen);
            return;
        }
        InvokeItem(item);
    }

    /// <summary>Prefix the label with an emoji/text glyph; skip server SVG-path icons (can't load offline).</summary>
    private static string LabelFor(NodeMenuItemDefinition item) =>
        string.IsNullOrEmpty(item.Icon)
        || item.Icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        || item.Icon.StartsWith('/')
            ? item.Label
            : $"{item.Icon}  {item.Label}";

    // ── action mapping (Children handled in BuildRow; this fires for leaf items only) ───────────────────
    private void InvokeItem(NodeMenuItemDefinition item)
    {
        // Client-only destinations (not a mesh node area) → native view/page.
        if (item.Area is { } area && area.StartsWith(ClientDestinations.Prefix, StringComparison.Ordinal))
        {
            OpenClientDestination(area);
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

    private void OpenClientDestination(string area)
    {
        switch (area)
        {
            case ClientDestinations.Settings:
                OpenSettings();
                break;
            case ClientDestinations.Voice:
                Navigate("Voice", () => _services.GetRequiredService<VoiceView>());
                break;
            case ClientDestinations.Instances:
                Navigate("Instances", BuildInstanceManager);
                break;
            case ClientDestinations.Profile:
                NavigateToNode(DeviceOnboarding.DeviceUserId, "Profile", "Overview");
                break;
        }
    }

    private async void OpenSettings()
    {
        var settings = _services.GetRequiredService<SettingsPage>();
        if (settings.ToolbarItems.Count == 0)
            settings.ToolbarItems.Add(new ToolbarItem("Done", null, async () => await Navigation.PopModalAsync()));
        await Navigation.PushModalAsync(new NavigationPage(settings));
    }

    // ── chat side-panel toggle (collapsible, animated) ─────────────────────────────────────────────────
    private async void ToggleChat()
    {
        if (_chatColumn.IsVisible)
        {
            await _chatColumn.FadeToAsync(0, 120);
            _chatColumn.IsVisible = false;
        }
        else
        {
            _chatColumn.Opacity = 0;
            _chatColumn.IsVisible = true;
            await _chatColumn.FadeToAsync(1, 160);
        }
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
        view.OnOpen = _ => NavigateHome();
        return view;
    }
}
