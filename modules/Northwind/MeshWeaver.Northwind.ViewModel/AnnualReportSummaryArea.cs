using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using static MeshWeaver.Northwind.ViewModel.LayoutTemplates;

namespace MeshWeaver.Northwind.ViewModel;

public static class AnnualReportSummaryArea
{
    public static LayoutDefinition AddAnnualReportSummary(this LayoutDefinition layout)
        => 
            layout
                .WithView(nameof(AnnualReportSummary), Controls.Layout.WithView(AnnualReportSummary))
    ;

    public static object AnnualReportSummary(this LayoutAreaHost layoutArea, RenderingContext context)
        => Controls.LayoutGrid
            .WithView(ValueBoxes, skin => skin.WithXs(12))
            .WithView(SummaryCharts, skin => skin.WithXs(12))
            .WithSkin(skin => skin.WithSpacing(5))
        ;

    private static IObservable<object> ValueBoxes(LayoutAreaHost layoutArea, RenderingContext context)
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

    public static object SummaryCharts(LayoutAreaHost layoutArea, RenderingContext context)
        => Controls.LayoutGrid
            .WithView(
                Controls.Stack.WithView(Controls.H2("Sales by category")).WithView("Coming soon...")
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

    private static object Customers(SummaryItem current, SummaryItem previous) =>
        ValueBox(
            "Customers",
            FluentIcons.Person,
            current.Customers.ToSuffixFormat(),
            previous is not null ? GrowthPercentage(current.Customers, previous.Customers) : null
        );

    private static object Sales(SummaryItem current, SummaryItem previous) =>
        ValueBox(
            "Sales",
            FluentIcons.WalletCreditCard,
            "$" + current.Sales.ToSuffixFormat(),
            previous is not null ? GrowthPercentage(current.Sales, previous.Sales) : null
        );

    private static object Products(SummaryItem current, SummaryItem previous) =>
        ValueBox(
            "Products",
            FluentIcons.Box,
            current.Products.ToSuffixFormat(),
            previous is not null ? GrowthPercentage(current.Products, previous.Products) : null
        );

    private static object Orders(SummaryItem current, SummaryItem previous) =>
        ValueBox(
            "Orders",
            FluentIcons.Checkmark,
            current.Orders.ToSuffixFormat(),
            previous is not null ? GrowthPercentage(current.Orders, previous.Orders) : null
        );

}

public record SummaryItem
{
    public int Year { get; init; }
    public int Customers { get; init; }
    public int Products { get; init; }
    public double Sales { get; init; }
    public int Orders { get; init; }
}
