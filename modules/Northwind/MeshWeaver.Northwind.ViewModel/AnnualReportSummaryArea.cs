using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Northwind.ViewModel;

public static class AnnualReportSummaryArea
{
    public static LayoutDefinition AddAnnualReportSummary(this LayoutDefinition layout)
        => 
            layout
                .WithView(nameof(AnnualReportSummary), Controls.Layout.WithView(AnnualReportSummary))
    ;

    public static IObservable<object> AnnualReportSummary(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        var currentYear = layoutArea.Year();
        var previousYear = currentYear - 1;

        return layoutArea.SummaryItems()
                .Select(items => items.ToDictionary(x => x.Year))
                .Select(items =>
                    Controls.LayoutGrid
                        .WithView(
                            Customers(items[currentYear],
                                items.TryGetValue(previousYear, out var item) ? item : null),
                            skin => skin.WithXs(6).WithSm(3))
                        .WithView(ValueBox("Sales", FluentIcons.WalletCreditCard, items[currentYear].Sales,
                                items.TryGetValue(previousYear, out var prev) ? prev.Sales : null, "C"),
                            skin => skin.WithXs(6).WithSm(3)
                        )
                )
            ;
    }

    private static object Customers(SummaryItem current, SummaryItem previous) =>
        Controls.Stack
            .WithSkin(skin => skin.WithOrientation(Orientation.Horizontal))
            .WithView(Controls.Icon(FluentIcons.Person).WithWidth("48px"))
            .WithView(Controls.Stack
                .WithView(Controls.H3("Customers"))
                .WithView(Controls.H1(current.Customers))
                .WithView(previous is not null ? Growth(current.Customers, previous.Customers) : null)
            );

    private static object ValueBox(string title, Icon icon, double current, double? previous, string format) =>
        Controls.Stack
            .WithSkin(skin => skin.WithOrientation(Orientation.Horizontal))
            .WithView(Controls.Icon(icon).WithWidth("48px"))
            .WithView(Controls.Stack
                .WithView(Controls.H3(title))
                .WithView(Controls.H1(current.ToString(format)))
                .WithView(previous is not null ? Growth(current, previous.Value) : null)
            );

    private static object Growth(double current, double previous)
    {
        var delta = current - previous;
        var percentage = delta / previous;
        var sign = delta >= 0 ? "+" : "-";
        var color = delta >= 0 ? "green" : "red";
        return Controls.Html($"<h5 style='color:{color}'>{sign}{percentage:P}</h5>");
    }

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
}

public record SummaryItem
{
    public int Year { get; init; }
    public int Customers { get; init; }
    public int Products { get; init; }
    public double Sales { get; init; }
    public int Orders { get; init; }
}
