using MeshWeaver.Application.Styles;
using MeshWeaver.Documentation;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Utils;

namespace MeshWeaver.Northwind.ViewModel;

public static class AnnualReportConfiguration
{
    public static LayoutDefinition AddAnnualReport(
        this LayoutDefinition layout
    ) => layout
        .AddSalesOverview()
        .AddSalesComparison()
        .AddProductOverview()
        .AddRevenue()
        .AddDiscountSummary()
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
                        Controls.NavGroup("Annual Report 2023", FluentIcons.Folder)
                            .WithSkin(skin => skin.WithExpanded(true)), 
                            (navGroup, documentName) => navGroup.WithNavLink(Path.GetFileNameWithoutExtension(documentName).Wordify(), 
                                layout.DocumentationPath(ThisAssembly, documentName), FluentIcons.Document))
                )
        );


    private static IEnumerable<string> AnnualReportDocuments =>
        ThisAssembly.GetManifestResourceNames()
            .Where(r => r.StartsWith(DocumentFolder))
            .Select(filePath => filePath.Substring(DocumentFolder.Length + 1))
            .OrderBy(x => x);

    private static string DocumentFolder =>
        $"{ThisAssembly.GetName().Name}.Markdown.AnnualReport";

    private static System.Reflection.Assembly ThisAssembly 
        => typeof(AnnualReportConfiguration).Assembly;
}
