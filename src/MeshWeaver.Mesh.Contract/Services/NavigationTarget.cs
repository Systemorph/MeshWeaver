using System.Collections.Immutable;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// The three distinct parts of a browser navigation target, parsed from the
/// relative URL that <c>NavigationManager</c> reports.
///
/// <para>A mesh node address is <b>always</b> the bare path — never a query string
/// (see <c>Doc/Architecture</c> → "Mesh URL Shape": <c>{baseUrl}/{meshpath}</c>, no
/// <c>?query</c>). So a navigation target is split into:</para>
/// <list type="bullet">
///   <item><see cref="Path"/> — the route (e.g. <c>search</c>, <c>AgenticPension/Jahresrechnung</c>).
///     This is the ONLY part that is resolved to a mesh node address and
///     permission-checked.</item>
///   <item><see cref="Args"/> — the query-string parameters (e.g. <c>q</c>,
///     <c>groupBy</c>). These are <b>page parameters</b>, never part of the address.</item>
/// </list>
///
/// <para>The reported defect this guards against: navigating to
/// <c>/search?q=nodeType%3AThread&amp;groupBy=Namespace</c> fed the whole URL (query
/// included) into path resolution, so the address became
/// <c>search?q=nodeType:Thread&amp;groupBy=Namespace</c> and the <c>nodeType:Thread</c>
/// query token got permission-checked as a Thread node ("lacks Thread permission").
/// Splitting the query into <see cref="Args"/> keeps it out of the address entirely.</para>
/// </summary>
public sealed record NavigationTarget
{
    /// <summary>The route — the part that maps to a mesh node address. Query and fragment stripped, leading '/' trimmed.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// The parsed query-string parameters. Empty when the URL carries no query.
    /// Values are URL-decoded; duplicate keys keep the last value.
    /// </summary>
    public ImmutableDictionary<string, string> Args { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Splits a relative navigation URL into its <see cref="Path"/> (route) and
    /// <see cref="Args"/> (query parameters). The <c>#fragment</c> is discarded.
    /// Never throws — a null/empty input yields an empty <see cref="Path"/>.
    /// </summary>
    public static NavigationTarget Parse(string? relativeUrl)
    {
        if (string.IsNullOrEmpty(relativeUrl))
            return new NavigationTarget { Path = string.Empty };

        var s = relativeUrl;

        // Drop the fragment — it is never part of the node path or the args.
        var hashIndex = s.IndexOf('#');
        if (hashIndex >= 0)
            s = s[..hashIndex];

        var queryIndex = s.IndexOf('?');
        var pathPart = queryIndex >= 0 ? s[..queryIndex] : s;
        var queryPart = queryIndex >= 0 ? s[(queryIndex + 1)..] : string.Empty;

        return new NavigationTarget
        {
            Path = pathPart.TrimStart('/'),
            Args = ParseQuery(queryPart)
        };
    }

    private static ImmutableDictionary<string, string> ParseQuery(string query)
    {
        if (string.IsNullOrEmpty(query))
            return ImmutableDictionary<string, string>.Empty;

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            string key, value;
            if (eq < 0)
            {
                key = Decode(pair);
                value = string.Empty;
            }
            else
            {
                key = Decode(pair[..eq]);
                value = Decode(pair[(eq + 1)..]);
            }

            if (key.Length == 0)
                continue;
            // Last value wins for a repeated key — matches a plain key→value model.
            builder[key] = value;
        }

        return builder.ToImmutable();
    }

    // Query strings encode space as '+' (legacy) or '%20'; Uri.UnescapeDataString only
    // handles the percent form, so normalise '+' → ' ' first (HttpUtility parity).
    private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
}
