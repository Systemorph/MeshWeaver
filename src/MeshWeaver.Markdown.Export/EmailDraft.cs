using System.ComponentModel;

namespace MeshWeaver.Markdown.Export;

/// <summary>
/// A half-written "Share ⇒ as email" compose form, persisted as a mesh node so that leaving the
/// page cannot destroy it.
///
/// <para><b>Why this type exists.</b> The compose form used to live only in the layout area's
/// <c>/data</c> store — i.e. in Blazor circuit memory. Connecting Microsoft 365 is a server-side
/// endpoint, so the connect button navigates with <c>forceLoad: true</c>; that tears the circuit
/// down, and the user came back from consent to a form that had silently emptied itself. Persisting
/// the in-progress state as a node makes the round trip irrelevant: the form re-reads its own
/// state, because the state was never in the circuit to begin with.</para>
///
/// <para><b>The field names are the form's binding contract.</b> Each property is addressed by the
/// matching camelCase <c>JsonPointerReference</c> in
/// <c>SendDocumentLayoutArea.BuildSendForm</c>; renaming one silently unbinds that field.</para>
/// </summary>
public record EmailDraft
{
    /// <summary>The raw recipient address — the primary field of the form.</summary>
    [Description("Recipient email address")]
    public string Email { get; init; } = "";

    /// <summary>PATH of a picked mesh <c>User</c> node, when the user picked one instead of typing
    /// an address. The dispatcher resolves it to an address at send time.</summary>
    [Description("Recipient mesh user")]
    public string Recipient { get; init; } = "";

    /// <summary>Mail subject.</summary>
    [Description("Subject")]
    public string Subject { get; init; } = "";

    /// <summary>The personal message that precedes the document in the mail body.</summary>
    [Description("Message")]
    public string Message { get; init; } = "";

    /// <summary>
    /// How the document travels — <c>body</c> or <c>attachment</c>. Kept as the raw form value
    /// rather than an enum so the existing <c>RadioGroupControl</c> option contract is unchanged.
    /// </summary>
    [Description("Delivery")]
    public string Delivery { get; init; } = "body";

    /// <summary>
    /// The document this draft composes a mail for. Recorded for provenance and so an orphaned
    /// draft can be traced back to its subject; the binding key is the node path itself.
    /// </summary>
    [Browsable(false)]
    public string DocumentPath { get; init; } = "";
}
