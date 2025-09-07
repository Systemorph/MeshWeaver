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
/// Provides methods to add and manage inventory analysis areas in the layout.
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
    /// Gets the stock levels analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing stock levels analysis.</returns>
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
    /// Gets supplier analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing supplier analysis.</returns>
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