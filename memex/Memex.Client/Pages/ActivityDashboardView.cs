using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace Memex.Client.Pages;

/// <summary>
/// A native MAUI rendition of the web portal's User Activity dashboard: a "Welcome back" banner, a tab bar
/// (Threads · Spaces · My Items · Recent), and a wrapping grid of node cards for the selected tab. Data
/// comes from the same <c>hub.GetQuery</c> queries the Activity area uses, run against the in-process mesh,
/// rendered by the shared <see cref="NodeCardListView"/> (also used by search). (Built natively because the
/// layout-area pipeline doesn't yet deliver the area's control to the MAUI view pack.)
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

    private readonly HorizontalStackLayout _tabBar = new() { Spacing = 4 };
    private readonly NodeCardListView _cards;
    private string _activeTab = Tabs[0].Name;

    /// <summary>Raised when a node card is tapped — the shell navigates to that node's area.</summary>
    public Action<MeshNode>? OnNodeSelected
    {
        get => _cards.OnNodeSelected;
        set => _cards.OnNodeSelected = value;
    }

    public ActivityDashboardView(IMessageHub hub, string userName, string instanceName)
    {
        _cards = new NodeCardListView(hub);

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

        _cards.SetQuery(Tabs.First(t => t.Name == name).Query);
    }
}
