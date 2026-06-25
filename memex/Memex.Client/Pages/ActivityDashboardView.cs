using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;

namespace Memex.Client.Pages;

/// <summary>
/// A native MAUI rendition of the web portal's User Activity dashboard: a "Welcome back" banner, a tab bar
/// (Threads · Spaces · My Items · Recent), and a wrapping grid of node cards for the selected tab — each
/// card an avatar initial + name + type, like the portal's MeshNodeCard. Data comes from the same
/// <c>hub.GetQuery</c> queries the Activity area uses, run against the in-process mesh. (Built natively
/// because the layout-area pipeline doesn't yet deliver the area's control to the MAUI view pack.)
/// </summary>
public sealed class ActivityDashboardView : ContentView
{
    private static readonly (string Name, string Query)[] Tabs =
    {
        ("Threads", "nodeType:Thread sort:LastModified-desc"),
        ("Spaces", "nodeType:Space is:main sort:LastModified-desc"),
        ("My Items", "is:main sort:LastModified-desc"),
        ("Recent", "is:main sort:LastModified-desc"),
    };

    private readonly IMessageHub _hub;
    private readonly HorizontalStackLayout _tabBar = new() { Spacing = 4 };
    private readonly FlexLayout _cards = new() { Wrap = FlexWrap.Wrap, AlignItems = FlexAlignItems.Start };
    private string _activeTab = Tabs[0].Name;
    private IDisposable? _querySub;

    /// <summary>Raised when a node card is tapped — the shell navigates to that node's area.</summary>
    public Action<MeshNode>? OnNodeSelected { get; set; }

    public ActivityDashboardView(IMessageHub hub, string userName, string instanceName)
    {
        _hub = hub;

        var banner = new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                new Label { Text = $"Welcome back, {userName}", FontSize = 26, FontAttributes = FontAttributes.Bold },
                new Label { Text = $"Signed in as {userName} · {instanceName}", FontSize = 13, TextColor = Colors.Gray },
            },
        };

        foreach (var (name, _) in Tabs)
            _tabBar.Children.Add(TabButton(name));

        Content = new ScrollView
        {
            Padding = new Thickness(24, 18),
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Children =
                {
                    banner,
                    new BoxView { HeightRequest = 1, Color = Colors.Gray, Opacity = 0.25 },
                    _tabBar,
                    _cards,
                },
            },
        };

        SelectTab(_activeTab);
    }

    private Button TabButton(string name)
    {
        var active = name == _activeTab;
        var btn = new Button
        {
            Text = name,
            FontSize = 14,
            FontAttributes = active ? FontAttributes.Bold : FontAttributes.None,
            TextColor = active ? Colors.RoyalBlue : Colors.Gray,
            BackgroundColor = Colors.Transparent,
            Padding = new Thickness(8, 4),
        };
        btn.Clicked += (_, _) => SelectTab(name);
        return btn;
    }

    private void SelectTab(string name)
    {
        _activeTab = name;
        // Rebuild the tab bar so the active style updates.
        _tabBar.Children.Clear();
        foreach (var (t, _) in Tabs) _tabBar.Children.Add(TabButton(t));

        var query = Tabs.First(t => t.Name == name).Query;
        _cards.Children.Clear();
        _cards.Children.Add(new ActivityIndicator { IsRunning = true, Margin = new Thickness(4, 12) });

        _querySub?.Dispose();
        _querySub = _hub.GetQuery("dashboard:" + query, query).Take(1)
            .Subscribe(nodes => MainThread.BeginInvokeOnMainThread(() => RenderCards(nodes)),
                       _ => MainThread.BeginInvokeOnMainThread(() => RenderCards(Array.Empty<MeshNode>())));
    }

    private void RenderCards(IEnumerable<MeshNode> nodes)
    {
        _cards.Children.Clear();
        var list = nodes.ToList();
        if (list.Count == 0)
        {
            _cards.Children.Add(new Label { Text = "No items found.", TextColor = Colors.Gray, Margin = new Thickness(4, 8) });
            return;
        }
        foreach (var node in list)
            _cards.Children.Add(Card(node));
    }

    private View Card(MeshNode node)
    {
        var name = node.Name ?? node.Path;
        var avatar = new Border
        {
            WidthRequest = 36,
            HeightRequest = 36,
            BackgroundColor = ColorFor(name),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            Content = new Label
            {
                Text = (name.Length > 0 ? name[..1] : "?").ToUpperInvariant(),
                TextColor = Colors.White,
                FontAttributes = FontAttributes.Bold,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            },
        };

        var card = new Border
        {
            Margin = new Thickness(4),
            Padding = 12,
            StrokeThickness = 1,
            Stroke = Color.FromArgb("#3A3A3C"),
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Content = new HorizontalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    avatar,
                    new VerticalStackLayout
                    {
                        Spacing = 1,
                        VerticalOptions = LayoutOptions.Center,
                        Children =
                        {
                            new Label { Text = name, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation },
                            new Label { Text = node.NodeType ?? "", FontSize = 11, TextColor = Colors.Gray },
                        },
                    },
                },
            },
        };
        card.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => OnNodeSelected?.Invoke(node)) });
        // Roughly 2–3 cards per row.
        FlexLayout.SetBasis(card, new FlexBasis(0.31f, true));
        return card;
    }

    // Deterministic pleasant color from the node name.
    private static Color ColorFor(string s)
    {
        var palette = new[] { "#5B8DEF", "#9B59B6", "#16A085", "#E67E22", "#E74C3C", "#2C82C9", "#27AE60", "#8E44AD" };
        var sum = 0;
        foreach (var c in s) sum += c;
        return Color.FromArgb(palette[Math.Abs(sum) % palette.Length]);
    }
}
