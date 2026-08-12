using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Messaging;

namespace MeshWeaver.Markdown.Export.Configuration;

/// <summary>
/// Type-registry registration for the document-export wire contract.
///
/// <para>Lives in <c>MeshWeaver.Markdown.Export.Contract</c> rather than next to
/// <c>AddMarkdownExport</c> so that a caller which only SENDS an export request — the Blazor
/// dialog, a JS client's server counterpart, a test hub — can register the types without
/// referencing the rendering engine (and, through it, Markdig / OpenXml / HtmlAgilityPack).
/// The engine's <c>AddMarkdownExport</c> still calls this, so nothing needs to know the split
/// exists.</para>
/// </summary>
public static class MarkdownExportTypeRegistration
{
    /// <summary>
    /// Registers the markdown-export messaging types on a hub type registry. Call this on any
    /// hub (mesh, node, client) that sends or receives <see cref="ExportDocumentRequest"/> or
    /// <see cref="ExportDocumentResponse"/>. Uses short names (<c>nameof</c>) so the <c>$type</c>
    /// discriminator matches across hub boundaries — same convention as <c>AddAITypes</c>.
    ///
    /// <para>🚨 The <c>nameof</c> discriminators are a WIRE contract shared with the React,
    /// React Native and portal-next clients, which post the literal string
    /// <c>"ExportDocumentRequest"</c>. They are keyed off the type NAME, not its namespace or
    /// assembly, so moving these types between assemblies is invisible to those clients —
    /// renaming one is not.</para>
    /// </summary>
    public static ITypeRegistry AddMarkdownExportTypes(this ITypeRegistry typeRegistry)
        => typeRegistry
            .WithType(typeof(ExportDocumentRequest), nameof(ExportDocumentRequest))
            .WithType(typeof(ExportDocumentResponse), nameof(ExportDocumentResponse))
            .WithType(typeof(DocumentExportOptions), nameof(DocumentExportOptions))
            .WithType(typeof(CorporateIdentity), nameof(CorporateIdentity))
            .WithType(typeof(ExportDocumentControl), nameof(ExportDocumentControl));
}
