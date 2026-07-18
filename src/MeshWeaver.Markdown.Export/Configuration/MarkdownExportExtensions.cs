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
        //
        // ExportDocumentControl ALSO has to be mesh-wide: the per-node hub renders it as a
        // UiControl inside a layout-area DataChangedEvent. The routing layer between the silo
        // hub and the client client/portal hub serialises the polymorphic UiControl through
        // the mesh-wide type registry. Without ExportDocumentControl registered there, the
        // routing serialiser can't resolve the $type discriminator and the response is
        // silently dropped — SubscribeRequest never gets a reply, the client times out at
        // 30s. Local-only `WithTypes` on the per-node hub is not enough.
        builder
            .WithMeshType(typeof(ExportDocumentRequest), nameof(ExportDocumentRequest))
            .WithMeshType(typeof(ExportDocumentResponse), nameof(ExportDocumentResponse))
            .WithMeshType(typeof(DocumentExportOptions), nameof(DocumentExportOptions))
            .WithMeshType(typeof(ExportDocumentControl), nameof(ExportDocumentControl));

        builder.ConfigureServices(services => services
            .AddSingleton(cfg)
            .AddTransient<ExportTemplateResolver>()
            .AddTransient<BrandingResolver>()
            // Make this assembly visible to kernel scripts. Without this the
            // export template .csx files can't resolve `using MeshWeaver.Markdown.Export.*`
            // when AppDomain hasn't eagerly loaded the assembly before the
            // first script run. See KernelScriptAssembly.
            .AddSingleton(new MeshWeaver.Kernel.Hub.KernelScriptAssembly(
                typeof(MarkdownExportTemplates).Assembly)));

        // Seed the built-in PDF/DOCX template Code MeshNodes at
        // Templates/Export/{Pdf,Docx}. Layout areas drive export by posting
        // ExecuteScriptRequest at these nodes — the kernel runs the embedded
        // .csx with caller-supplied Inputs and writes progress / output to an
        // Activity in the caller's home. See Doc/Architecture/ActivityControlPlane.md
        // → "Operations as scripts". Stateless static helper, no DI provider.
        builder.AddMeshNodes(MarkdownExportTemplates.GetStaticNodes());

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
                // Deck nodes get a PDF export item (one page per slide) — self-gated on NodeType=Deck.
                services.TryAddEnumerable(
                    ServiceDescriptor.Scoped<INodeMenuProvider, DeckExportMenuProvider>());
                return services;
            })
            .AddLayout(layout => layout
                .WithView(ExportDocumentLayoutArea.PdfArea, ExportDocumentLayoutArea.RenderPdf)
                .WithView(ExportDocumentLayoutArea.DocxArea, ExportDocumentLayoutArea.RenderDocx)));

        return builder;
    }
}
