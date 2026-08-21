using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>The export actions a node type offers from its node menu.</summary>
[Flags]
public enum ExportFormats
{
    /// <summary>No export actions.</summary>
    None = 0,
    /// <summary>Export the node as a PDF document.</summary>
    Pdf = 1,
    /// <summary>Export the node as a DOCX document.</summary>
    Docx = 2,
    /// <summary>Send the node to contacts by email.</summary>
    Send = 4,
}

/// <summary>How the export templates compose the node into a document.</summary>
public enum ExportComposition
{
    /// <summary>A single document rendered from the node's own body.</summary>
    Document,
    /// <summary>One chapter/page per slide, in the deck's manifest or query order.</summary>
    SlideDeck,
}

/// <summary>
/// A node type's DECLARATION of its export behaviour — what the Export node-menu group offers
/// and how the export templates compose the document. Set on the type's hub configuration via
/// <see cref="ExportDeclarationExtensions.WithExport"/> (the same
/// <c>MessageHubConfiguration.Set</c> idiom as <see cref="PageLayoutOptions"/>) and read by the
/// ONE generic export menu provider, the export layout area and the export TEMPLATES — replacing
/// the per-type compiled providers and template branches that each hard-coded a NodeType
/// comparison (#1576).
/// <para>Because the declaration travels on the hub configuration, a PLUGIN node type declares
/// its exports in its own configuration lambda — <b>no compiled code needs to know the type, and
/// since #1576 none does</b>. Readers ask the configuration directly
/// (<c>Configuration.Get&lt;ExportDeclaration&gt;()</c>); the transition fallback that briefly
/// bridged plugin decks compiled against an older platform — a suffix-aware
/// <c>DeckNodeType.Matches</c> check — is deleted, because <c>Publish/Deck</c> now declares
/// (MeshWeaver.Plugins#553). A type that declares nothing exports nothing, which is the honest
/// answer rather than a guess made from its name.</para>
/// <para>The export TEMPLATES cannot read a hub configuration — they run from a script request
/// carrying only a source path — so <c>ExportDocumentHandler</c>, which runs on the source node's
/// own hub, stamps <see cref="Composition"/> into the script inputs and the templates dispatch on
/// that. Same declaration, carried to where it is needed.</para>
/// </summary>
public sealed record ExportDeclaration
{
    /// <summary>The export actions offered in the node menu's Export group.</summary>
    public required ExportFormats Formats { get; init; }

    /// <summary>How the export templates compose the document. Default: the node's own body.</summary>
    public ExportComposition Composition { get; init; } = ExportComposition.Document;

    /// <summary>A document node's standard declaration: PDF, Email and DOCX over its own body.</summary>
    public static readonly ExportDeclaration Document = new()
    {
        Formats = ExportFormats.Pdf | ExportFormats.Send | ExportFormats.Docx,
    };

    /// <summary>
    /// A slide deck's declaration: PDF and Email, one page per slide. No DOCX — a deck carries no
    /// markdown body of its own, so DOCX (which renders the node's own content) would be empty.
    /// </summary>
    public static readonly ExportDeclaration SlideDeck = new()
    {
        Formats = ExportFormats.Pdf | ExportFormats.Send,
        Composition = ExportComposition.SlideDeck,
    };
}

/// <summary>Chains an <see cref="ExportDeclaration"/> onto a node type's hub configuration.</summary>
public static class ExportDeclarationExtensions
{
    /// <summary>Declares the export actions and composition for nodes of this hub's type.</summary>
    /// <param name="configuration">The node type's hub configuration.</param>
    /// <param name="declaration">The export declaration (e.g. <see cref="ExportDeclaration.Document"/>).</param>
    public static MessageHubConfiguration WithExport(
        this MessageHubConfiguration configuration, ExportDeclaration declaration)
        => configuration.Set(declaration);
}
