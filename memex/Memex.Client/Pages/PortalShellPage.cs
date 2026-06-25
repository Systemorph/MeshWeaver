using System.Collections.Immutable;
using System.Reactive.Linq;
using DevExpress.Maui.Controls;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Memex.Client.Prefs;
using Memex.Client.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;
using Placement = DevExpress.Maui.Core.Placement;

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
/// <see cref="DXPopup"/> mechanism — a native popup anchored to each round button, its rows tappable
/// (icon + label), nested <c>Children</c> expanding inline.</para>
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
    private DXPopup? _openPopup;   // the currently-open menu/drawer popup (closed before opening another)

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
            hamburger.Clicked += (_, _) => ShowDrawer(hamburger);
            _menuBar.Children.Add(hamburger);
            return;
        }
        // Wide: one round button per context that currently has items; tap opens its native popup.
        foreach (var (context, glyph) in MenuContexts)
        {
            var items = ItemsFor(context);
            if (items.Count == 0) continue;
            var button = RoundIcon(glyph);
            // Node/Mesh popups show the current node name as a header (mirrors the Blazor portal).
            var header = context is NodeMenuContext or MeshMenuContext ? _currentNodeTitle : null;
            button.Clicked += (_, _) => ShowContextPopup(button, header, items);
            _menuBar.Children.Add(button);
        }
    }

    // ── native DevExpress popups ───────────────────────────────────────────────────────────────────────

    /// <summary>A fresh menu popup: a floating, dismiss-on-outside-tap card placed below its anchor.</summary>
    private static DXPopup NewPopup() => new()
    {
        Placement = Placement.Bottom,
        AllowScrim = true,
        CloseOnScrimTap = true,
        AllowShadow = true,
        BackgroundColor = Colors.Transparent,   // the rounded inner Border paints the card surface
    };

    /// <summary>One context's menu: an anchored popup whose rows are the provider/platform items.</summary>
    private void ShowContextPopup(View anchor, string? header, IReadOnlyList<NodeMenuItemDefinition> items)
    {
        if (items.Count == 0) return;
        _openPopup?.Close();
        var popup = NewPopup();
        var stack = new VerticalStackLayout();
        if (!string.IsNullOrEmpty(header))
        {
            stack.Children.Add(SectionHeader(header!));
            stack.Children.Add(Divider());
        }
        foreach (var item in items)
            stack.Children.Add(BuildRow(item, popup, depth: 0));
        popup.Content = Surface(stack, width: 270);
        _openPopup = popup;
        popup.Show(anchor);
    }

    /// <summary>The narrow-mode drawer: a single popup listing EVERY context's items under section headers.</summary>
    private void ShowDrawer(View anchor)
    {
        _openPopup?.Close();
        var popup = NewPopup();
        var stack = new VerticalStackLayout();
        var first = true;
        foreach (var (context, glyph) in MenuContexts)
        {
            var items = ItemsFor(context);
            if (items.Count == 0) continue;
            if (!first) stack.Children.Add(Divider());
            first = false;
            stack.Children.Add(SectionHeader($"{glyph}  {context}"));
            foreach (var item in items)
                stack.Children.Add(BuildRow(item, popup, depth: 0));
        }
        popup.Content = Surface(stack, width: 300);
        _openPopup = popup;
        popup.Show(anchor);
    }

    /// <summary>Builds a tappable menu row; rows with <c>Children</c> toggle an inline (indented) expander.</summary>
    private View BuildRow(NodeMenuItemDefinition item, DXPopup popup, int depth)
    {
        if (item.Area == "_separator")
            return Divider();

        var row = new Grid
        {
            Padding = new Thickness(12 + depth * 16, 10, 12, 10),
            ColumnSpacing = 8,
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) },
        };
        var icon = BuildIcon(item.Icon);
        if (icon is not null) row.Add(icon, 0);
        row.Add(new Label { Text = item.Label, TextColor = Colors.White, FontSize = 15, VerticalOptions = LayoutOptions.Center }, 1);

        var hasChildren = item.Children is { Count: > 0 };
        var container = new VerticalStackLayout { Children = { row } };

        if (hasChildren)
        {
            var chevron = new Label { Text = "›", TextColor = Colors.Gray, FontSize = 18, VerticalOptions = LayoutOptions.Center };
            row.Add(chevron, 2);
            var children = new VerticalStackLayout { IsVisible = false };
            foreach (var child in item.Children!)
                children.Children.Add(BuildRow(child, popup, depth + 1));
            container.Children.Add(children);

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                children.IsVisible = !children.IsVisible;
                chevron.Text = children.IsVisible ? "⌄" : "›";
            };
            row.GestureRecognizers.Add(tap);
        }
        else
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => { popup.Close(); InvokeItem(item); };
            row.GestureRecognizers.Add(tap);
        }
        return container;
    }

    private static View? BuildIcon(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return null;
        // 🚨 URL / absolute-path icons (e.g. /static/NodeTypeIcons/*.svg) are SERVER assets — the local
        // client has no HTTP host, so loading them as a network Image throws ("not connected to internet")
        // and crashes the popup. Skip them; the label carries the meaning. Only emoji / short text glyphs
        // render (they need no network).
        if (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase) || icon.StartsWith('/'))
            return null;
        return new Label { Text = icon, FontSize = 15, VerticalOptions = LayoutOptions.Center };
    }

    /// <summary>The opaque, rounded card the popup paints — wraps the scrollable rows.</summary>
    private static Border Surface(View content, double width) => new()
    {
        BackgroundColor = Color.FromArgb(SurfaceBg),
        Stroke = Color.FromArgb(BorderColor),
        StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = 12 },
        WidthRequest = width,
        Content = new ScrollView { MaximumHeightRequest = 480, Content = content },
    };

    private static Label SectionHeader(string text) => new()
    {
        Text = text, FontSize = 12, FontAttributes = FontAttributes.Bold,
        TextColor = Colors.Gray, Padding = new Thickness(12, 10, 12, 4),
        LineBreakMode = LineBreakMode.TailTruncation,
    };

    private static BoxView Divider() => new() { HeightRequest = 1, Color = Color.FromArgb(BorderColor), Margin = new Thickness(8, 4) };

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
