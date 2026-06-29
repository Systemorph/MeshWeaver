using System.Collections.Generic;
using System.Linq;
using HtmlAgilityPack;

namespace MeshWeaver.Hosting.Monolith.TestBase;

/// <summary>
/// Test helper for inspecting rendered HTML — extracts the layout-area coordinates
/// (the <c>data-address</c> / <c>data-area</c> attributes) emitted by the Blazor renderer.
/// </summary>
public static class HtmlParser
{
    /// <summary>
    /// Parses <paramref name="htmlContent"/> and returns the (address, area) pair of every element
    /// carrying a <c>data-address</c> attribute, so a test can assert which layout areas were rendered.
    /// </summary>
    /// <param name="htmlContent">The rendered HTML markup to scan.</param>
    /// <returns>One (Address, Area) tuple per matching element; empty when none are present.</returns>
    public static List<(string Address, string Area)> ExtractDataAddressAttributes(string htmlContent)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(htmlContent);

        var nodes = doc.DocumentNode.SelectNodes("//*[@data-address]");
        return nodes?.Select(node =>
            (node.GetAttributeValue("data-address", string.Empty), node.GetAttributeValue("data-area", string.Empty))
        ).ToList() ?? new();
    }
}
