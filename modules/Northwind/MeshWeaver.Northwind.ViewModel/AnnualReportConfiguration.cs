using MeshWeaver.Application.Styles;
using MeshWeaver.Collections;
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
                        Controls.NavGroup("Financial Report 2023", FluentIcons.Folder)
                            .WithSkin(skin => 
                                skin.WithHref(layout.DocumentHref(SummaryDocument))
                                .WithExpanded(true)), 
                            (navGroup, documentName) => navGroup.WithLink(Path.GetFileNameWithoutExtension(documentName).Wordify(), 
                                layout.DocumentationPath(ThisAssembly, documentName), FluentIcons.Document))
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

    private static System.Reflection.Assembly ThisAssembly 
        => typeof(AnnualReportConfiguration).Assembly;
}
