using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Handlers;
using MeshWeaver.Markdown.Export.Layout;
using MeshWeaver.Domain;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Markdown.Export.Configuration;

/// <summary>
/// Fluent extension methods that register the full markdown-export pipeline
/// (corporate identity node type, menu items, layout areas, request handler).
/// </summary>
public static class MarkdownExportExtensions
{
    /// <summary>
    /// Registers the markdown-export messaging types on a hub type registry. Call this on any
    /// hub (mesh, node, client) that sends or receives <see cref="ExportDocumentRequest"/> or
    /// <see cref="ExportDocumentResponse"/>. Uses short names (<c>nameof</c>) so the <c>$type</c>
    /// discriminator matches across hub boundaries — same convention as <c>AddAITypes</c>.
    /// </summary>
    public static ITypeRegistry AddMarkdownExportTypes(this ITypeRegistry typeRegistry)
        => typeRegistry
            .WithType(typeof(ExportDocumentRequest), nameof(ExportDocumentRequest))
            .WithType(typeof(ExportDocumentResponse), nameof(ExportDocumentResponse))
            .WithType(typeof(DocumentExportOptions), nameof(DocumentExportOptions))
            .WithType(typeof(CorporateIdentity), nameof(CorporateIdentity))
            .WithType(typeof(ExportDocumentControl), nameof(ExportDocumentControl));

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
    /// <remarks>
    /// Use the <paramref name="configure"/> callback to pick the target content collection
    /// and sub-directory, and whether to overwrite existing files.
    /// </remarks>
    public static TBuilder AddMarkdownExport<TBuilder>(
        this TBuilder builder,
        Action<MarkdownExportConfig>? configure = null)
        where TBuilder : MeshBuilder
    {
        // Accept QuestPDF's Community License once per process. Safe to call repeatedly.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        var cfg = new MarkdownExportConfig();
        configure?.Invoke(cfg);

        builder.AddCorporateIdentityType();

        // Register the request/response on the mesh-wide type registry so every hub (mesh, node,
        // client) can serialize/deserialize them with a consistent $type discriminator. Without
        // this the client hub receives a MessageDelivery<JsonElement> that can't be cast to
        // IMessageDelivery<ExportDocumentResponse>.
        builder
            .WithMeshType(typeof(ExportDocumentRequest), nameof(ExportDocumentRequest))
            .WithMeshType(typeof(ExportDocumentResponse), nameof(ExportDocumentResponse))
            .WithMeshType(typeof(DocumentExportOptions), nameof(DocumentExportOptions));

        builder.ConfigureServices(services => services
            .AddSingleton(cfg)
            .AddTransient<ExportTemplateResolver>()
            .AddTransient<BrandingResolver>());

        // Menu items, layout views, and the export request handler must live on the
        // node hubs (one per Markdown node) — that's where layout rendering runs and where
        // the user's click navigates. Registering on the mesh hub via ConfigureHub would
        // never surface the items to the per-node menu.
        // Provider registered via TryAddEnumerable — DI guarantees exactly one instance per hub
        // (same pattern as IAutocompleteProvider). Layout views + request handler still need
        // the per-node-hub registration so clicks land on a hub that can render the export dialog.
        // AddExportDocumentHandler registers the request/response + handler on the node hub.
        // Layout views + the DI-scoped menu provider also belong on per-node hubs.
        // The cross-hub request/response types are already registered mesh-wide via WithMeshType above.
        builder.ConfigureDefaultNodeHub(hub => hub
            .AddExportDocumentHandler()
            .WithTypes(typeof(CorporateIdentity), typeof(ExportDocumentControl))
            .WithServices(services =>
            {
                services.TryAddEnumerable(
                    ServiceDescriptor.Scoped<INodeMenuProvider, MarkdownExportMenuProvider>());
                return services;
            })
            .AddLayout(layout => layout
                .WithView(ExportDocumentLayoutArea.PdfArea, ExportDocumentLayoutArea.RenderPdf)
                .WithView(ExportDocumentLayoutArea.DocxArea, ExportDocumentLayoutArea.RenderDocx)));

        return builder;
    }
}
