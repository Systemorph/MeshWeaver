using MeshWeaver.Application.Styles;
using MeshWeaver.Collections;
using MeshWeaver.Domain;
using MeshWeaver.Domain.Layout.Documentation;
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
                            (navGroup, documentName) => navGroup.WithLink(Path.GetFileNameWithoutExtension(documentName).Wordify(),
                                layout.DocumentationPath(ThisAssembly, documentName), GetIcon(documentName)))
                )
        );

    private const string SummaryDocument = "AnnualReportSummary.md";

    private static string DocumentHref(this LayoutDefinition layout, string documentName) =>
        layout.DocumentationPath(ThisAssembly, documentName);

    private static IEnumerable<string> AnnualReportDocuments =>
        ThisAssembly.GetManifestResourceNames()
            .Where(r => r.StartsWith(DocumentFolder))
            .Select(filePath => filePath.Substring(DocumentFolder.Length + 1))
            .Except(SummaryDocument.RepeatOnce())
            .OrderBy(x => x);

    private static string DocumentFolder =>
        $"{ThisAssembly.GetName().Name}.Markdown.AnnualReport";

    private static readonly IReadOnlyDictionary<string, Icon> DocumentIcons = new Dictionary<string, Icon>()
    {
        { "TopClientsOverview.md", FluentIcons.CreditCardPerson },
        { "DiscountsAnalysis.md", FluentIcons.ShoppingBagPercent },
        { "TopSalesRepresentatives.md", FluentIcons.PersonAccounts },
        { "OrdersReview.md", FluentIcons.BoxCheckmark },
        { "TopProductsOverview.md", FluentIcons.ShoppingBag },
        { "SalesAnalysis.md", FluentIcons.Money },
    };

    private static Icon GetIcon(string documentName)
        => DocumentIcons.TryGetValue(documentName, out var icon) ? icon : FluentIcons.Document;

    private static System.Reflection.Assembly ThisAssembly
        => typeof(AnnualReportConfiguration).Assembly;
}
