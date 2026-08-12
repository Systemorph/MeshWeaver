namespace MeshWeaver.Mesh;

/// <summary>
/// A single file attachment for an outbound email — file name, MIME type, and raw bytes.
/// Produced from the platform's node ⇒ file export pipeline (a rendered <c>RenderedDocument</c>'s
/// bytes) and handed to <see cref="IEmailSender.SendEmail(string,string,string,IReadOnlyCollection{EmailAttachment})"/>.
///
/// <para>Deliberately transport-agnostic: the concrete sender (Microsoft Graph, SMTP, …) maps it to
/// its own attachment shape. Bytes are held in memory — this is for the small, single-artifact
/// "send my deck/document" flow, not a bulk-mail pipeline.</para>
/// </summary>
/// <param name="FileName">Suggested file name including extension (e.g. <c>Pitch Deck.pdf</c>).</param>
/// <param name="MimeType">MIME type of the content (e.g. <c>application/pdf</c>).</param>
/// <param name="Content">Raw file bytes.</param>
/// <param name="ContentId">
/// When set, this part is <b>INLINE</b> rather than a listed attachment: the HTML body references
/// it as <c>&lt;img src="cid:{ContentId}"&gt;</c> and the client draws it in place.
///
/// <para>This is how a picture reliably reaches a reader. The two obvious alternatives both fail
/// in common configurations: a <c>data:</c> URI is stripped by classic Outlook for Windows, and a
/// remote <c>https</c> image is blocked behind "Download pictures" in most clients by default. A
/// content-ID part travels INSIDE the message, so it renders immediately, needs no fetch, leaks no
/// read-receipt beacon, and survives forwarding.</para>
///
/// <para>Null (the default) keeps the original behaviour — an ordinary attachment.</para>
/// </param>
public sealed record EmailAttachment(
    string FileName,
    string MimeType,
    byte[] Content,
    string? ContentId = null)
{
    /// <summary>True when this part is referenced from the body by <c>cid:</c> rather than listed.</summary>
    public bool IsInline => !string.IsNullOrEmpty(ContentId);
}
