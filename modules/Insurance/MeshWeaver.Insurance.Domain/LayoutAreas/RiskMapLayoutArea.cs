using System.Reactive.Linq;
using MeshWeaver.Insurance.Domain.LayoutAreas.Shared;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Insurance.Domain.LayoutAreas;

/// <summary>
/// Layout area for displaying a map of property risks.
/// </summary>
public static class RiskMapLayoutArea
{
    /// <summary>
    /// Renders a map view of property risks for a specific pricing.
    /// </summary>
    public static IObservable<UiControl> RiskMap(LayoutAreaHost host, RenderingContext ctx)
    {
        _ = ctx;
        var pricingId = host.Hub.Address.Id;
        var riskStream = host.Workspace.GetStream<PropertyRisk>()!;

        return riskStream.Select(risks =>
        {
            var riskList = risks?.ToList() ?? new List<PropertyRisk>();
            var geocodedRisks = riskList.Where(r => r.GeocodedLocation?.Latitude != null && r.GeocodedLocation?.Longitude != null).ToList();

            if (!riskList.Any())
            {
                return Controls.Stack
                    .WithView(PricingLayoutShared.BuildToolbar(pricingId, "RiskMap"))
                    .WithView(Controls.Markdown("# Risk Map\n\n*No risks loaded. Import or add risks to begin.*"));
            }

            if (!geocodedRisks.Any())
            {
                return Controls.Stack
                    .WithView(PricingLayoutShared.BuildToolbar(pricingId, "RiskMap"))
                    .WithView(Controls.Markdown($"# Risk Map\n\n*No geocoded risks found. {riskList.Count} risk(s) available but none have valid coordinates.*"));
            }

            var mapContent = RenderMapContent(geocodedRisks);

            return Controls.Stack
                .WithView(PricingLayoutShared.BuildToolbar(pricingId, "RiskMap"))
                .WithView(Controls.Markdown(mapContent));
        })
        .StartWith(Controls.Stack
            .WithView(PricingLayoutShared.BuildToolbar(pricingId, "RiskMap"))
            .WithView(Controls.Markdown("# Risk Map\n\n*Loading...*")));
    }

    private static string RenderMapContent(List<PropertyRisk> geocodedRisks)
    {
        var lines = new List<string>
        {
            "# Risk Map",
            "",
            $"**Total Geocoded Risks:** {geocodedRisks.Count}",
            "",
            "## Risk Locations",
            ""
        };

        foreach (var risk in geocodedRisks.Take(10))
        {
            lines.Add($"- **{risk.LocationName ?? "Unknown"}**: {risk.City}, {risk.State}, {risk.Country}");
            lines.Add($"  - Coordinates: {risk.GeocodedLocation!.Latitude:F6}, {risk.GeocodedLocation.Longitude:F6}");
            lines.Add($"  - TSI Building: {risk.Currency} {risk.TsiBuilding:N0}");
            lines.Add("");
        }

        if (geocodedRisks.Count > 10)
        {
            lines.Add($"*... and {geocodedRisks.Count - 10} more risk(s)*");
            lines.Add("");
        }

        lines.Add("---");
        lines.Add("");
        lines.Add("*Interactive map visualization coming soon...*");

        return string.Join("\n", lines);
    }
}
