using MeshWeaver.Messaging;
using Memex.Client.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Shapes;

namespace Memex.Client.Pages;

/// <summary>
/// The app's portal shell — the native equivalent of the Blazor portal layout, with three regions:
/// a <b>top bar</b> (nav toggle · ← → · instance switcher · chat toggle), a <b>left nav menu</b>
/// (Home / Profile / Voice / Instances), a <b>main content area</b> (history-driven, back/forward like a
/// browser), and a <b>collapsible chat side-panel</b>. The content frame hosts the dashboard, node areas
/// (rendered via the MAUI view pack), and the instance manager; the chat panel hosts the on-device chat.
/// </summary>
public sealed class PortalShellPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly InstanceStore _store;
    private readonly IMessageHub _hub;

    private readonly List<(string Title, Func<View> Build)> _history = new();
    private int _index = -1;
    private bool _started;

    private readonly Button _back = NavButton("←", 18);
    private readonly Button _forward = NavButton("→", 18);
    private readonly Button _instance = new() { FontSize = 15, FontAttributes = FontAttributes.Bold, BackgroundColor = Colors.Transparent };
    private readonly Label _crumb = new() { FontSize = 13, TextColor = Colors.Gray, VerticalOptions = LayoutOptions.Center };

    private readonly ContentView _frame = new() { VerticalOptions = LayoutOptions.Fill, HorizontalOptions = LayoutOptions.Fill };
    private readonly VerticalStackLayout _navMenu = new() { Spacing = 2, Padding = new Thickness(8, 12) };
    private Border _navColumn = null!;
    private Border _chatColumn = null!;
    private MemexInstance? _current;

    public PortalShellPage(IServiceProvider services, InstanceStore store, IMessageHub hub)
    {
        _services = services;
        _store = store;
        _hub = hub;
        _current = store.Instances.FirstOrDefault();
        Title = "Memex";

        // ── top bar ──────────────────────────────────────────────────────────────────────────────────
        var navToggle = NavButton("☰", 18);
        navToggle.Clicked += (_, _) => _navColumn.IsVisible = !_navColumn.IsVisible;
        _back.Clicked += (_, _) => GoBack();
        _forward.Clicked += (_, _) => GoForward();
        _instance.Clicked += async (_, _) => await ShowInstanceSwitcherAsync();
        var chatToggle = NavButton("💬", 16);
        chatToggle.Clicked += (_, _) => _chatColumn.IsVisible = !_chatColumn.IsVisible;

        var bar = new Grid
        {
            Padding = new Thickness(6, 4),
            ColumnSpacing = 2,
            ColumnDefinitions =
            {
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto),
            },
        };
        bar.Add(navToggle, 0);
        bar.Add(_back, 1);
        bar.Add(_forward, 2);
        bar.Add(_instance, 3);
        bar.Add(_crumb, 4);
        bar.Add(chatToggle, 5);

        // ── left nav menu ────────────────────────────────────────────────────────────────────────────
        AddNavItem("🏠", "Home", NavigateHome);
        AddNavItem("👤", "Profile", () => Navigate("Profile", () => new NodeAreaView(_hub, DeviceOnboarding.DeviceUserId)));
        AddNavItem("🎙", "Voice", () => Navigate("Voice", () => _services.GetRequiredService<VoiceView>()));
        AddNavItem("🧩", "Instances", () => Navigate("Instances", BuildInstanceManager));
        _navColumn = new Border
        {
            WidthRequest = 210,
            StrokeThickness = 0,
            BackgroundColor = Color.FromArgb("#1C1C1E"),
            Content = new ScrollView { Content = _navMenu },
        };

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

        // ── body: [nav][content][chat] ────────────────────────────────────────────────────────────────
        var body = new Grid
        {
            ColumnSpacing = 0,
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) },
        };
        body.Add(_navColumn, 0);
        body.Add(_frame, 1);
        body.Add(_chatColumn, 2);

        var root = new Grid { RowSpacing = 0, RowDefinitions = { new(GridLength.Auto), new(new GridLength(1, GridUnitType.Auto)), new(GridLength.Star) } };
        root.Add(bar, 0);
        Grid.SetRow(bar, 0);
        var sep = new BoxView { HeightRequest = 1, Color = Colors.Gray, Opacity = 0.25 };
        root.Add(sep);
        Grid.SetRow(sep, 1);
        root.Add(body);
        Grid.SetRow(body, 2);
        Content = root;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;
        NavigateHome();
    }

    // ── nav menu helpers ──────────────────────────────────────────────────────────────────────────────
    private void AddNavItem(string icon, string text, Action action)
    {
        // A left-aligned tappable row (MAUI Button can't left-align its text).
        var row = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Padding = new Thickness(10, 10),
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Content = new Label { Text = $"{icon}   {text}", FontSize = 15, VerticalOptions = LayoutOptions.Center },
        };
        row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(action) });
        _navMenu.Children.Add(row);
    }

    private static Button NavButton(string text, double size) =>
        new() { Text = text, FontSize = size, WidthRequest = 40, BackgroundColor = Colors.Transparent };

    // ── navigation history (the content area) ─────────────────────────────────────────────────────────
    private void Navigate(string title, Func<View> build)
    {
        if (_index < _history.Count - 1)
            _history.RemoveRange(_index + 1, _history.Count - 1 - _index);
        _history.Add((title, build));
        _index = _history.Count - 1;
        Render();
    }

    private void GoBack() { if (_index > 0) { _index--; Render(); } }
    private void GoForward() { if (_index < _history.Count - 1) { _index++; Render(); } }

    private void Render()
    {
        _frame.Content = _history[_index].Build();
        _back.IsEnabled = _index > 0;
        _forward.IsEnabled = _index < _history.Count - 1;
        _back.Opacity = _back.IsEnabled ? 1 : 0.35;
        _forward.Opacity = _forward.IsEnabled ? 1 : 0.35;
        _instance.Text = $"{_current?.Name ?? "Local"} ▾";
        _crumb.Text = _history[_index].Title;
    }

    // ── destinations ────────────────────────────────────────────────────────────────────────────────
    private void NavigateHome() => Navigate("Home", BuildPortalHome);

    private View BuildPortalHome()
    {
        var userName = _hub.ServiceProvider.GetService<AccessService>()?.CircuitContext?.Name ?? "you";
        return new ActivityDashboardView(_hub, userName, $"{_current?.Name ?? "your local"} mesh")
        {
            OnNodeSelected = node => Navigate(node.Name ?? node.Path, () => new NodeAreaView(_hub, node.Path)),
        };
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
}
