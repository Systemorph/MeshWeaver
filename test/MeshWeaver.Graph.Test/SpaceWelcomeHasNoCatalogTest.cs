using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins policy <c>explorer-navigation</c> on the default Space welcome text: a Space's children are
/// navigated in the side rail, so the template must never end in a "children section" — a
/// <c>## Contents</c> heading or a <c>Search</c>-area catalog embed restating that index.
/// </summary>
public class SpaceWelcomeHasNoCatalogTest
{
    [Theory]
    [InlineData("## Contents")]
    [InlineData("## Everything in this space")]
    [InlineData("area/Search")]
    [InlineData("area:Search")]
    public void WelcomeMarkdown_DoesNotRestateTheIndex(string catalogMarker)
        => Assert.DoesNotContain(catalogMarker, SpaceNodeType.WelcomeMarkdown, System.StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void WelcomeMarkdown_PointsTheReaderAtTheIndex()
        => Assert.Contains("index on the left", SpaceNodeType.WelcomeMarkdown, System.StringComparison.Ordinal);
}
