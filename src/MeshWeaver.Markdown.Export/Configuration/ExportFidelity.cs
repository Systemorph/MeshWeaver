using System.ComponentModel;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown.Export.Configuration;

/// <summary>
/// How faithfully an export reproduces what the browser shows.
///
/// <para>The two modes are complements, not a migration: <see cref="Content"/> is the default
/// because for most documents it is the better artifact (structured, text-selectable, and the only
/// one that carries a cover page, a table of contents and a running header), and
/// <see cref="Pixel"/> exists for the specific class of deck whose meaning is carried by CSS the
/// document model cannot express.</para>
///
/// <para>Since #1230 <b>both</b> modes print with the headless browser that ships in the portal
/// image; what differs is the document handed to it. Content-faithful composes the markdown AST
/// into a structured, branded print document; pixel-faithful composes the deck's own live stage.
/// The distinction is fidelity, no longer "browser or no browser".</para>
/// </summary>
public enum ExportFidelity
{
    /// <summary>
    /// Content-faithful (default). The markdown AST is reconstructed into a document model and
    /// composed into a structured print document — headings, lists, tables, links, plus the cover
    /// page, table of contents and running header/footer. Text is selectable and the file is
    /// small, but anything whose appearance depends on the author's own CSS (gradients and image
    /// backgrounds, raw-HTML slide bodies, CSS layout, transforms, web fonts) does not survive.
    /// </summary>
    [Description("Content-faithful — selectable text, cover page and contents")]
    [Translation("de", "Inhaltstreu – markierbarer Text, Deckblatt und Inhaltsverzeichnis")]
    Content,

    /// <summary>
    /// Pixel-faithful. The deck's slides are composed into a self-contained HTML document that
    /// carries the SAME stage CSS the live Slide view uses, and a headless browser prints it to
    /// PDF — so gradients, image backgrounds, raw HTML, CSS layout and web fonts land exactly as
    /// on screen. Requires a headless Chromium to be configured on the server; when none is
    /// available the option is not offered.
    /// </summary>
    [Description("Pixel-faithful — renders gradients, HTML and CSS exactly as on screen")]
    [Translation("de", "Pixelgetreu – Verläufe, HTML und CSS exakt wie am Bildschirm")]
    Pixel
}
