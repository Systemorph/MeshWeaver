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
public sealed record EmailAttachment(string FileName, string MimeType, byte[] Content);
