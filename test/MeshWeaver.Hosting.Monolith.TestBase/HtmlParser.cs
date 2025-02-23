using System.Collections.Generic;
using System.Linq;
using HtmlAgilityPack;

namespace MeshWeaver.Hosting.Monolith.TestBase;

public static class HtmlParser
{
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
