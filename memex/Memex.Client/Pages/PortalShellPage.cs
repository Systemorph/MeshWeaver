using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Maui;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Memex.Client.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Shapes;

namespace Memex.Client.Pages;

/// <summary>
/// The app's browser-like shell: a single page with a title bar — [← back] [→ forward], the CURRENT
/// instance name (tap → switch instance / connect a new one), 🏠 Home, 🎙 Voice — over a content frame.
/// Navigation keeps a real history stack with both directions (back AND forward). Each history entry is a
/// view factory; navigating rebuilds the view (a new navigation truncates forward history, like a browser).
/// </summary>
public sealed class PortalShellPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly InstanceStore _store;
    private readonly IMessageHub _hub;

    private readonly List<(string Title, Func<View> Build)> _history = new();
    private int _index = -1;
    private bool _started;

    private readonly Button _back = new() { Text = "←", FontSize = 18, WidthRequest = 44, BackgroundColor = Colors.Transparent };
    private readonly Button _forward = new() { Text = "→", FontSize = 18, WidthRequest = 44, BackgroundColor = Colors.Transparent };
    private readonly Button _instance = new() { FontSize = 15, FontAttributes = FontAttributes.Bold, BackgroundColor = Colors.Transparent };
    private readonly ContentView _frame = new() { VerticalOptions = LayoutOptions.Fill, HorizontalOptions = LayoutOptions.Fill };
    private MemexInstance? _current;

    public PortalShellPage(IServiceProvider services, InstanceStore store, IMessageHub hub)
    {
        _services = services;
        _store = store;
        _hub = hub;
        _current = store.Instances.FirstOrDefault();
        Title = "Memex";

        _back.Clicked += (_, _) => GoBack();
        _forward.Clicked += (_, _) => GoForward();
        _instance.Clicked += async (_, _) => await ShowInstanceSwitcherAsync();
        var home = new Button { Text = "🏠", FontSize = 16, WidthRequest = 40, BackgroundColor = Colors.Transparent };
        home.Clicked += (_, _) => NavigateHome();
        var chat = new Button { Text = "💬", FontSize = 16, WidthRequest = 40, BackgroundColor = Colors.Transparent };
        chat.Clicked += (_, _) => NavigateChat();
        var voice = new Button { Text = "🎙", FontSize = 16, WidthRequest = 40, BackgroundColor = Colors.Transparent };
        voice.Clicked += (_, _) => NavigateVoice();

        var bar = new Grid
        {
            Padding = new Thickness(6, 4),
            ColumnSpacing = 2,
            ColumnDefinitions =
            {
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star),
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
            },
        };
        bar.Add(_back, 0);
        bar.Add(_forward, 1);
        bar.Add(_instance, 2);
        bar.Add(home, 3);
        bar.Add(chat, 4);
        bar.Add(voice, 5);

        // A bordered bar over a filling content frame. Two rows: [bar Auto][frame Star].
        var root = new Grid { RowSpacing = 0, RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) } };
        root.Add(new Border
        {
            Content = bar,
            StrokeThickness = 0,
            Stroke = Colors.Transparent,
            Padding = 0,
        }, 0, 0);
        root.Add(_frame, 0, 1);
        Content = root;
    }

    // Build the initial view once the page is actually appearing — creating the LayoutAreaView (and its
    // stream subscription) during the constructor, before the page is in the visual tree, can land its
    // async content set on a view that never displays.
    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;
        NavigateHome();
    }

    // ── navigation history ──────────────────────────────────────────────────────────────────────────
    private void Navigate(string title, Func<View> build)
    {
        // Truncate any forward history (a new navigation forks the timeline, like a browser).
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
    }

    // ── destinations ────────────────────────────────────────────────────────────────────────────────
    private void NavigateHome() => Navigate("Home", BuildPortalHome);
    private void NavigateChat() => Navigate("Chat", () => _services.GetRequiredService<ChatView>());
    private void NavigateVoice() => Navigate("Voice", () => _services.GetRequiredService<VoiceView>());

    // The home is a native rendition of the web portal's User Activity dashboard (welcome banner + tabs +
    // node-card grid), driven by the same GetQuery queries. Built natively because the layout-area pipeline
    // doesn't yet deliver the area's control to the MAUI view pack (under investigation); the view pack +
    // its controls (forms, picker, catalog, Monaco chat) remain for node areas / once that's fixed.
    private View BuildPortalHome() => new ActivityDashboardView(_hub, $"{_current?.Name ?? "your local"} mesh");

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
