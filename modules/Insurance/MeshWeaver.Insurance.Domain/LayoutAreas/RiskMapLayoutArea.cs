using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.GoogleMaps;
using MeshWeaver.Insurance.Domain.LayoutAreas.Shared;
using MeshWeaver.Insurance.Domain.Services;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
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
    public static IObservable<UiControl> RiskMap(LayoutAreaHost host, RenderingContext _)
    {
        var pricingId = host.Hub.Address.Id;

        return host.Workspace.GetStream<PropertyRisk>()!
            .Select(risks =>
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

                var mapControl = BuildGoogleMapControl(geocodedRisks);

                return Observable.Using(
                    () => new ReplaySubject<string?>(1),
                    riskDetailsSubject =>
                    {
                        riskDetailsSubject.OnNext(null);
                        var mapControlWithClick = mapControl.WithClickAction(ctx => riskDetailsSubject.OnNext(ctx.Payload?.ToString()));

                        return Observable.Return(
                            Controls.Stack
                                .WithView(PricingLayoutShared.BuildToolbar(pricingId, "RiskMap"))
                                .WithView(Controls.Title("Risk Map", 2))
                                .WithView(mapControlWithClick)
                                .WithView(GeocodingArea)
                                .WithView(Controls.Title("Risk Details", 3))
                                .WithView((h, c) => riskDetailsSubject
                                    .SelectMany(id => id == null ?
                                        Observable.Return(Controls.Html("Click marker to see details.")) : RenderRiskDetails(host.Hub, id))
                                )
                        );
                    }
                );
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

    private static IObservable<UiControl> RenderRiskDetails(IMessageHub hub, string id)
    {
        return hub.GetWorkspace()
            .GetStream(new EntityReference(nameof(PropertyRisk), id))!
            .Select(r => BuildRiskDetailsMarkdown(r.Value as PropertyRisk));
    }

    private static UiControl BuildRiskDetailsMarkdown(PropertyRisk? risk)
    {
        if (risk is null)
            return Controls.Html("Risk not found");

        return Controls.Stack
            .WithView(Controls.Markdown("## Risk Details"))
            .WithView(Controls.Markdown($"**ID:** {risk.Id}"))
            .WithView(Controls.Markdown($"**Location:** {risk.LocationName ?? "N/A"}"))
            .WithView(Controls.Markdown($"**Address:** {risk.GeocodedLocation?.FormattedAddress ?? risk.Address ?? "N/A"}"))
            .WithView(Controls.Markdown($"**City:** {risk.City ?? "N/A"}"))
            .WithView(Controls.Markdown($"**State:** {risk.State ?? "N/A"}"))
            .WithView(Controls.Markdown($"**Country:** {risk.Country ?? "N/A"}"))
            .WithView(Controls.Markdown($"**Currency:** {risk.Currency ?? "N/A"}"))
            .WithView(Controls.Markdown($"**TSI Building:** {risk.TsiBuilding:N0}"))
            .WithView(Controls.Markdown($"**TSI Content:** {risk.TsiContent:N0}"))
            .WithView(Controls.Markdown($"**TSI BI:** {risk.TsiBi:N0}"))
            .WithView(Controls.Markdown($"**Latitude:** {risk.GeocodedLocation?.Latitude:F6}"))
            .WithView(Controls.Markdown($"**Longitude:** {risk.GeocodedLocation?.Longitude:F6}"));
    }

    private static GoogleMapControl BuildGoogleMapControl(IReadOnlyCollection<PropertyRisk> risks)
    {
        var riskList = risks.Where(r => r.GeocodedLocation?.Latitude is not null && r.GeocodedLocation?.Longitude is not null).ToList();

        // Find center point
        LatLng center;
        if (riskList.Any())
        {
            var avgLat = riskList.Average(r => r.GeocodedLocation!.Latitude!.Value);
            var avgLng = riskList.Average(r => r.GeocodedLocation!.Longitude!.Value);
            center = new LatLng(avgLat, avgLng);
        }
        else
        {
            center = new LatLng(0, 0);
        }

        // Create markers
        var markers = riskList.Select(r => new MapMarker
        {
            Position = new LatLng(r.GeocodedLocation!.Latitude!.Value, r.GeocodedLocation.Longitude!.Value),
            Title = ((r.LocationName ?? r.Address) + " " + (r.City ?? "")).Trim(),
            Id = r.Id,
            Data = r
        }).ToList();

        var mapOptions = new MapOptions
        {
            Center = center,
            Zoom = riskList.Any() ? 6 : 2,
            MapTypeId = "roadmap",
            ZoomControl = true,
            MapTypeControl = true,
            StreetViewControl = false,
            FullscreenControl = true
        };

        return new GoogleMapControl()
        {
            Options = mapOptions,
            Markers = markers,
            Id = "risk-map"
        }.WithStyle(style => style.WithHeight("500px").WithWidth("80%"));
    }
}
