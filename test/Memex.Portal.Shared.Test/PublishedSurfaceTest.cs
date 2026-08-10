using System.Linq;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The publication marker's contract: "published to the web" is a QUERY over the Anonymous Read
/// grant, not a location and not a second flag — and the human-facing list is built from the same
/// enumeration the sitemap renders, so the two cannot drift.
/// </summary>
public class PublishedSurfaceTest
{
    private static MeshNode Node(string path, string? name = null, object? content = null) =>
        new(path) { NodeType = "Store/Plugin", Name = name, Content = content };

    private sealed record PluginLike(string? OgImage = null);

    /// <summary>
    /// 🚨 PUBLISHING MOVES NOTHING. A page's public URL is its ordinary mesh path — no `Www/`
    /// prefix, no relocation. Relocating public content would rewrite every shared link, every
    /// canonical and every og:url, which is the opposite of what publishing well requires; this
    /// pins that the row's URL is the path itself.
    /// </summary>
    [Fact]
    public void ThePublicUrl_IsTheNodesOwnPath_NotARelocatedOne()
    {
        var page = new PublishedPage(Node("Claims", "Claims"), "Claims");

        var row = PublishedSettingsTab.RowFor(page);

        Assert.Equal("/Claims", row.Url);
        Assert.DoesNotContain("Www", row.Url, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The share-image column answers the question people actually ask — is this the card we made,
    /// or the one the portal drew? It uses the SAME precedence as the crawler-facing head, so the
    /// column cannot claim one thing while the meta tag says another.
    /// </summary>
    [Fact]
    public void TheShareImageColumn_ReportsTheAuthoredImageWhenThereIsOne()
    {
        var authored = new PublishedPage(
            Node("Claims", "Claims", new PluginLike(OgImage: "/api/content/Claims/content/og.png")),
            "Claims");

        Assert.Equal("/api/content/Claims/content/og.png", PublishedSettingsTab.RowFor(authored).ShareImage);
    }

    /// <summary>…and says so plainly when the portal generates one, rather than leaving it blank
    /// (a blank column reads as "no card", which is exactly wrong now).</summary>
    [Fact]
    public void TheShareImageColumn_SaysGeneratedWhenNoneIsAuthored()
    {
        var page = new PublishedPage(Node("Chess", "Chess", new PluginLike()), "Chess");

        Assert.Equal("generated card", PublishedSettingsTab.RowFor(page).ShareImage);
    }

    /// <summary>A brochure page under a plugin keeps its full path — the list must be openable.</summary>
    [Fact]
    public void ANestedPublicSegment_KeepsItsFullPath()
    {
        var page = new PublishedPage(
            Node("AgenticPrimer/01-TheMagicWish", "The Magic Wish"), "AgenticPrimer/01-TheMagicWish");

        Assert.Equal("/AgenticPrimer/01-TheMagicWish", PublishedSettingsTab.RowFor(page).Url);
    }

    /// <summary>Rows sort by URL so the list reads as a site map rather than in query order.</summary>
    [Fact]
    public void Rows_AreOrderedByUrl()
    {
        var pages = new[]
        {
            new PublishedPage(Node("Underwriting", "Underwriting"), "Underwriting"),
            new PublishedPage(Node("Claims", "Claims"), "Claims"),
            new PublishedPage(Node("AgenticPrimer", "Primer"), "AgenticPrimer"),
        };

        var urls = PublishedSettingsTab.RowsFor(pages).Select(r => r.Url).ToArray();

        Assert.Equal(new[] { "/AgenticPrimer", "/Claims", "/Underwriting" }, urls);
    }

    /// <summary>An empty surface is a real answer, not an error — and must not throw.</summary>
    [Fact]
    public void AnEmptySurface_ProducesNoRows()
        => Assert.Empty(PublishedSettingsTab.RowsFor([]));
}
