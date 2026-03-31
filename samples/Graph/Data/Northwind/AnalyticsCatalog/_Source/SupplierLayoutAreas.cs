// <meshweaver>
// Id: SupplierLayoutAreas
// DisplayName: Supplier Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;

/// <summary>
/// Supplier analysis views.
/// </summary>
[Display(GroupName = "Suppliers", Order = 420)]
public static class SupplierLayoutAreas
{
    public static LayoutDefinition AddSupplierLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView(nameof(SupplierSummary), SupplierSummary)
            .WithView(nameof(SupplierAnalysis), SupplierAnalysis);

    /// <summary>
    /// Supplier summary data grid with revenue and product count.
    /// </summary>
    [Display(GroupName = "Suppliers", Order = 420)]
    public static UiControl SupplierSummary(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var suppliers = data
                .GroupBy(x => x.SupplierName ?? x.Supplier.ToString())
                .Select(g => new
                {
                    Supplier = g.Key,
                    Products = g.DistinctBy(x => x.Product).Count(),
                    Revenue = Math.Round(g.Sum(x => x.Amount), 2),
                    Orders = g.DistinctBy(x => x.OrderId).Count()
                })
                .OrderByDescending(x => x.Revenue)
                .ToArray();

            return (UiControl)layoutArea.ToDataGrid(suppliers, config => config.AutoMapProperties());
        });

    /// <summary>
    /// Supplier revenue distribution as a bar chart.
    /// </summary>
    [Display(GroupName = "Suppliers", Order = 421)]
    public static UiControl SupplierAnalysis(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var topSuppliers = data
                .GroupBy(x => x.SupplierName ?? x.Supplier.ToString())
                .Select(g => new { Supplier = g.Key, Revenue = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.Revenue)
                .Take(10)
                .ToArray();

            return (UiControl)Charts.Bar(
                topSuppliers.Select(x => x.Revenue),
                topSuppliers.Select(x => x.Supplier)
            ).WithTitle($"Top 10 Suppliers by Revenue ({year})");
        });
}
