using System.Collections.Immutable;

namespace MeshWeaver.Messaging;

/// <summary>
/// The languages this deployment ships, and the ONE rule for resolving an arbitrary requested
/// language tag to one of them. Pure, dependency-free and deterministic.
///
/// <para>Both localization shapes route through here — the string catalog (Blazor markup, inline
/// <c>Controls.*</c> literals, toasts) and the <c>[Translation]</c> attributes (property labels,
/// node-type names, enum members) — so they can never disagree about which language a viewer is in.
/// Placed in the contract layer, below <c>AccessService</c>, so the hub, the mesh, the layout areas
/// and the Blazor client all share one definition.</para>
/// </summary>
public static class Locales
{
    /// <summary>
    /// The fallback language. Every unsupported, empty or unresolvable tag lands here, so a missing
    /// translation degrades to readable English rather than to a blank UI.
    /// </summary>
    public const string Default = "en";

    /// <summary>
    /// The languages this deployment ships translations for, in display order. Adding a language is
    /// this list, plus a <c>strings.{tag}.json</c> catalog, plus the matching
    /// <c>[Translation("{tag}", …)]</c> attributes — nothing else.
    /// </summary>
    public static ImmutableList<string> Supported { get; } = ["en", "de"];

    /// <summary>
    /// Endonyms (each language named in itself) for the settings picker — a German speaker looks
    /// for "Deutsch", not "German". Keyed by the tag stored in <c>User.Locale</c>.
    /// </summary>
    public static ImmutableDictionary<string, string> DisplayNames { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "English",
            ["de"] = "Deutsch",
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves an arbitrary requested tag to a SUPPORTED language, never throwing and never
    /// returning null. Falls back in three steps: exact match → primary subtag (so <c>de-CH</c>,
    /// <c>de-AT</c> and <c>de_DE</c> all serve German) → <see cref="Default"/>.
    /// </summary>
    public static string Resolve(string? requested) => TryMatch(requested) ?? Default;

    /// <summary>
    /// Resolves a whole HTTP <c>Accept-Language</c> header to a SUPPORTED language, or <c>null</c>
    /// when the visitor asked for nothing this deployment ships.
    ///
    /// <para>This is the ONLY thing we know about an ANONYMOUS visitor's language. A first-time
    /// visitor — the audience a paywall, an invite or a public course page exists for — has no
    /// profile, so <see cref="AccessContext.Locale"/> would otherwise be null and every one of them
    /// would be served English regardless of who they are. The header is seeded onto the identity
    /// (see <c>UserContextMiddleware</c> and <c>CircuitAccessHandler</c>) so it rides the SAME
    /// explicit path a signed-in user's stored preference does, rather than becoming a second,
    /// ambient resolution mechanism.</para>
    ///
    /// <para>Full header shape per RFC 9110: a comma-separated list of tags, each optionally
    /// weighted (<c>de-CH, de;q=0.9, en;q=0.8</c>). Entries are tried in descending weight — ties
    /// keep the order the browser sent, which is already the browser's own preference order — and
    /// each is matched by <see cref="TryMatch"/>, so region variants fold onto their primary subtag
    /// exactly as everywhere else (<c>en-GB</c> → <c>en</c>, <c>de-CH</c> → <c>de</c>). Entries with
    /// <c>q=0</c> are explicit REFUSALS and are skipped; the <c>*</c> wildcard means "no preference"
    /// and is skipped too, so it can never outrank a real tag or pin a visitor to a language they
    /// never asked for.</para>
    ///
    /// <para>🚨 Returns <c>null</c>, not <see cref="Default"/>, when nothing matches — for the same
    /// reason <see cref="TryMatch"/> does. "This visitor asked for something we do not ship" must
    /// stay distinguishable from "this visitor asked for English", so a later, better answer (a
    /// profile that loads a moment afterwards) is not shadowed by a guess.</para>
    /// </summary>
    /// <param name="acceptLanguageHeader">The raw header value; null, empty or malformed is fine.</param>
    /// <returns>A tag from <see cref="Supported"/>, or <c>null</c>.</returns>
    public static string? Negotiate(string? acceptLanguageHeader)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguageHeader))
            return null;

        // Weight-ordered, ties broken by the order the browser sent — the header's own order is
        // already a preference statement, so the index tiebreak is spelled out rather than left to
        // LINQ's (documented, but easy to overlook) stable sort.
        var best = acceptLanguageHeader
            .Split(',')
            .Select((entry, order) => (Entry: entry.Split(';'), Order: order))
            .Select(e => (Tag: e.Entry[0].Trim(), Quality: QualityOf(e.Entry), e.Order))
            // "*" is "anything is acceptable" — an absence of preference, not a request for the
            // first language we happen to list. Treating it as a match would pin every browser that
            // sends "*" (curl, many bots) to a language chosen by our ordering. q<=0 is the
            // header's way of saying "explicitly NOT this language".
            .Where(e => e.Tag.Length > 0 && e.Tag != "*" && e.Quality > 0d)
            .OrderByDescending(e => e.Quality)
            .ThenBy(e => e.Order)
            .Select(e => TryMatch(e.Tag))
            .FirstOrDefault(matched => matched is not null);

        return best;
    }

    /// <summary>
    /// The <c>q=</c> weight of one already-split <c>Accept-Language</c> entry; 1.0 when absent.
    /// A MALFORMED weight keeps the default rather than dropping the entry — the browser still
    /// named a language, and discarding the preference over a bad number is the wrong trade.
    /// </summary>
    private static double QualityOf(string[] entryParts)
    {
        for (var p = 1; p < entryParts.Length; p++)
        {
            var parameter = entryParts[p].Trim();
            if (!parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                continue;
            return double.TryParse(
                parameter.AsSpan(2),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : 1.0d;
        }

        return 1.0d;
    }

    /// <summary>
    /// Returns the supported tag matching <paramref name="requested"/>, or <c>null</c> when this
    /// deployment ships no translation for it. Use <see cref="Resolve"/> when you need a usable
    /// tag; use this when "unsupported" must stay distinguishable from "English" — the write-once
    /// profile population needs that distinction so it does not pin a profile to a language we
    /// would only ever render in English anyway.
    /// </summary>
    public static string? TryMatch(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return null;

        // Accept BCP-47 ("de-CH"), POSIX ("de_CH.UTF-8") and weighted Accept-Language
        // ("de-CH;q=0.9") shapes — the browser, the OS and the HTTP header each produce a
        // different one and they all arrive at this method.
        var tag = requested.Trim().Split('.', ';')[0].Replace('_', '-');

        foreach (var supported in Supported)
            if (string.Equals(supported, tag, StringComparison.OrdinalIgnoreCase))
                return supported;

        var primary = tag.Split('-')[0];
        if (primary.Length == 0)
            return null;

        foreach (var supported in Supported)
            if (string.Equals(supported, primary, StringComparison.OrdinalIgnoreCase))
                return supported;

        return null;
    }
}
