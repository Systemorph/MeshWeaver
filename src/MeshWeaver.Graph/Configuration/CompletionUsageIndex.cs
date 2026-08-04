using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// How often each identifier actually appears in the code THIS MESH already contains — the
/// "likely usage" prior behind completion ranking.
///
/// <para><b>Why.</b> Roslyn returns every symbol in scope alphabetically, so a bare member list
/// (<c>Controls.</c>) leads with <c>Badge</c> while every real cell wants <c>Stack</c>,
/// <c>Markdown</c> or <c>DataGrid</c>. Editors solve this with a learned prior: VS Code keeps
/// per-user acceptance history plus a locality bonus, and Visual Studio's IntelliCode re-ranks
/// with a model trained on a large code corpus. The mesh has a BETTER corpus for its own API than
/// any public one — the Code nodes it is already running — so the prior is simply how often each
/// identifier occurs in them.</para>
///
/// <para><b>Safety — this can never break or slow a completion.</b> The index is built ONCE,
/// lazily, in the background, from a BOUNDED query, behind a timeout; until it is ready (and
/// forever, if the query never answers) completions serve with an empty prior and are merely
/// ordered as before. A global <c>nodeType:</c> query is exactly the shape that has wedged this
/// mesh before, so it is never awaited on the request path and every failure resolves to "no
/// prior" rather than an error.</para>
/// </summary>
internal sealed class CompletionUsageIndex(IMessageHub hub, ILogger logger)
{
    /// <summary>How many Code nodes the corpus scan reads at most.</summary>
    private const int MaxNodes = 400;

    /// <summary>How many distinct identifiers are kept — the long tail carries no ranking signal.</summary>
    private const int MaxIdentifiers = 4000;

    /// <summary>How long a built index is served before the next request triggers a rebuild.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    /// <summary>The corpus scan's ceiling — a wedged query must degrade to "no prior", not hang.</summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);

    // Identifiers only: what a completion label can be. Deliberately not a C# parse — this is a
    // frequency prior, and a tokenizer that is 99% right costs microseconds instead of a compile.
    private static readonly Regex IdentifierPattern =
        new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    // The C# keywords and the handful of ubiquitous noise words that would otherwise top every
    // ranking without ever being the thing a user is choosing from a member list.
    private static readonly ImmutableHashSet<string> Ignored = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "var", "new", "return", "using", "public", "private", "protected", "internal", "static",
        "class", "record", "struct", "interface", "enum", "void", "null", "true", "false", "if",
        "else", "for", "foreach", "while", "do", "switch", "case", "break", "continue", "in",
        "out", "ref", "this", "base", "get", "set", "init", "string", "int", "double", "bool",
        "object", "long", "decimal", "float", "byte", "char", "short", "async", "await", "try",
        "catch", "finally", "throw", "namespace", "readonly", "const", "params", "default",
        "is", "as", "when", "where", "select", "from", "let", "into", "yield", "global");

    private volatile ImmutableDictionary<string, int> frequencies =
        ImmutableDictionary<string, int>.Empty;
    private DateTimeOffset builtAt = DateTimeOffset.MinValue;
    private int building;

    /// <summary>
    /// The corpus count for <paramref name="identifier"/> (0 when unknown or the index is not
    /// built yet). Pure and allocation-free — safe to call once per candidate per keystroke.
    /// </summary>
    public int Frequency(string identifier)
        => frequencies.TryGetValue(identifier, out var count) ? count : 0;

    /// <summary>
    /// Kicks off a background (re)build when the index is stale and no build is already running.
    /// Returns immediately — the CALLER NEVER WAITS. Completions that arrive before the first
    /// build finishes are simply ranked without the corpus prior.
    /// </summary>
    public void EnsureFresh()
    {
        if (DateTimeOffset.UtcNow - builtAt < Ttl)
            return;
        if (Interlocked.CompareExchange(ref building, 1, 0) != 0)
            return;

        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
        {
            builtAt = DateTimeOffset.UtcNow; // nothing to scan here — don't retry every keystroke
            Interlocked.Exchange(ref building, 0);
            return;
        }

        // Accumulate the chunked query, settle after a quiet second, take one snapshot — the
        // standard bounded read. Timeout + Catch make every failure mode "no prior".
        mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:Code limit:{MaxNodes}"))
            .Scan(ImmutableDictionary<string, MeshNode>.Empty, (map, change) =>
            {
                if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                    return change.Items.ToImmutableDictionary(n => n.Path);
                foreach (var item in change.Items)
                    map = change.ChangeType == QueryChangeType.Removed
                        ? map.Remove(item.Path)
                        : map.SetItem(item.Path, item);
                return map;
            })
            .Throttle(TimeSpan.FromSeconds(1))
            .Take(1)
            .Timeout(QueryTimeout)
            .Catch(Observable.Return(ImmutableDictionary<string, MeshNode>.Empty))
            .Subscribe(
                nodes =>
                {
                    try
                    {
                        frequencies = Tally(nodes.Values.Select(NodeCode));
                        logger.LogDebug(
                            "Completion usage index built from {NodeCount} Code node(s): {IdentifierCount} identifiers",
                            nodes.Count, frequencies.Count);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Completion usage index build failed — ranking without the corpus prior");
                    }
                    finally
                    {
                        builtAt = DateTimeOffset.UtcNow;
                        Interlocked.Exchange(ref building, 0);
                    }
                },
                ex =>
                {
                    logger.LogDebug(ex, "Completion usage corpus query failed — ranking without the corpus prior");
                    builtAt = DateTimeOffset.UtcNow;
                    Interlocked.Exchange(ref building, 0);
                });
    }

    /// <summary>The node's script text, in whatever shape the content arrived in.</summary>
    private string NodeCode(MeshNode node)
    {
        var code = node.ContentAs<CodeConfiguration>(hub.JsonSerializerOptions)?.Code;
        return code ?? string.Empty;
    }

    /// <summary>
    /// Counts identifier occurrences across <paramref name="sources"/>, keeping the most frequent
    /// <see cref="MaxIdentifiers"/>. Public for the tests — pure, no mesh, no Roslyn.
    /// </summary>
    public static ImmutableDictionary<string, int> Tally(IEnumerable<string> sources)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (string.IsNullOrEmpty(source))
                continue;
            foreach (Match match in IdentifierPattern.Matches(source))
            {
                var token = match.Value;
                if (token.Length < 2 || Ignored.Contains(token))
                    continue;
                counts[token] = counts.TryGetValue(token, out var n) ? n + 1 : 1;
            }
        }
        return counts.Count <= MaxIdentifiers
            ? counts.ToImmutableDictionary(StringComparer.Ordinal)
            : counts.OrderByDescending(kv => kv.Value)
                .Take(MaxIdentifiers)
                .ToImmutableDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }
}
