using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates a sales overview display for products within a selected category.
/// Features detailed sales metrics including product names, unit prices, quantities sold, 
/// discounts given, and total revenue for products in the selected category.
/// Includes toolbar filtering by category and displays products ranked by total sales amount.
/// </summary>
[Display(GroupName = "Products", Order = 450)]
public static class SalesInOneCategoryArea
{
    /// <summary>
    /// Adds the sales in one category view to the layout definition.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <returns>The updated layout definition with the sales in one category view.</returns>
    public static LayoutDefinition AddSalesInOneCategory(this LayoutDefinition layout)
        =>
            layout
                .WithView(nameof(SalesInOneCategory), SalesInOneCategory)
    ;

    /// <summary>
    /// Renders a complete sales in one category interface with category filtering toolbar and product sales grid.
    /// The top section contains category selection controls, while the bottom displays a sortable data grid
    /// showing product names, unit prices, units sold, discount amounts, and total revenue for the selected category.
    /// Products are automatically sorted by total revenue in descending order.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A stacked layout with toolbar controls and product sales data grid.</returns>
    public static UiControl SalesInOneCategory(this LayoutAreaHost layoutArea, RenderingContext context) =>
        Controls.Stack
            .WithView(CategoryToolbarArea.CategoryToolbar)
            .WithView(CategorySalesGrid);

    private static IObservable<UiControl> CategorySalesGrid(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.CategorySalesData()
            .Select(data => 
                layoutArea.ToDataGrid(data.ToArray(),
                    config => config.AutoMapProperties()
                    )
                )
        ;

    private static IObservable<IEnumerable<CategorySalesItem>> CategorySalesData(this LayoutAreaHost layoutArea)
        => layoutArea.YearlyDataBySelectedCategory()
            .CombineLatest(layoutArea.Workspace.GetStream(typeof(Category)),
                (data, changeItem) => (data, changeItem))
            .Select(tuple =>
                tuple.data.GroupBy(data => data.Product)
                .Select(g => new CategorySalesItem
                {
                    ProductId = g.Key,
                    ProductName = g.Select(x => x.ProductName).FirstOrDefault(),
                    CategoryName = tuple.changeItem.Value!.GetData<Category>(g.Select(x => x.Category).FirstOrDefault())?.CategoryName,
                    UnitPrice = g.Select(x => x.UnitPrice).FirstOrDefault(),
                    UnitsSold = g.Sum(x => x.Quantity),
                    DiscountGiven = g.Sum(x => x.UnitPrice * x.Quantity * x.Discount),
                    TotalRevenue = g.Sum(x => x.Amount),
                })
                .OrderByDescending(x => x.TotalRevenue)
            );

    private static IObservable<IEnumerable<NorthwindDataCube>> YearlyDataBySelectedCategory(this LayoutAreaHost layoutArea)
        => layoutArea.YearlyNorthwindData()
            .CombineLatest(
                layoutArea.GetDataStream<CategoryToolbar>(nameof(CategoryToolbar)),
                (data, tb) => (data, tb))
            .Select(x => x.data.Where(d => d.Category == x.tb!.Category || x.tb.Category == 0))
    ;
}

/// <summary>
/// Represents a sales overview item for a product within a specific category.
/// </summary>
public record CategorySalesItem
{
    /// <summary>
    /// Gets the product ID.
    /// </summary>
    public int ProductId { get; init; }

    /// <summary>
    /// Gets the product name.
    /// </summary>
    [DisplayName("Product Name")]
    public string? ProductName { get; init; }

    /// <summary>
    /// Gets the category name.
    /// </summary>
    [DisplayName("Category")]
    public string? CategoryName { get; init; }

    /// <summary>
    /// Gets the unit price of the product.
    /// </summary>
    [DisplayName("Unit Price")]
    [DisplayFormat(DataFormatString = "{0:C}")]
    public double UnitPrice { get; init; }

    /// <summary>
    /// Gets the number of units sold.
    /// </summary>
    [DisplayName("Units Sold")]
    [DisplayFormat(DataFormatString = "{0:N0}")]
    public int UnitsSold { get; init; }

    /// <summary>
    /// Gets the total discount amount given for this product.
    /// </summary>
    [DisplayName("Discount Given")]
    [DisplayFormat(DataFormatString = "{0:C}")]
    public double DiscountGiven { get; init; }

    /// <summary>
    /// Gets the total revenue generated by this product.
    /// </summary>
    [DisplayName("Total Revenue")]
    [DisplayFormat(DataFormatString = "{0:C}")]
    public double TotalRevenue { get; init; }
}
