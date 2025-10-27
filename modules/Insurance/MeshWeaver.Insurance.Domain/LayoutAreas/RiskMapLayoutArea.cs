using System.Reactive.Linq;
using MeshWeaver.Insurance.Domain.LayoutAreas.Shared;
using MeshWeaver.Insurance.Domain.Services;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using Microsoft.Extensions.DependencyInjection;

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
                    .WithView(Controls.Markdown($"# Risk Map\n\n*No geocoded risks found. {riskList.Count} risk(s) available but none have valid coordinates.*"))
                    .WithView(GeocodingArea);
            }

            var mapContent = RenderMapContent(geocodedRisks);

            return Controls.Stack
                .WithView(PricingLayoutShared.BuildToolbar(pricingId, "RiskMap"))
                .WithView(Controls.Markdown(mapContent))
                .WithView(GeocodingArea);
        })
        .StartWith(Controls.Stack
            .WithView(PricingLayoutShared.BuildToolbar(pricingId, "RiskMap"))
            .WithView(Controls.Markdown("# Risk Map\n\n*Loading...*")));
    }

    private static IObservable<UiControl> GeocodingArea(LayoutAreaHost host, RenderingContext ctx)
    {
        var svc = host.Hub.ServiceProvider.GetRequiredService<IGeocodingService>();
        return svc.Progress.Select(p => p is null
            ? (UiControl)Controls.Button("Geocode").WithClickAction(ClickGeocoding)
            : Controls.Progress($"Processing {p.CurrentRiskName}: {p.ProcessedRisks} of {p.TotalRisks}",
                p.TotalRisks == 0 ? 0 : (int)(100.0 * p.ProcessedRisks / p.TotalRisks)));
    }

    private static async Task ClickGeocoding(UiActionContext obj)
    {
        // Show initial progress
        obj.Host.UpdateArea(obj.Area, Controls.Progress("Starting geocoding...", 0));

        try
        {
            // Start the geocoding process
            var response = await obj.Host.Hub.AwaitResponse(
                new GeocodingRequest(),
                o => o.WithTarget(obj.Hub.Address));

            // Show completion message
            var resultMessage = response?.Message?.Success == true
                ? $"✅ Geocoding Complete: {response.Message.GeocodedCount} locations geocoded successfully."
                : $"❌ Geocoding Failed: {response?.Message?.Error}";

            obj.Host.UpdateArea(obj.Area, Controls.Markdown($"**{resultMessage}**"));
        }
        catch (Exception ex)
        {
            obj.Host.UpdateArea(obj.Area, Controls.Markdown($"**Geocoding Failed**: {ex.Message}"));
        }
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
