using System.Text;
using System.Net;

namespace MeshWeaver.Graph;

/// <summary>
/// Builds the branded HTML shell shared by every first-contact email (access-granted
/// notifications, invitations). For most recipients this email is their FIRST interaction with
/// Memex — before they have ever signed in — so it must read like a real product invitation:
/// a wordmark, a clear heading, the body, a single prominent call-to-action button (with the raw
/// URL shown beneath it, since email clients strip button styling and some users copy-paste), and
/// an optional first-timer sign-in hint. All caller-supplied text is plain and HTML-encoded here;
/// only the CTA URL is trusted (callers build it from a configured base URL + a node path).
/// </summary>
public static class EmailTemplate
{
    private const string Accent = "#2563eb";
    private const string Ink = "#111827";
    private const string Muted = "#6b7280";

    /// <summary>
    /// Renders the email. <paramref name="heading"/> and <paramref name="paragraphs"/> are plain
    /// text (encoded here). A CTA button is emitted only when BOTH <paramref name="ctaLabel"/> and
    /// <paramref name="ctaUrl"/> are set. <paramref name="footerNote"/> is the optional muted line
    /// under the CTA (e.g. the first-time sign-in hint).
    /// </summary>
    public static string Build(
        string heading,
        IReadOnlyList<string> paragraphs,
        string? ctaLabel = null,
        string? ctaUrl = null,
        string? footerNote = null)
    {
        var sb = new StringBuilder();
        sb.Append(
            "<div style=\"margin:0;padding:24px;background:#f3f4f6;\">" +
            "<div style=\"max-width:520px;margin:0 auto;background:#ffffff;border:1px solid #e5e7eb;" +
            "border-radius:12px;overflow:hidden;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;\">" +
            // Wordmark bar
            $"<div style=\"padding:18px 28px;border-bottom:1px solid #f0f0f0;font-weight:700;font-size:16px;color:{Ink};\">" +
            "Memex</div>" +
            "<div style=\"padding:28px;\">");

        sb.Append($"<h1 style=\"margin:0 0 16px 0;font-size:20px;line-height:1.3;color:{Ink};\">{Enc(heading)}</h1>");

        foreach (var p in paragraphs)
            if (!string.IsNullOrWhiteSpace(p))
                sb.Append($"<p style=\"margin:0 0 14px 0;font-size:15px;line-height:1.55;color:{Ink};\">{Enc(p)}</p>");

        if (!string.IsNullOrEmpty(ctaLabel) && !string.IsNullOrWhiteSpace(ctaUrl))
        {
            var url = Enc(ctaUrl!);
            sb.Append(
                $"<div style=\"margin:24px 0 8px 0;\"><a href=\"{url}\" " +
                $"style=\"display:inline-block;background:{Accent};color:#ffffff;padding:12px 22px;" +
                "border-radius:8px;text-decoration:none;font-weight:600;font-size:15px;\">" +
                $"{Enc(ctaLabel)}</a></div>" +
                // Raw URL under the button — survives style-stripping clients and copy-paste.
                $"<p style=\"margin:0 0 4px 0;font-size:12px;color:{Muted};word-break:break-all;\">{url}</p>");
        }

        if (!string.IsNullOrWhiteSpace(footerNote))
            sb.Append($"<p style=\"margin:20px 0 0 0;font-size:13px;line-height:1.5;color:{Muted};\">{Enc(footerNote)}</p>");

        sb.Append(
            "</div></div>" +
            $"<div style=\"max-width:520px;margin:12px auto 0 auto;font-size:11px;color:{Muted};text-align:center;" +
            "font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;\">" +
            "You received this because someone shared content with this email address on Memex." +
            "</div></div>");
        return sb.ToString();
    }

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");
}
