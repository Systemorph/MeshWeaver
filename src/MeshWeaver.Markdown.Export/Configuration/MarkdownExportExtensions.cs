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
    // AddMarkdownExportTypes moved to MeshWeaver.Markdown.Export.Contract
    // (MarkdownExportTypeRegistration) so a caller that only SENDS an export request need not
    // reference the rendering engine. Same namespace, so every existing `using
    // MeshWeaver.Markdown.Export.Configuration;` call site is unchanged.

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
            .AddSingleton(cfg.PixelRendering)
            // The browser leaf: a mesh-scoped singleton so its promise-cached probe lives (and
            // dies) with the mesh — never static, never bleeding across test meshes. Since #1230
            // it backs BOTH fidelities: the content-faithful PDF prints a composed document with
            // it, and the pixel-faithful deck export prints the live stage with it.
            .AddSingleton<Pixel.IPixelPdfRenderer, Pixel.HeadlessChromiumPdfRenderer>()
            // The content-faithful PDF renderer is a thin, stateless composition over that leaf;
            // registering it keeps the export script free of construction details.
            .AddSingleton<Pdf.PdfDocumentRenderer>()
            .AddTransient<ExportTemplateResolver>()
            .AddTransient<BrandingResolver>()
            // Make this assembly visible to kernel scripts. Without this the
            // export template .csx files can't resolve `using MeshWeaver.Markdown.Export.*`
            // when AppDomain hasn't eagerly loaded the assembly before the
            // first script run. See KernelScriptAssembly.
            .AddSingleton(new MeshWeaver.Kernel.Hub.KernelScriptAssembly(
                typeof(MarkdownExportTemplates).Assembly))
            // 🚨 …and the CONTRACT assembly, which is a SEPARATE registration because
            // KernelScriptAssembly is per-assembly. The .csx templates reference both halves —
            // DocumentExportOptions / ExportFormat / RenderedDocument live here, DocumentBuilder /
            // PdfDocumentRenderer live in the engine — and the two now ship as different
            // assemblies. Dropping this line compiles clean and fails only at RUNTIME, on the
            // first export, with CS0246 from inside the script: the exact failure mode
            // ExportTemplateCompilationTest exists to catch.
            .AddSingleton(new MeshWeaver.Kernel.Hub.KernelScriptAssembly(
                typeof(Messaging.RenderedDocument).Assembly)));

        // Seed the built-in PDF/DOCX template Code MeshNodes at
        // Templates/Export/{Pdf,Docx}. Layout areas drive export by posting
        // ExecuteScriptRequest at these nodes — the kernel runs the embedded
        // .csx with caller-supplied Inputs and writes progress / output to an
        // Activity in the caller's home. See Doc/Architecture/ActivityControlPlane.md
        // → "Operations as scripts". Stateless static helper, no DI provider.
        builder.AddMeshNodes(MarkdownExportTemplates.GetStaticNodes());

        // …and the access grant that lets an ordinary (non-admin) user actually RUN them.
        // ExecuteScriptRequest is gated on Permission.Execute on the template's own path, so
        // without this every non-admin's export failed "lacks Execute permission on
        // Templates/Export/Pdf" (issue #423 — the reopen reason). Seeded here, next to the nodes
        // it guards, because those nodes are in-memory statics that never reach Postgres — so a
        // migration could not cover them. IfAbsent: AddGraph() seeds the same partition-level
        // grant, and either call alone must land it exactly once.
        builder.AddMeshNodesIfAbsent(ScriptTemplates.PublicExecuteGrant());

        // The per-user compose draft that makes the share dialog survive a full page navigation
        // (the Microsoft 365 consent round trip). See EmailDraftNodeType for why it is filed under
        // the AUTHOR rather than under the document.
        builder.AddEmailDraftType();

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
                .WithView(ExportDocumentLayoutArea.DocxArea, ExportDocumentLayoutArea.RenderDocx)
                .WithView(ExportDocumentLayoutArea.HtmlArea, ExportDocumentLayoutArea.RenderHtml)
                .WithView(SendDocumentLayoutArea.SendArea, SendDocumentLayoutArea.RenderSend)));

        return builder;
    }
}
