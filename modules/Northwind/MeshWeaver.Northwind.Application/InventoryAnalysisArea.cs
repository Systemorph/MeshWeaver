using System.Reactive.Linq;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates inventory management displays showing stock levels, reorder status, and supplier performance analytics.
/// Features data grids with stock status indicators (Out of Stock, Low Stock, Normal, High) and supplier metrics
/// including product counts, revenue totals, and average pricing across all suppliers.
/// </summary>
public static class InventoryAnalysisArea
{
    /// <summary>
    /// Adds the inventory analysis area to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the inventory analysis area will be added.</param>
    /// <returns>The updated layout definition with the inventory analysis area added.</returns>
    public static LayoutDefinition AddInventoryAnalysis(this LayoutDefinition layout)
        => layout.WithView(nameof(StockLevelsAnalysis), StockLevelsAnalysis)
            .WithView(nameof(SupplierAnalysis), SupplierAnalysis);

    /// <summary>
    /// Displays a data grid showing inventory status for all products with stock level analysis.
    /// Columns include product name, units in stock, units on order, reorder level, total units sold,
    /// and calculated stock status (Out of Stock, Low Stock, Normal Stock, High Stock).
    /// Products are sorted by stock level (lowest first) to prioritize items needing attention.
    /// Stock status helps identify which products need reordering or have excess inventory.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A data grid with product inventory details and color-coded stock status indicators.</returns>
    public static IObservable<UiControl> StockLevelsAnalysis(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var stockAnalysis = data.GroupBy(x => x.ProductName)
                    .Select(g => new
                    {
                        ProductName = g.Key,
                        UnitsInStock = g.First().UnitsInStock,
                        UnitsOnOrder = g.First().UnitsOnOrder,
                        ReorderLevel = g.First().ReorderLevel,
                        TotalSold = g.Sum(x => x.Quantity),
                        StockStatus = g.First().UnitsInStock switch
                        {
                            0 => "Out of Stock",
                            var stock when stock <= g.First().ReorderLevel => "Low Stock",
                            var stock when stock <= g.First().ReorderLevel * 2 => "Normal Stock",
                            _ => "High Stock"
                        }
                    })
                    .OrderBy(x => x.UnitsInStock);

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Stock Levels Analysis"))
                        .WithView(layoutArea.ToDataGrid(stockAnalysis.ToArray()))
                );
            });

    /// <summary>
    /// Shows supplier performance metrics in a comprehensive data grid analysis.
    /// Displays supplier company names with key performance indicators: number of different products supplied,
    /// total revenue generated, total quantity sold, and average unit price across their products.
    /// Suppliers are ranked by total revenue to identify the most valuable business partnerships.
    /// Helps with supplier relationship management and procurement decisions.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A data grid showing supplier performance metrics sorted by revenue contribution.</returns>
    public static IObservable<UiControl> SupplierAnalysis(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .CombineLatest(layoutArea.Workspace.GetStream<Supplier>()!)
            .SelectMany(tuple =>
            {
                var data = tuple.First;
                var suppliers = tuple.Second!.ToDictionary(s => s.SupplierId, s => s.CompanyName);
                
                var supplierData = data.GroupBy(x => x.Supplier)
                    .Select(g => new
                    {
                        Supplier = suppliers.TryGetValue(g.Key, out var name) ? name : g.Key.ToString(),
                        ProductCount = g.Select(x => x.Product).Distinct().Count(),
                        TotalRevenue = g.Sum(x => x.Amount),
                        TotalQuantity = g.Sum(x => x.Quantity),
                        AvgUnitPrice = g.Average(x => x.UnitPrice)
                    })
                    .OrderByDescending(x => x.TotalRevenue);

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Supplier Performance Analysis"))
                        .WithView(layoutArea.ToDataGrid(supplierData.ToArray()))
                );
            });

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}