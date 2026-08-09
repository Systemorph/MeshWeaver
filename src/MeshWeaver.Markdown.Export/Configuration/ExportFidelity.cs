using System.ComponentModel;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown.Export.Configuration;

/// <summary>
/// How faithfully an export reproduces what the browser shows.
///
/// <para>The two modes are complements, not a migration: <see cref="Content"/> is the default
/// because for most documents it is the better artifact (small, fast, text-selectable, no browser
/// in the image), and <see cref="Pixel"/> exists for the specific class of deck whose meaning is
/// carried by CSS the document model cannot express.</para>
/// </summary>
public enum ExportFidelity
{
    /// <summary>
    /// Content-faithful (default). The markdown AST is reconstructed into a PDF/DOCX document
    /// model (QuestPDF / OpenXml). Text is selectable, the file is small, generation is fast and
    /// needs no browser — but anything whose appearance depends on browser rendering (CSS
    /// gradients and image backgrounds, raw-HTML slide bodies, CSS layout, transforms, web fonts)
    /// does not survive.
    /// </summary>
    [Description("Content-faithful — selectable text, no browser required")]
    [Translation("de", "Inhaltstreu – markierbarer Text, kein Browser erforderlich")]
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
