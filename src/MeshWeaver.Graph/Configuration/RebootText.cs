using System.Collections.Immutable;
using System.Text;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The persisted, language-neutral form of one sentence an instance reboot writes about itself
/// (MeshWeaver#7322): a sequence of <see cref="KeyedText"/> parts — the carrier the compile refusals
/// use (#7355) — each with the punctuation that GLUES it to the part before it.
///
/// <para>The reboot's sentences join clauses with <c>"; "</c>, list facts with <c>", "</c> and wrap
/// notes in brackets, which a space-joined part list (<see cref="KeyedTexts.Render"/>) cannot
/// express. The glue is punctuation, never a word, so it is the same in every language; every word
/// lives in a catalog key.</para>
///
/// <para>🚨 <b>Self-validating, exactly as <see cref="KeyedTexts.Localize"/>.</b> The record keeps
/// its English text (<c>Failure</c>, <c>Log</c>, a step's or a check's <c>Detail</c> — the wire value
/// every reader, log and test uses, and the one the watchdog reads its <c>stalled: </c> marker from)
/// AND this keyed form. The keyed form is used only while its English rendering is exactly the stored
/// text, so a writer that changes the text without the keys — or a record written before keys
/// existed — is rendered verbatim, never as a sentence it no longer says.</para>
/// </summary>
public sealed record RebootText
{
    /// <summary>The catalog key whose only content is its <c>{text}</c> argument — a fact carried as written.</summary>
    public const string VerbatimKey = "reboot.verbatim";

    /// <summary>No parts.</summary>
    public static readonly RebootText Empty = new();

    /// <summary>The parts, in order.</summary>
    public ImmutableList<RebootTextPart> Parts { get; init; } = ImmutableList<RebootTextPart>.Empty;

    /// <summary>One keyed part with string arguments.</summary>
    /// <param name="key">The catalog key.</param>
    /// <param name="args">Named arguments for its placeholders.</param>
    /// <returns>The sentence.</returns>
    public static RebootText Of(string key, params (string Name, string Value)[] args)
        => new() { Parts = [new RebootTextPart { Text = KeyedText.Of(key, args) }] };

    /// <summary>A fact carried as written — a version, a path, a sentence another subsystem composed.</summary>
    /// <param name="text">The text; null renders empty.</param>
    /// <returns>The sentence.</returns>
    public static RebootText Verbatim(string? text) => Of(VerbatimKey, ("text", text ?? ""));

    /// <summary><paramref name="value"/> as written, or the catalog key <paramref name="fallbackKey"/> when it is null.</summary>
    /// <param name="value">The fact, or null.</param>
    /// <param name="fallbackKey">The key for "none recorded".</param>
    /// <returns>The sentence.</returns>
    public static RebootText Or(string? value, string fallbackKey) => value is null ? Of(fallbackKey) : Verbatim(value);

    /// <summary>
    /// <paramref name="keyed"/> while it reproduces <paramref name="english"/>, otherwise the English
    /// verbatim — how a sentence another writer stored is embedded in a new one.
    /// </summary>
    /// <param name="english">The stored English text.</param>
    /// <param name="keyed">Its keyed form, or null.</param>
    /// <returns>A keyed form that renders exactly <paramref name="english"/> in English.</returns>
    public static RebootText Faithful(string? english, RebootText? keyed)
        => keyed is not null && keyed.Reproduces(english) ? keyed : Verbatim(english);

    /// <summary>This sentence followed by <paramref name="next"/>, glued with <paramref name="glue"/>.</summary>
    /// <param name="next">What follows.</param>
    /// <param name="glue">The punctuation between the two (a space by default).</param>
    /// <returns>The joined sentence.</returns>
    public RebootText Then(RebootText next, string glue = " ")
        => Parts.IsEmpty ? next
            : next.Parts.IsEmpty ? this
            : new() { Parts = Parts.Add(next.Parts[0] with { Glue = glue }).AddRange(next.Parts.Skip(1)) };

    /// <summary>The items glued with <paramref name="glue"/> — <see cref="Empty"/> when there are none.</summary>
    /// <param name="glue">The punctuation between items.</param>
    /// <param name="items">The items.</param>
    /// <returns>The joined sentence.</returns>
    public static RebootText Join(string glue, IEnumerable<RebootText> items)
        => items.Aggregate(Empty, (joined, item) => joined.Then(item, glue));

    /// <summary>The sentence in <paramref name="locale"/>.</summary>
    /// <param name="locale">The viewer's language tag (<c>ViewerLocale()</c>, never the ambient culture).</param>
    /// <returns>The rendered sentence.</returns>
    public string Render(string? locale)
    {
        var text = new StringBuilder();
        for (var i = 0; i < Parts.Count; i++)
        {
            if (i > 0)
                text.Append(Parts[i].Glue);
            text.Append(Parts[i].Text.Render(locale));
        }
        return text.ToString();
    }

    /// <summary>The English sentence — what the record stores as its wire text.</summary>
    /// <returns>The sentence in English.</returns>
    public string ToEnglish() => Render("en");

    /// <summary>Whether this keyed form renders exactly <paramref name="english"/> in English.</summary>
    /// <param name="english">The stored English text, or null.</param>
    /// <returns>True when the keys are a faithful form of the text.</returns>
    public bool Reproduces(string? english)
        => !Parts.IsEmpty && english is not null && string.Equals(ToEnglish(), english, StringComparison.Ordinal);

    /// <summary>
    /// The text a viewer reads: <paramref name="keyed"/> in <paramref name="locale"/> when it
    /// reproduces <paramref name="english"/>, otherwise <paramref name="english"/> verbatim.
    /// </summary>
    /// <param name="english">The stored English text.</param>
    /// <param name="keyed">The keyed form stored beside it, or null.</param>
    /// <param name="locale">The viewer's language tag.</param>
    /// <returns>The text to render; null only when <paramref name="english"/> is null.</returns>
    public static string? Localize(string? english, RebootText? keyed, string? locale)
        => keyed is not null && keyed.Reproduces(english) ? keyed.Render(locale) : english;
}

/// <summary>One part of a <see cref="RebootText"/>.</summary>
public sealed record RebootTextPart
{
    /// <summary>The keyed words.</summary>
    public KeyedText Text { get; init; } = KeyedText.Of(RebootText.VerbatimKey);

    /// <summary>The punctuation that joins this part to the one before it — ignored on the first part.</summary>
    public string Glue { get; init; } = " ";
}
