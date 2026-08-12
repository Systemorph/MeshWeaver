namespace MeshWeaver.Markdown.Export.Configuration;

/// <summary>
/// How a sent document reaches the recipient's inbox.
/// </summary>
public enum DocumentDelivery
{
    /// <summary>
    /// The document is exported to a file and attached; the email body carries only the sender's
    /// covering note. The original behaviour, and still the right choice for a PDF meant to be
    /// filed or printed.
    /// </summary>
    Attachment,

    /// <summary>
    /// The document IS the email body — rendered as email-safe HTML with its embedded layout
    /// areas resolved, so the recipient reads it in the message itself with nothing to open and
    /// no attachment to be stripped by a mail gateway.
    /// </summary>
    EmailBody
}
