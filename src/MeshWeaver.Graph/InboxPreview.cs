using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MeshWeaver.Graph;

/// <summary>
/// The inbox row's PREVIEW line — the first line of what the item says, under its subject: the reply it
/// quotes and the signature cut off. Pure functions of what the source already handed over.
///
/// <para>🚨 The text is what the source's OWN listing carries (the content's <c>preview</c>): Graph's
/// <c>bodyPreview</c>, Gmail's <c>snippet</c>, the last message of a chat. Nothing fetches a body to
/// make one, and nothing is stored for it: a live source still reads its provider live, and the
/// preview rides on the item it already returned.</para>
/// </summary>
public static partial class InboxPreview
{
    /// <summary>The longest preview shown, in characters; longer text is cut at a word and marked with an ellipsis.</summary>
    public const int MaxLength = 160;

    /// <summary>
    /// The preview line of <paramref name="text"/>: the reply quoted below the new text, a signature
    /// and the forwarded-message header removed, zero-width and attachment placeholder characters
    /// dropped (zero-width joiners stay — they hold an emoji or a word together), whitespace collapsed to single spaces, and cut at a word to <see cref="MaxLength"/>.
    /// Empty when nothing is left. Cheap: line scans and one regex per line, never a parse of HTML.
    /// </summary>
    /// <param name="text">The source's preview text (plain text), or null.</param>
    public static string Of(string? text) => Cut(Clean(text));

    /// <summary>
    /// <paramref name="text"/> as the new text of the message on one line — <see cref="Of"/> without the
    /// length cut, for a caller that applies its own (<see cref="InboxRows.PreviewOf"/>).
    /// </summary>
    /// <param name="text">The source's preview text (plain text), or null.</param>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var kept = new StringBuilder();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            // Everything from here down is the conversation it answers, or the sender's signature.
            if (EndsTheNewText(line))
                break;
            // A quoted line inside the text (a reply quoting inline) is not what this message says.
            if (line.StartsWith('>'))
                continue;
            if (line.Length > 0)
                kept.Append(line).Append(' ');
        }
        var collapsed = CutInline(Whitespace().Replace(Invisible(kept.ToString()), " ").Trim());
        // A preview that was ALL quote (a bare forward) says what the QUOTED lines say rather than
        // nothing — never a signature, which was cut on purpose: "-- \nJane" stays empty.
        if (collapsed.Length == 0)
            collapsed = Whitespace().Replace(Invisible(string.Join(' ', lines
                .Select(l => l.Trim()).Where(l => l.StartsWith('>')).Select(l => l.TrimStart('>', ' ')))), " ").Trim();
        return collapsed;
    }

    /// <summary>Whether <paramref name="line"/> starts what is NOT the new text: a quoted reply's header, a forward's header, or a signature.</summary>
    internal static bool EndsTheNewText(string line)
        => line is "--" or "-- " or "__"
           || line.StartsWith("-----Original Message-----", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("---------- Forwarded message", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("-----Ursprüngliche Nachricht-----", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("Sent from my ", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("Get Outlook for ", StringComparison.OrdinalIgnoreCase)
           || LineSignature().IsMatch(line)
           || QuoteHeader().IsMatch(line)
           || OutlookHeader().IsMatch(line);

    /// <summary>
    /// <paramref name="text"/> up to the first quoted-reply marker INSIDE it. A source that already
    /// collapsed its preview to one line (Graph's <c>bodyPreview</c>) carries the reply header mid-line
    /// ("Thanks! From: Jane Roe Sent: Monday …"), where the line scan cannot see it. A marker at the
    /// very start keeps the text: there is nothing before it to show instead.
    /// </summary>
    internal static string CutInline(string text)
    {
        var match = InlineMarker().Match(text);
        return match.Success && match.Index > 0 ? text[..match.Index].TrimEnd() : text;
    }

    private static string Invisible(string text)
        => new([.. text.Where(c => c is not ('\u200B' or '\uFEFF' or '\uFFFC' or '\u034F' or '\u00AD'))]);

    private static string Cut(string text)
    {
        if (text.Length <= MaxLength)
            return text;
        var cut = text.LastIndexOf(' ', MaxLength - 1);
        if (cut <= MaxLength / 2)
        {
            // A hard cut never splits a surrogate pair (an emoji outside the BMP).
            cut = MaxLength - 1;
            if (char.IsLowSurrogate(text[cut]) && char.IsHighSurrogate(text[cut - 1]))
                cut--;
        }
        return text[..cut].TrimEnd() + "…";
    }

    [GeneratedRegex(@"(-----Original Message-----|-----Ursprüngliche Nachricht-----|---------- Forwarded message|_{10,}|\b(From|Von):\s.{1,200}?\s(Sent|Gesendet):\s|\bOn\s.{4,200}?\swrote:|\bAm\s.{4,200}?\sschrieb[^:]{0,80}:|\s--\s+(?-i:\p{Lu})|\bSent from my\s|\bVon meinem\s\S+\s+gesendet|\bGet Outlook for\s)", RegexOptions.IgnoreCase)]
    private static partial Regex InlineMarker();

    // "Von meinem iPhone gesendet", "Von meinem iPad gesendet" — any device.
    [GeneratedRegex(@"^Von meinem\s\S+\s+gesendet", RegexOptions.IgnoreCase)]
    private static partial Regex LineSignature();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // "On Mon, 3 Nov 2026 at 10:02, Jane Roe <jane@example.com> wrote:" — and its German twin.
    [GeneratedRegex(@"^(On\s.{4,200}\swrote:|Am\s.{4,200}\sschrieb.{0,80}:)$", RegexOptions.IgnoreCase)]
    private static partial Regex QuoteHeader();

    // Outlook's reply header block, as plain text: "From: Jane Roe <…>" followed by Sent:/To:/Subject:.
    // Only "From:" with an address or a "Sent:" companion on the SAME preview line counts, so a
    // sentence that starts "From: our side…" is not cut.
    [GeneratedRegex(@"^(From|Von):\s.{1,200}\s(Sent|Gesendet):\s", RegexOptions.IgnoreCase)]
    private static partial Regex OutlookHeader();
}
