using System.Reactive.Linq;
using OpenSmc.Application.Styles;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Utils;

namespace OpenSmc.Northwind.ViewModel;

/// <summary>
/// Provides a static view for customer summaries within the Northwind application.
/// </summary>
public static class CustomerSummaryArea
{
    /// <summary>
    /// Registers the customer summary.
    /// </summary>
    /// <param name="layout"></param>
    /// <returns></returns>
    public static LayoutDefinition AddCustomerSummary(this LayoutDefinition layout)
        => layout.WithView(nameof(CustomerSummary), CustomerSummary, x => x
            .WithMenu(Controls.NavLink(nameof(CustomerSummary).Wordify(), FluentIcons.Person,
                layout.ToHref(new(nameof(CustomerSummary)))))
        );

    /// <summary>
    /// Generates the customer summary view.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="ctx">The rendering context.</param>
    /// <returns>A layout stack control representing the customer summary.</returns>
    public static LayoutStackControl CustomerSummary(
        this LayoutAreaHost layoutArea,
        RenderingContext ctx
    ) =>
        Controls.Stack()
            .WithView(Controls.PaneHeader("Customer Summary"))
            .WithView(
                (a, _) =>
                    a.GetDataStream<Toolbar>(nameof(Toolbar))
                        .Select(tb => $"Year selected: {tb.Year}")
            );
}
