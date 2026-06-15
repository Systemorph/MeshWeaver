using System.Reactive.Linq;
using System.Text;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// A dependency-free <see cref="ISummarizer"/> that derives a short summary from the document text
/// itself — no AI/LLM call, no external dependency, no cost. It takes the leading prose (the first
/// non-blank lines, with markdown heading/list markers stripped) collapsed to a single line and
/// length-bounded.
///
/// <para>This is the sensible DEFAULT summary for a host that has not wired a chat model: the
/// per-file <c>Document</c> node still gets a meaningful, human-readable summary. Swap in
/// <see cref="ChatClientSummarizer"/> (via the chat-client overloads of the pipeline/document
/// extensions) for AI-generated summaries — the rest of the pipeline is identical.</para>
/// </summary>
public sealed class ExtractiveSummarizer : ISummarizer
{
    private const int MaxChars = 280;

    /// <inheritdoc />
    public IObservable<string> Summarize(string text, string fileName) =>
        Observable.Return(Extract(text));

    /// <summary>
    /// Leading prose of <paramref name="text"/>, one line, ≤ <see cref="MaxChars"/> chars. Markdown
    /// heading markers (<c>#</c>), block-quote (<c>&gt;</c>) and list bullets (<c>-</c>/<c>*</c>) are
    /// stripped from each line so the summary reads as prose rather than raw markup.
    /// </summary>
    private static string Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sb = new StringBuilder(MaxChars + 16);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '>', '-', '*', ' ', '\t').Trim();
            if (line.Length == 0)
                continue;
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(line);
            if (sb.Length >= MaxChars)
                break;
        }

        // Collapse any internal whitespace runs introduced by the line joins.
        var oneLine = string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= MaxChars ? oneLine : oneLine[..MaxChars].TrimEnd() + "…";
    }
}
