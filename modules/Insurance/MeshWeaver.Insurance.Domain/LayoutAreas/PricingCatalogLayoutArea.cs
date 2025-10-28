using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Insurance.Domain.LayoutAreas;

/// <summary>
/// Layout area for displaying the insurance pricing catalog.
/// </summary>
public static class PricingCatalogLayoutArea
{
    /// <summary>
    /// Renders the pricing catalog as a table with links to individual pricings.
    /// This is displayed at /app/Insurance/Pricings
    /// </summary>
    public static IObservable<UiControl> Pricings(LayoutAreaHost host, RenderingContext ctx)
    {
        _ = ctx;
        return host.Workspace.GetStream<Pricing>()!
            .Select(pricings => Controls.Markdown(RenderPricingTable(pricings!)))
            .StartWith(Controls.Markdown("# Insurance Pricing Catalog\n\n*Loading...*"));
    }

    private static string RenderPricingTable(IReadOnlyCollection<Pricing> pricings)
    {
        if (!pricings.Any())
            return "# Insurance Pricing Catalog\n\n*No pricings available.*";

        var lines = new List<string>
        {
            "# Insurance Pricing Catalog | [Data Model](/pricing/default/DataModel)",
            "",
            "| Insured | Line of Business | Country | Legal Entity | Inception | Expiration | Status |",
            "|---------|------------------|---------|--------------|-----------|------------|--------|"
        };

        lines.AddRange(pricings
            .OrderByDescending(p => p.InceptionDate ?? DateTime.MaxValue)
            .Select(p =>
            {
                var link = $"[{p.InsuredName}](/pricing/{p.Id}/Overview)";
                var inception = p.InceptionDate?.ToString("yyyy-MM-dd") ?? "-";
                var expiration = p.ExpirationDate?.ToString("yyyy-MM-dd") ?? "-";
                return $"| {link} | {p.LineOfBusiness ?? "-"} | {p.Country ?? "-"} | {p.LegalEntity ?? "-"} | {inception} | {expiration} |  {p.Status ?? "-"} |";
            }));

        return string.Join("\n", lines);
    }
}
