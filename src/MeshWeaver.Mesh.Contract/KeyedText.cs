using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// One catalog key plus its NAMED arguments — the persisted, language-neutral form of a sentence
/// the platform composed, resolved in the VIEWER's language when someone reads it (#7355).
///
/// <para>Persisted on a shared record (a <c>NodeTypeDefinition</c>), so translating at write time
/// would freeze one language — the writer's, usually System's — into a record every viewer reads.
/// The key travels instead, and <see cref="KeyedTexts.Localize"/> renders it at read time.</para>
/// </summary>
/// <param name="Key">The catalog key in <c>strings.en.json</c> / <c>strings.de.json</c>.</param>
/// <param name="Args">Named arguments for the key's <c>{name}</c> placeholders, or null.</param>
public sealed record KeyedText(string Key, ImmutableDictionary<string, object>? Args = null)
{
    /// <summary>A keyed sentence with string arguments.</summary>
    /// <param name="key">The catalog key.</param>
    /// <param name="args">Named arguments for its placeholders.</param>
    /// <returns>The keyed sentence.</returns>
    public static KeyedText Of(string key, params (string Name, string Value)[] args)
        => new(key, args is { Length: > 0 }
            ? args.ToImmutableDictionary(a => a.Name, a => (object)a.Value)
            : null);

    /// <summary>The sentence in <paramref name="locale"/>.</summary>
    /// <param name="locale">The viewer's language tag.</param>
    /// <returns>The rendered sentence; never null.</returns>
    public string Render(string? locale) => LocalizationCatalog.GetNamed(Key, locale, Args);
}

/// <summary>
/// Rendering a sequence of <see cref="KeyedText"/> parts against the English text stored beside
/// them. The parts are joined with one space.
///
/// <para>🚨 <b>Self-validating.</b> A record keeps its English text (the wire value every existing
/// reader, log and test uses) AND the parts. The parts are used only while their ENGLISH rendering
/// is exactly that stored text, so a later writer that changes the text without the parts — or a
/// record written before keys existed — can never be rendered as a sentence it no longer says: it
/// falls back to the stored English verbatim.</para>
/// </summary>
public static class KeyedTexts
{
    /// <summary>The parts joined in <paramref name="locale"/>.</summary>
    /// <param name="parts">The keyed parts.</param>
    /// <param name="locale">The viewer's language tag.</param>
    /// <returns>The rendered text.</returns>
    public static string Render(IReadOnlyList<KeyedText> parts, string? locale)
        => string.Join(" ", parts.Select(p => p.Render(locale)));

    /// <summary>Whether <paramref name="parts"/> render exactly <paramref name="english"/> in English.</summary>
    /// <param name="parts">The keyed parts, or null.</param>
    /// <param name="english">The stored English text, or null.</param>
    /// <returns>True when the parts are a faithful key for the text.</returns>
    public static bool Reproduces(IReadOnlyList<KeyedText>? parts, string? english)
        => parts is { Count: > 0 } && english is not null
           && string.Equals(Render(parts, "en"), english, StringComparison.Ordinal);

    /// <summary>
    /// The text a viewer reads: <paramref name="parts"/> in <paramref name="locale"/> when they
    /// reproduce <paramref name="english"/>, otherwise <paramref name="english"/> verbatim.
    /// </summary>
    /// <param name="english">The stored English text.</param>
    /// <param name="parts">The keyed parts stored beside it, or null.</param>
    /// <param name="locale">The viewer's language tag (<c>ViewerLocale()</c>, never the ambient culture).</param>
    /// <returns>The text to render; null only when <paramref name="english"/> is null.</returns>
    public static string? Localize(string? english, IReadOnlyList<KeyedText>? parts, string? locale)
        => Reproduces(parts, english) ? Render(parts!, locale) : english;
}
