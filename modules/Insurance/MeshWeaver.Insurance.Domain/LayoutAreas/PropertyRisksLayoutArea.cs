using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Utils;

namespace MeshWeaver.Insurance.Domain.LayoutAreas;

/// <summary>
/// Layout area for displaying property risks associated with a pricing.
/// </summary>
public static class PropertyRisksLayoutArea
{
    /// <summary>
    /// Renders the property risks grid for a specific pricing.
    /// </summary>
    public static IObservable<UiControl> PropertyRisks(LayoutAreaHost host, RenderingContext ctx)
    {
        _ = ctx;
        var pricingId = host.Hub.Address.Id;
        var riskStream = host.Workspace.GetStream<PropertyRisk>()!;

        return riskStream.Select(risks =>
        {
            var riskList = risks?.ToList() ?? new List<PropertyRisk>();

            if (!riskList.Any())
            {
                return Controls.Stack
                    .WithView(BuildToolbar(pricingId))
                    .WithView(Controls.Markdown("# Property Risks\n\n*No risks loaded. Import or add risks to begin.*"));
            }

            var dataGrid = RenderRisksDataGrid(host, riskList);

            return Controls.Stack
                .WithView(BuildToolbar(pricingId))
                .WithView(dataGrid);
        })
        .StartWith(Controls.Stack
            .WithView(BuildToolbar(pricingId))
            .WithView(Controls.Markdown("# Property Risks\n\n*Loading...*")));
    }

    private static UiControl BuildToolbar(string pricingId)
    {
        return Controls.Markdown($"[← Back to Overview](/insurance-pricing/{pricingId}/Overview)");
    }

    private static UiControl RenderRisksDataGrid(LayoutAreaHost host, IReadOnlyCollection<PropertyRisk> risks)
    {
        // Project risks to a display model
        var riskDetails = risks.Select(r => new PropertyRiskDetailsModel
        {
            Id = r.Id,
            SourceRow = r.SourceRow,
            LocationName = r.LocationName,
            Address = r.Address,
            City = r.City,
            State = r.State,
            Country = r.Country,
            TsiBuilding = r.TsiBuilding,
            TsiContent = r.TsiContent,
            TsiBi = r.TsiBi,
            Currency = r.Currency,
            Latitude = r.GeocodedLocation?.Latitude,
            Longitude = r.GeocodedLocation?.Longitude,
            GeocodedAddress = r.GeocodedLocation?.FormattedAddress,
            GeocodedStatus = r.GeocodedLocation?.Status,
        }).ToList();

        // Create data grid using stream pattern
        var id = Guid.NewGuid().ToString();

        // Register the data with the host
        host.RegisterForDisposal(Observable.Return(riskDetails)
            .Subscribe(data => host.UpdateData(id, data)));

        // Create and configure the data grid
        var dataGrid = new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)))
            .WithColumn(new PropertyColumnControl<int?> { Property = nameof(PropertyRiskDetailsModel.SourceRow).ToCamelCase() }.WithTitle("Row"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(PropertyRiskDetailsModel.LocationName).ToCamelCase() }.WithTitle("Location"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(PropertyRiskDetailsModel.Address).ToCamelCase() }.WithTitle("Address"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(PropertyRiskDetailsModel.City).ToCamelCase() }.WithTitle("City"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(PropertyRiskDetailsModel.State).ToCamelCase() }.WithTitle("State"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(PropertyRiskDetailsModel.Country).ToCamelCase() }.WithTitle("Country"))
            .WithColumn(new PropertyColumnControl<double> { Property = nameof(PropertyRiskDetailsModel.TsiBuilding).ToCamelCase() }.WithTitle("TSI Building").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<double> { Property = nameof(PropertyRiskDetailsModel.TsiContent).ToCamelCase() }.WithTitle("TSI Content").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<double> { Property = nameof(PropertyRiskDetailsModel.TsiBi).ToCamelCase() }.WithTitle("TSI BI").WithFormat("N0"))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(PropertyRiskDetailsModel.Currency).ToCamelCase() }.WithTitle("Currency"))
            .WithColumn(new PropertyColumnControl<double?> { Property = nameof(PropertyRiskDetailsModel.Latitude).ToCamelCase() }.WithTitle("Latitude").WithFormat("F6"))
            .WithColumn(new PropertyColumnControl<double?> { Property = nameof(PropertyRiskDetailsModel.Longitude).ToCamelCase() }.WithTitle("Longitude").WithFormat("F6"))
            .WithItemSize(40)
            .Resizable();

        return Controls.Stack
            .WithView(Controls.Title("Property Risks", 1))
            .WithView(dataGrid);
    }
}

/// <summary>
/// Display model for property risk details in the data grid.
/// </summary>
internal class PropertyRiskDetailsModel
{
    public string? Id { get; set; }
    public int? SourceRow { get; set; }
    public string? LocationName { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }
    public double TsiBuilding { get; set; }
    public double TsiContent { get; set; }
    public double TsiBi { get; set; }
    public string? Currency { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? GeocodedAddress { get; set; }
    public string? GeocodedStatus { get; set; }
}
