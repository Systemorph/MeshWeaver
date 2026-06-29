using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace Memex.Client.Pages;

/// <summary>
/// Real mesh search results: runs the user's query through <c>hub.GetQuery</c> (the same reactive query the
/// portal search uses — free-text routes to vector/ILIKE search at the backend) and renders the matches as
/// node cards via the shared <see cref="NodeCardListView"/>. No hand-rolled / fabricated result list.
/// </summary>
public sealed class SearchResultsView : ContentView
{
    public SearchResultsView(IMessageHub hub, string query, Action<MeshNode> onNodeSelected)
    {
        var cards = new NodeCardListView(hub)
        {
            OnNodeSelected = onNodeSelected,
            EmptyMessage = $"No results for “{query}”.",
        };
        cards.SetQuery(query);

        Content = new ScrollView
        {
            Padding = new Thickness(24, 18),
            Content = new VerticalStackLayout
            {
                Spacing = 14,
                Children =
                {
                    new Label { Text = $"Results for “{query}”", FontSize = 22, FontAttributes = FontAttributes.Bold },
                    new BoxView { HeightRequest = 1, Color = Colors.Gray, Opacity = 0.25 },
                    cards,
                },
            },
        };
    }
}
