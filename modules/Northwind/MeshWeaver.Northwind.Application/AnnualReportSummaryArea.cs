using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using static MeshWeaver.Northwind.Application.LayoutTemplates;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Provides methods to add and render the annual report summary area.
/// </summary>
public static class AnnualReportSummaryArea
{
    /// <summary>
    /// Adds the annual report summary view to the layout definition.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <returns>The updated layout definition with the annual report summary view.</returns>
    public static LayoutDefinition AddAnnualReportSummary(this LayoutDefinition layout)
        => 
            layout
                .WithView(nameof(AnnualReportSummary), AnnualReportSummary, area => area.WithCategory("Dashboard"))
            ;

    /// <summary>
    /// Renders the annual report summary area.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An object representing the rendered annual report summary area.</returns>
    public static UiControl AnnualReportSummary(this LayoutAreaHost layoutArea, RenderingContext context)
        => Controls.LayoutGrid
            .WithView(ValueBoxes, skin => skin.WithXs(12))
            .WithView(SummaryCharts, skin => skin.WithXs(12))
            .WithSkin(skin => skin.WithSpacing(5))
        ;

    /// <summary>
    /// Creates an observable sequence of value boxes for the annual report summary.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of value boxes.</returns>
    private static IObservable<UiControl> ValueBoxes(LayoutAreaHost layoutArea, RenderingContext context)
    {
        var currentYear = layoutArea.Year();
        var previousYear = currentYear - 1;
        return layoutArea.SummaryItems()
                .Select(items => items.ToDictionary(x => x.Year))
                .Select(items =>
                {
                    var current = items[currentYear];
                    var previous = items.TryGetValue(previousYear, out var item) ? item : null;

                    return Controls.LayoutGrid
                        .WithView(Sales(current, previous), skin => skin.WithXs(6).WithSm(3))
                        .WithView(Products(current, previous), skin => skin.WithXs(6).WithSm(3))
                        .WithView(Orders(current, previous), skin => skin.WithXs(6).WithSm(3))
                        .WithView(Customers(current, previous), skin => skin.WithXs(6).WithSm(3))
                        ;
                })
            ;
    }

    /// <summary>
    /// Renders the summary charts for the annual report.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An object representing the rendered summary charts.</returns>
    public static UiControl SummaryCharts(LayoutAreaHost layoutArea, RenderingContext context)
        => Controls.LayoutGrid
            .WithView(
                Controls.Stack.WithView(Controls.H2("Top Clients")).WithView(ClientsOverviewArea.TopClients)
                    .WithVerticalGap(10),
                skin => skin.WithXs(12).WithSm(6))
            .WithView(
                Controls.Stack.WithView(Controls.H2("Top products")).WithView(TopProductsArea.TopProducts)
                    .WithVerticalGap(10),
                skin => skin.WithXs(12).WithSm(6))
            ;

    private static IObservable<IReadOnlyCollection<SummaryItem>> SummaryItems(this LayoutAreaHost layoutArea)
        => layoutArea.GetNorthwindDataCubeData()
            .Select(d => 
                d.GroupBy(x => x.OrderYear)
                    .Select(g => new SummaryItem
                    {
                        Year = g.Key,
                        Customers = g.Select(x => x.Customer).Distinct().Count(),
                        Products = g.Sum(x => x.Quantity),
                        Sales = g.Sum(x => x.Amount),
                        Orders = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .ToArray()
            )
        ;

    private static UiControl Customers(SummaryItem current, SummaryItem? previous) =>
        ValueBox(
            "Customers",
            FluentIcons.Person(),
            current.Customers.ToSuffixFormat(),
            previous is not null ? GrowthPercentage(current.Customers, previous.Customers) : null
        );

    private static UiControl Sales(SummaryItem current, SummaryItem? previous) =>
        ValueBox(
            "Sales",
            FluentIcons.WalletCreditCard(),
            "$" + current.Sales.ToSuffixFormat(),
            previous is not null ? GrowthPercentage(current.Sales, previous.Sales) : null
        );

    private static UiControl Products(SummaryItem current, SummaryItem? previous) =>
        ValueBox(
            "Products",
            FluentIcons.Box(),
            current.Products.ToSuffixFormat(),
            previous is not null ? GrowthPercentage(current.Products, previous.Products) : null
        );

    private static UiControl Orders(SummaryItem current, SummaryItem? previous) =>
        ValueBox(
            "Orders",
            FluentIcons.Checkmark(),
            current.Orders.ToSuffixFormat(),
            previous is not null ? GrowthPercentage(current.Orders, previous.Orders) : null
        );
}

/// <summary>
/// Represents a summary item for the annual report.
/// </summary>
public record SummaryItem
{
    /// <summary>
    /// Gets the year of the summary item.
    /// </summary>
    public int Year { get; init; }
     /// <summary>
    /// Gets the number of customers in the summary item.
    /// </summary>
    public int Customers { get; init; }
    /// <summary>
    /// Gets the number of products in the summary item.
    /// </summary>
    public int Products { get; init; }
    /// <summary>
    /// Gets the total sales in the summary item.
    /// </summary>
    public double Sales { get; init; }
    /// <summary>
    /// Gets the number of orders in the summary item.
    /// </summary>
    public int Orders { get; init; }
}
