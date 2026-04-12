using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Handlers;
using MeshWeaver.Markdown.Export.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Markdown.Export.Configuration;

/// <summary>
/// Fluent extension methods that register the full markdown-export pipeline
/// (corporate identity node type, menu items, layout areas, request handler).
/// </summary>
public static class MarkdownExportExtensions
{
    /// <summary>
    /// Registers the <c>CorporateIdentity</c> node type on the mesh builder.
    /// </summary>
    public static TBuilder AddCorporateIdentityType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CorporateIdentityNodeType.CreateMeshNode());
        return builder;
    }

    /// <summary>
    /// Registers everything the markdown-export feature needs on the mesh builder:
    /// the CorporateIdentity node type, the request/response hub handler, menu items,
    /// and the dialog layout areas.
    /// </summary>
    public static TBuilder AddMarkdownExport<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        // Accept QuestPDF's Community License once per process. Safe to call repeatedly.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        builder.AddCorporateIdentityType();

        builder.ConfigureServices(services =>
            services.AddTransient<BrandingResolver>());

        builder.ConfigureHub(hub => hub
            .AddExportDocumentHandler()
            .WithTypes(typeof(CorporateIdentity), typeof(ExportDocumentControl))
            .AddNodeMenuItems(MarkdownExportMenuProvider.Provide)
            .AddLayout(layout => layout
                .WithView(ExportDocumentLayoutArea.PdfArea, ExportDocumentLayoutArea.RenderPdf)
                .WithView(ExportDocumentLayoutArea.DocxArea, ExportDocumentLayoutArea.RenderDocx)));

        return builder;
    }
}
