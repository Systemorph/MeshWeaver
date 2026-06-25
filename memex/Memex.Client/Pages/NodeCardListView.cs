using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;

namespace Memex.Client.Pages;

/// <summary>
/// A reactive grid of mesh-node cards bound to a <c>hub.GetQuery</c> query — the shared building block for
/// the dashboard tabs and search results (one card = avatar initial + name + type, like the portal's
/// MeshNodeCard). Call <see cref="SetQuery"/> to (re)bind; the previous subscription is replaced. Tapping a
/// card raises <see cref="OnNodeSelected"/>. Factored out so the dashboard and search share ONE card list
/// instead of replicating it.
/// </summary>
public sealed class NodeCardListView : ContentView, IDisposable
{
    private readonly IMessageHub _hub;
    private readonly FlexLayout _cards = new() { Wrap = FlexWrap.Wrap, AlignItems = FlexAlignItems.Start };
    private IDisposable? _querySub;

    /// <summary>Raised when a node card is tapped.</summary>
    public Action<MeshNode>? OnNodeSelected { get; set; }

    /// <summary>Shown when a query returns no nodes.</summary>
    public string EmptyMessage { get; set; } = "No items found.";

    /// <summary>Optional custom empty-state view (e.g. a "➕ New thread" call-to-action). Overrides
    /// <see cref="EmptyMessage"/> when set.</summary>
    public Func<View>? EmptyView { get; set; }

    public NodeCardListView(IMessageHub hub)
    {
        _hub = hub;
        Content = _cards;
    }

    /// <summary>Binds the grid to <paramref name="query"/> (a mesh query string). Replays current matches,
    /// then renders. Replaces any prior binding.</summary>
    public void SetQuery(string query)
    {
        _cards.Children.Clear();
        _cards.Children.Add(new ActivityIndicator { IsRunning = true, Margin = new Thickness(4, 12) });

        _querySub?.Dispose();
        // Surface failures (incl. a 15s timeout) as a VISIBLE error instead of an eternal spinner — so a
        // query the backend can't run (e.g. an unsupported sort/filter on SQLite) is diagnosable, not silent.
        _querySub = _hub.GetQuery("cards:" + query, query).Take(1).Timeout(TimeSpan.FromSeconds(15))
            .Subscribe(nodes => MainThread.BeginInvokeOnMainThread(() => Render(nodes)),
                       ex => MainThread.BeginInvokeOnMainThread(() => RenderError(query, ex)));
    }

    private void Render(IEnumerable<MeshNode> nodes)
    {
        _cards.Children.Clear();
        var list = nodes.ToList();
        if (list.Count == 0)
        {
            _cards.Children.Add(EmptyView?.Invoke()
                ?? new Label { Text = EmptyMessage, TextColor = Colors.Gray, Margin = new Thickness(4, 8) });
            return;
        }
        foreach (var node in list)
            _cards.Children.Add(Card(node, OnNodeSelected));
    }

    private void RenderError(string query, Exception ex)
    {
        _cards.Children.Clear();
        _cards.Children.Add(new VerticalStackLayout
        {
            Spacing = 4,
            Margin = new Thickness(4, 8),
            Children =
            {
                new Label { Text = "Couldn't load this list.", TextColor = Colors.OrangeRed, FontAttributes = FontAttributes.Bold },
                new Label { Text = $"query: {query}", FontSize = 12, TextColor = Colors.Gray },
                new Label { Text = $"{ex.GetType().Name}: {ex.Message}", FontSize = 12, TextColor = Colors.Gray },
            },
        });
    }

    /// <summary>Builds one node card. Static so any view can render a node card consistently.</summary>
    public static View Card(MeshNode node, Action<MeshNode>? onSelected)
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
        card.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => onSelected?.Invoke(node)) });
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

    public void Dispose() => _querySub?.Dispose();
}
