using MeshWeaver.Application.Styles;
using MeshWeaver.Collections;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Utils;

namespace MeshWeaver.Northwind.ViewModel;

/// <summary>
/// Provides configuration methods for the annual report layout.
/// </summary>
public static class AnnualReportConfiguration
{
    /// <summary>
    /// Adds the annual report sections to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the annual report sections will be added.</param>
    /// <returns>The updated layout definition with the annual report sections.</returns>
    public static LayoutDefinition AddAnnualReport(
        this LayoutDefinition layout
    ) => layout
        .AddAnnualReportSummary()
        .AddSalesOverview()
        .AddSalesComparison()
        .AddProductOverview()
        .AddRevenue()
        .AddDiscountSummary()
        .AddDiscountVsRevenue()
        .AddDiscountPercentage()
        .AddEmployeesOverview()
        .AddClientsOverview()
        .AddOrdersOverview()
        .AddSalesOverview()
        .AddAnnualReportMenu()
    ;

    private static LayoutDefinition AddAnnualReportMenu(this LayoutDefinition layout)
        => layout.WithNavMenu
        (
            (menu, _, _) => menu
                .WithNavGroup(
                    AnnualReportDocuments.Aggregate(
                        Controls.NavGroup("Sales Dashboard 2023", FluentIcons.Folder)
                            .WithSkin(skin =>
                                skin.WithHref(layout.DocumentHref(SummaryDocument))
                                .WithExpanded(true)),
                            (navGroup, documentMenuDescriptor) => navGroup.WithLink(Path.GetFileNameWithoutExtension(documentMenuDescriptor.DocumentName).Wordify(),
                                layout.DocumentationPath(ThisAssembly, documentMenuDescriptor.DocumentName), documentMenuDescriptor.Icon))
                )
        );

    private const string SummaryDocument = "AnnualReportSummary.md";

    private static string DocumentHref(this LayoutDefinition layout, string documentName) =>
        layout.DocumentationPath(ThisAssembly, documentName);

    private static IEnumerable<(string DocumentName, Icon Icon)> AnnualReportDocuments =>
        ThisAssembly.GetManifestResourceNames()
            .Where(r => r.StartsWith(DocumentFolder))
            .Select(filePath => filePath.Substring(DocumentFolder.Length + 1))
            .Except(SummaryDocument.RepeatOnce())
            .Select(docName => new { DocumentName = docName, IconAndOrder = GetIconAndOrder(docName), })
            .OrderBy(x => x.IconAndOrder.Order)
            .Select(x => (x.DocumentName, x.IconAndOrder.Icon));

    private static string DocumentFolder =>
        $"{ThisAssembly.GetName().Name}.Markdown.AnnualReport";

    private static readonly IReadOnlyDictionary<string, (Icon, int)> DocumentIcons = new Dictionary<string, (Icon, int)>()
    {
        { "SalesAnalysis.md", (FluentIcons.Money, 10) },
        { "TopSalesRepresentatives.md", (FluentIcons.PersonAccounts, 20) },
        { "TopProductsOverview.md", (FluentIcons.ShoppingBag, 30) },
        { "DiscountsAnalysis.md", (FluentIcons.ShoppingBagPercent, 40) },
        { "OrdersReview.md", (FluentIcons.BoxCheckmark, 50) },
        { "TopClientsOverview.md", (FluentIcons.CreditCardPerson, 60) },
    };

    private static (Icon Icon, int Order) GetIconAndOrder(string documentName)
        => DocumentIcons.TryGetValue(documentName, out var icon) ? icon : (FluentIcons.Document, int.MaxValue);

    private static System.Reflection.Assembly ThisAssembly
        => typeof(AnnualReportConfiguration).Assembly;
}
