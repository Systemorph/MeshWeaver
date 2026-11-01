using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Completion;

/// <summary>
/// One autocomplete request from a query box.
/// </summary>
/// <param name="Text">The VISIBLE query text being typed.</param>
/// <param name="Caret">The caret offset in <paramref name="Text"/> (0-based).</param>
/// <param name="HiddenQuery">The control's hidden query (its fixed scope — one sub-query per line), which
/// supplies the NodeType context for content completions and the anchor for node and semantic suggestions.</param>
/// <param name="ContextPath">The path the box is browsing (proximity ranking for name matches).</param>
/// <param name="Limit">Maximum number of suggestions.</param>
public sealed record QueryCompletionRequest(
    string Text,
    int Caret,
    string? HiddenQuery = null,
    string? ContextPath = null,
    int Limit = 20);

/// <summary>
/// Query-box autocomplete over the mesh: the grammar (<see cref="QueryGrammarCompleter"/>) fed with live
/// facts — the NodeType catalog, the content schema of the NodeType in context, the members a
/// <c>[MeshNode]</c> property may hold, node paths — plus, for a free-text word, the nodes whose NAME
/// starts with it and the top semantic (vector-index) hits of the query as typed.
///
/// <para><b>Reactive end to end.</b> <see cref="Complete"/> returns a cold observable that emits a growing
/// snapshot as each source answers and COMPLETES once every source has answered or spent its budget
/// (<see cref="SourceBudget"/>), so a caller can render progressively or take the last snapshot. Every
/// source is bounded (a <c>limit:</c>, a <c>Take(1)</c>, a timeout) and a failing source contributes
/// nothing and is logged — completion is best-effort and one slow index must never empty the grammar's
/// suggestions.</para>
///
/// <para><b>Identity.</b> Every read runs as the caller: mesh queries and node reads carry the ambient
/// viewer. Suggestions therefore name only what the viewer could find with the query themselves.</para>
///
/// <para><b>Lifetime.</b> One instance per query box. It caches content schemas per NodeType for its own
/// lifetime (a <see cref="PromiseCache{TKey,TValue}"/>, so a failed fetch is retried by the next request,
/// never replayed); it holds no static state. Full account: <c>Doc/GUI/QueryAutocomplete</c>.</para>
/// </summary>
public sealed class QueryCompleter
{
    /// <summary>Maximum time any one source may take before it contributes nothing.</summary>
    public static readonly TimeSpan SourceBudget = TimeSpan.FromSeconds(2);

    /// <summary>How many semantic hits a free-text word adds.</summary>
    public const int SemanticTopK = 5;

    /// <summary>How many name-prefix node hits a free-text word adds.</summary>
    public const int NameTopK = 8;

    /// <summary>How many NodeTypes / reference members / paths are read for a value.</summary>
    public const int ValueReadLimit = 50;

    /// <summary>How many NodeTypes in context contribute content properties.</summary>
    public const int MaxContextNodeTypes = 3;

    /// <summary>Shortest free-text word that triggers node and semantic suggestions.</summary>
    public const int MinFreeTextLength = 3;

    /// <summary>
    /// How many of the catalog's own hits (the hidden query) are read for the values a reference property
    /// actually carries. Distinct values are capped at <see cref="ValueReadLimit"/> after that.
    /// </summary>
    public const int HitScanLimit = 200;

    private const string RowSelect = "select:path,name,nodeType,icon";

    private readonly IMeshService mesh;
    private readonly IMessageHub hub;
    private readonly ILogger? logger;
    private readonly PromiseCache<string, IReadOnlyList<ContentPropertyFact>> schemas = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a completer reading through <paramref name="mesh"/> and <paramref name="hub"/>.</summary>
    /// <param name="mesh">The mesh query surface (runs as the ambient viewer).</param>
    /// <param name="hub">The caller's hub — its services (the content-type registry), serializer options and node reads.</param>
    /// <param name="logger">Optional logger for sources that fail.</param>
    public QueryCompleter(IMeshService mesh, IMessageHub hub, ILogger? logger = null)
    {
        this.mesh = mesh;
        this.hub = hub;
        this.logger = logger;
    }

    /// <summary>
    /// The suggestions for <paramref name="request"/>: a cold, bounded, completing stream of growing snapshots.
    /// </summary>
    /// <param name="request">The text, caret and context.</param>
    /// <returns>Snapshots, best first; the last one is the settled answer.</returns>
    public IObservable<QueryCompletionResult> Complete(QueryCompletionRequest request)
    {
        var token = QueryCaret.At(request.Text, request.Caret);
        if (token.Kind is QueryCaretKind.None or QueryCaretKind.Reference)
            return Observable.Return(new QueryCompletionResult(token.ReplaceStart, token.ReplaceEnd, []));

        var contextTypes = ContextNodeTypes(request.HiddenQuery, request.Text);
        var today = DateOnly.FromDateTime(Clock().GetUtcNow().UtcDateTime);
        var facts = FactsFor(token, contextTypes, request).StartWith(QueryCompletionFacts.None)
            .Select(f => f with { Today = today });
        var nodes = FreeTextNodes(token, request).StartWith(Array.Empty<QueryCompletionItem>());

        return facts.CombineLatest(nodes, (f, n) => Merge(token, f, n, request.Limit))
            .DistinctUntilChanged(ResultComparer.Instance);
    }

    /// <summary>
    /// The NodeTypes the query is about — every positive <c>nodeType:</c> value in the hidden query and the
    /// visible text (equality or alternation), first ones first, at most <see cref="MaxContextNodeTypes"/>.
    /// </summary>
    /// <param name="hiddenQuery">The hidden query (one sub-query per line).</param>
    /// <param name="visible">The visible text.</param>
    /// <returns>The NodeType paths.</returns>
    public static IReadOnlyList<string> ContextNodeTypes(string? hiddenQuery, string? visible)
    {
        var parser = new QueryParser();
        var lines = (hiddenQuery ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(visible ?? "");
        var result = new List<string>();
        foreach (var line in lines)
        {
            Walk(parser.Parse(line).Filter, result);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxContextNodeTypes).ToArray();

        static void Walk(QueryNode? node, List<string> into)
        {
            switch (node)
            {
                case QueryComparison { Condition: var c }
                    when c.Selector.Equals(QueryGrammar.NodeType, StringComparison.OrdinalIgnoreCase)
                         && c.Operator is QueryOperator.Equal or QueryOperator.In:
                    into.AddRange(c.Values.Where(v => v.Length > 0 && !v.Contains('*')));
                    break;
                case QueryAnd and:
                    foreach (var child in and.Children) Walk(child, into);
                    break;
                case QueryOr or:
                    foreach (var child in or.Children) Walk(child, into);
                    break;
            }
        }
    }

    private static QueryCompletionResult Merge(
        QueryCaretToken token, QueryCompletionFacts facts, IReadOnlyList<QueryCompletionItem> nodes, int limit)
    {
        var grammar = QueryGrammarCompleter.Complete(token, facts, limit);
        var items = grammar.Concat(nodes)
            .GroupBy(i => i.InsertText, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(i => i.Rank).First())
            .OrderByDescending(i => i.Rank)
            .Take(Math.Max(0, limit))
            .ToArray();
        return new QueryCompletionResult(token.ReplaceStart, token.ReplaceEnd, items);
    }

    // ---------------------------------------------------------------- facts

    private IObservable<QueryCompletionFacts> FactsFor(
        QueryCaretToken token, IReadOnlyList<string> contextTypes, QueryCompletionRequest request)
    {
        switch (token.Kind)
        {
            case QueryCaretKind.Value when Is(token.Field, QueryGrammar.NodeType):
                return Bounded(NodeTypes(token.Prefix), "nodeType catalog")
                    .Select(n => new QueryCompletionFacts { NodeTypes = n });
            case QueryCaretKind.Value when token.Field is { } f && QueryGrammar.TakesPath(f):
                return Bounded(Paths(token.Prefix, request.ContextPath), "path values")
                    .Select(n => new QueryCompletionFacts { ReferenceValues = n });
            case QueryCaretKind.Value when Is(token.Field, QueryGrammar.Sort):
            case QueryCaretKind.ContentProperty:
            case QueryCaretKind.Keyword when token.Prefix.Length > 0:
                return Content(contextTypes).Select(p => new QueryCompletionFacts { ContentProperties = p });
            case QueryCaretKind.ContentValue:
                return Content(contextTypes).SelectMany(props =>
                {
                    var prop = props.FirstOrDefault(p => p.Name.Equals(token.Field, StringComparison.OrdinalIgnoreCase));
                    var withProps = new QueryCompletionFacts { ContentProperties = props };
                    if (prop is not { ReferenceQueries.Count: > 0 })
                        return Observable.Return(withProps);
                    return ReferenceValues(prop, token.Prefix, request)
                        .Select(members => withProps with { ReferenceValues = members })
                        .StartWith(withProps);
                });
            default:
                return Observable.Empty<QueryCompletionFacts>();
        }
    }

    private IObservable<IReadOnlyList<ContentPropertyFact>> Content(IReadOnlyList<string> nodeTypes)
    {
        if (nodeTypes.Count == 0)
            return Observable.Return<IReadOnlyList<ContentPropertyFact>>([]);
        return nodeTypes
            .Select(t => Bounded(schemas.GetOrAdd(t, SchemaOf), $"schema of {t}")
                .DefaultIfEmpty(Array.Empty<ContentPropertyFact>()))
            .CombineLatest()
            .Select(lists => (IReadOnlyList<ContentPropertyFact>)lists
                .SelectMany(l => l)
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray());
    }

    /// <summary>
    /// The content properties of <paramref name="nodeType"/>, from its CLR content type as the mesh-wide
    /// <see cref="IMeshContentTypeRegistry"/> knows it, read through the generator the mesh publishes its
    /// schemas with (<see cref="ContentSchemaFacts.FromType(Type, System.Text.Json.JsonSerializerOptions, MeshWeaver.Messaging.Serialization.XmlDocsReader)"/>).
    /// <para>A type is registered when a hub of that NodeType ACTIVATES in this process. When it is not
    /// known yet, the completer reads ONE instance of the type through <c>GetMeshNodeStream</c> — the
    /// sanctioned node read — which activates that instance's hub and so registers its content type, and
    /// takes the registration (subscribed BEFORE the read, and the map re-checked after, so neither order
    /// of events is lost). A type with no instance the viewer can read contributes no properties: there
    /// is nothing of it to search.</para>
    /// </summary>
    private IObservable<IReadOnlyList<ContentPropertyFact>> SchemaOf(string nodeType)
    {
        var registry = hub.ServiceProvider.GetService(typeof(IMeshContentTypeRegistry)) as IMeshContentTypeRegistry;
        if (registry is null)
            return Observable.Return<IReadOnlyList<ContentPropertyFact>>([]);

        var known = Observable.Defer(() => registry.TryResolveByNodeType(nodeType, out var t)
            ? Observable.Return(t)
            : Observable.Empty<Type>());
        var registered = registry
            .RegistrationsMatching(r => string.Equals(r.NodeTypePath, nodeType, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ContentType);
        var activate = Rows($"nodeType:{QueryGrammarCompleter.ValueText(nodeType)} scope:subtree partitions:all select:path", 1)
            .SelectMany(rows => rows.Count == 0
                ? Observable.Empty<Type>()
                : hub.GetMeshNodeStream(rows[0].Path).Take(1).IgnoreElements().Select(_ => typeof(object)))
            .Concat(known);

        return known
            .Concat(Observable.Defer(() => registered.Merge(activate)))
            .Take(1)
            .Timeout(SourceBudget)
            // The generator reads the XML-doc summary of every property it walks, so it runs on the
            // mesh's documentation read edge, never on the completing turn (#7386).
            .SelectMany(type => MeshWeaver.Messaging.Serialization.XmlDocsExtensions.OnXmlDocsReadEdge(hub, ct =>
            {
                ct.ThrowIfCancellationRequested();
                return ContentSchemaFacts.FromType(type, hub.JsonSerializerOptions, MeshWeaver.Messaging.Serialization.XmlDocsExtensions.XmlDocs(hub));
            }));
    }

    private IObservable<IReadOnlyList<QueryNodeFact>> NodeTypes(string prefix)
    {
        var slash = prefix.LastIndexOf('/');
        var query = slash > 0
            ? $"namespace:{QueryGrammarCompleter.ValueText(prefix[..slash])} scope:descendants nodeType:NodeType"
            : $"nodeType:NodeType partitions:all{NameFilter(prefix)}";
        return Rows($"{query} {RowSelect}", ValueReadLimit);
    }

    /// <summary>
    /// The values a reference property can take, from two kinds of source merged as they answer:
    /// <list type="bullet">
    /// <item><description><b>Every reference leg.</b> A property declares one anchored query per partition
    /// (<c>[MeshNode("namespace:A …", "namespace:B …")]</c>), so EVERY non-template leg is read — not
    /// the first — each filtered by what was typed on the member's name and on its full path (both a
    /// contains match, applied by the store), bounded by <see cref="ValueReadLimit"/>.</description></item>
    /// <item><description><b>The catalog's own hits.</b> The members a book actually references often live
    /// in partitions no leg names (one top-level partition per company). The hidden query defines the
    /// catalog's result set, so its hits are read (<see cref="HitScanLimit"/>), the distinct values they
    /// carry for the property collected, and each value that is a node path resolved to its node for the
    /// label.</description></item>
    /// </list>
    /// Every read is capped at <see cref="ValueReadLimit"/>; the result is de-duplicated by path, and the grammar then
    /// filters it by contains on name or path (<see cref="QueryGrammarCompleter.NodeMatch"/>). Each source
    /// is bounded, so the stream completes.
    /// </summary>
    private IObservable<IReadOnlyList<QueryNodeFact>> ReferenceValues(
        ContentPropertyFact prop, string prefix, QueryCompletionRequest request)
    {
        var sources = ReferenceLegReads(prop, prefix)
            .Select(q => Bounded(Rows(q, ValueReadLimit), "reference members"))
            // Each read inside is bounded on its own (hits, then names), so the chain completes.
            .Append(Guarded(CarriedValues(prop.Name, request.HiddenQuery), "values the catalog's hits carry"))
            .ToArray();
        return sources.Merge()
            .Scan((IReadOnlyList<QueryNodeFact>)[], (acc, next) => acc.Concat(next)
                .GroupBy(n => n.Path, StringComparer.Ordinal)
                .Select(g => g.FirstOrDefault(n => n.Name is { Length: > 0 }) ?? g.First())
                .ToArray());
    }

    /// <summary>
    /// The member reads of a reference property: every non-template leg (a <c>{node.X}</c> template needs
    /// the record being edited — a search box has none), declared mesh-wide when the leg anchors nothing,
    /// once filtered by name and once by full path (<c>mainNode:*typed*</c>) when something plain was
    /// typed, else unfiltered.
    /// </summary>
    /// <param name="prop">The reference property.</param>
    /// <param name="prefix">What was typed of the value.</param>
    /// <returns>The query strings to run.</returns>
    public static IReadOnlyList<string> ReferenceLegReads(ContentPropertyFact prop, string prefix)
    {
        var parser = new QueryParser();
        var filters = prefix.Contains('/') || NameFilter(prefix).Length == 0
            ? new[] { "" }
            // `mainNode` IS the path of a main node (every reference member is one), so a Like on it is a
            // contains match over the WHOLE path — a parent segment included — applied by the store
            // before the leg's limit, and, unlike `path:*x*`, it is a filter, never an anchor.
            : new[] { NameFilter(prefix), $" mainNode:*{prefix}*" };
        return prop.ReferenceQueries
            .Where(q => !q.Contains('{'))
            .Distinct(StringComparer.Ordinal)
            .SelectMany(q =>
            {
                var anchor = parser.Parse(q).IsSufficientlySpecified() ? "" : " partitions:all";
                return filters.Select(f => $"{q}{anchor}{f} {RowSelect}");
            })
            .ToArray();
    }

    /// <summary>
    /// The distinct values the catalog's own hits carry for <paramref name="property"/>: each hidden
    /// sub-query is run bounded (its <c>sort:</c>/<c>limit:</c>/<c>select:</c> replaced, the hits narrowed
    /// to those that HAVE a value, the content selected), the values read shape-tolerantly, and the ones
    /// that are node paths resolved to their nodes in ONE read so the suggestion is labelled with the
    /// node's name. A value that resolves to nothing is still offered, labelled as stored.
    /// </summary>
    private IObservable<IReadOnlyList<QueryNodeFact>> CarriedValues(string property, string? hiddenQuery)
    {
        var lines = (hiddenQuery ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return Observable.Return<IReadOnlyList<QueryNodeFact>>([]);

        var parser = new QueryParser();
        var hits = lines.Select(line =>
        {
            var query = line;
            foreach (var q in new[] { "sort", "limit", "select" })
                query = parser.RemoveQualifier(query, q);
            query = $"{query} {QueryCaret.ContentPrefix}{property}:{QueryWildcard.Wildcard}".Trim();
            var anchor = parser.Parse(query).IsSufficientlySpecified() ? "" : " partitions:all";
            return Bounded(Nodes($"{query}{anchor} select:path,content", HitScanLimit), "catalog hits");
        });

        return hits.Merge()
            .Aggregate(new List<string>(), (acc, nodes) =>
            {
                foreach (var node in nodes)
                    acc.AddRange(ValuesOf(node.Content, property));
                return acc;
            })
            .Select(all => all.Distinct(StringComparer.Ordinal).Take(ValueReadLimit).ToArray())
            .SelectMany(values =>
            {
                var raw = (IReadOnlyList<QueryNodeFact>)values.Select(v => new QueryNodeFact(v, null)).ToArray();
                var paths = values.Where(IsPlainPath).ToArray();
                if (paths.Length == 0)
                    return Observable.Return(raw);
                return Bounded(Rows($"{QueryGrammar.Path}:{string.Join('|', paths)} {RowSelect}", paths.Length), "carried value names")
                    .DefaultIfEmpty([])
                    .Select(resolved =>
                    {
                        var byPath = resolved.GroupBy(n => n.Path, StringComparer.Ordinal)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                        return (IReadOnlyList<QueryNodeFact>)raw
                            .Select(v => byPath.TryGetValue(v.Path, out var n) ? n : v)
                            .ToArray();
                    });
            });
    }

    /// <summary>
    /// The string values <paramref name="content"/> holds at <paramref name="property"/> (a dotted path,
    /// matched case-insensitively): a string, or the strings of an array. Shape-tolerant — typed content
    /// and a <see cref="System.Text.Json.JsonElement"/> are read alike, and anything else yields nothing.
    /// </summary>
    /// <param name="content">A node's content.</param>
    /// <param name="property">The content property, without the <c>content.</c> prefix.</param>
    /// <returns>The values; empty when the content does not carry the property.</returns>
    private IEnumerable<string> ValuesOf(object? content, string property)
    {
        if (content is null)
            return [];
        // A content that cannot be serialized faults this source, which Guarded reports and drops.
        var element = content as System.Text.Json.JsonElement?
                      ?? System.Text.Json.JsonSerializer.SerializeToElement(content, content.GetType(), hub.JsonSerializerOptions);
        foreach (var segment in property.Split('.'))
        {
            if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
                return [];
            var found = element.EnumerateObject()
                .FirstOrDefault(p => p.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (found.Value.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                return [];
            element = found.Value;
        }
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String when element.GetString() is { Length: > 0 } v => [v],
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray()
                .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(v => v.Length > 0)
                .ToArray(),
            _ => [],
        };
    }

    /// <summary>A value that can be written bare in a <c>path:a|b</c> alternation.</summary>
    private static bool IsPlainPath(string value)
        => value.Length > 0 && value[0] != '@'
           && value.All(c => !char.IsWhiteSpace(c) && c is not ('|' or '*' or '(' or ')' or '"' or '\'' or ':'));

    private IObservable<IReadOnlyList<QueryNodeFact>> Paths(string prefix, string? contextPath)
    {
        var text = prefix.TrimStart('@').TrimStart('/');
        var slash = text.LastIndexOf('/');
        var (basePath, namePrefix) = slash >= 0
            ? (text[..slash], text[(slash + 1)..])
            : (contextPath ?? "", text);
        return mesh.Autocomplete(basePath, namePrefix, AutocompleteMode.PathFirst, ValueReadLimit, contextPath)
            .Where(s => s.Count > 0)
            .Take(1)
            .Select(s => (IReadOnlyList<QueryNodeFact>)s.Select(Fact).ToArray());
    }

    // ---------------------------------------------------------------- free text: names + semantic

    private IObservable<IReadOnlyList<QueryCompletionItem>> FreeTextNodes(QueryCaretToken token, QueryCompletionRequest request)
    {
        if (token.Kind != QueryCaretKind.Keyword || token.Negated || token.Prefix.Length < MinFreeTextLength)
            return Observable.Empty<IReadOnlyList<QueryCompletionItem>>();
        // Typing a qualifier ("names" → name:, "nodeTy") is not a search for nodes.
        if (QueryGrammar.Qualifiers.Concat(QueryGrammar.NodeFields)
            .Any(q => q.Name.StartsWith(token.Prefix, StringComparison.OrdinalIgnoreCase)))
            return Observable.Empty<IReadOnlyList<QueryCompletionItem>>();

        var byName = Bounded(
                mesh.Autocomplete(request.ContextPath ?? "", token.Prefix, AutocompleteMode.RelevanceFirst, NameTopK, request.ContextPath)
                    .Where(s => s.Count > 0)
                    .Take(1)
                    .Select(s => (IReadOnlyList<QueryNodeFact>)s.Select(Fact).ToArray()),
                "name matches")
            .Select(ns => NodeItems(ns, QueryGrammarCompleter.NodeCategory, CompletionKind.Reference, baseRank: 2000));

        var semantic = Bounded(Rows(SemanticQuery(request), SemanticTopK), "semantic matches")
            .Select(ns => NodeItems(ns, QueryGrammarCompleter.SemanticCategory, CompletionKind.Text, baseRank: 1000));

        return byName.StartWith(Array.Empty<QueryCompletionItem>())
            .CombineLatest(semantic.StartWith(Array.Empty<QueryCompletionItem>()),
                (a, b) => (IReadOnlyList<QueryCompletionItem>)a.Concat(b).ToArray())
            .Skip(1);
    }

    /// <summary>
    /// The query the semantic suggestions run: the box's first hidden sub-query plus the visible text as typed
    /// — its free-text words route to the vector index (where the store has one; otherwise they match
    /// lexically) — declared mesh-wide when nothing anchors it, so it is never refused.
    /// </summary>
    /// <param name="request">The completion request.</param>
    /// <returns>The query string.</returns>
    public static string SemanticQuery(QueryCompletionRequest request)
    {
        var anchor = (request.HiddenQuery ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        var query = $"{anchor} {request.Text}".Trim();
        var parser = new QueryParser();
        foreach (var q in new[] { "sort", "limit", "select" })
            query = parser.RemoveQualifier(query, q);
        var specified = parser.Parse(query).IsSufficientlySpecified();
        return $"{query}{(specified ? "" : " partitions:all")} {RowSelect}".Trim();
    }

    private static IReadOnlyList<QueryCompletionItem> NodeItems(
        IReadOnlyList<QueryNodeFact> nodes, string category, CompletionKind kind, int baseRank)
        => nodes.Select((n, i) =>
        {
            var insert = $"{QueryGrammar.Path}:{QueryGrammarCompleter.ValueText(n.Path)}";
            return new QueryCompletionItem(
                n.Name is { Length: > 0 } name ? name : n.Path, insert, kind, category,
                Description: n.NodeType, Path: n.Path, Icon: n.Icon, Rank: baseRank - i);
        }).ToArray();

    // ---------------------------------------------------------------- plumbing

    private IObservable<IReadOnlyList<QueryNodeFact>> Rows(string query, int limit)
        => Nodes(query, limit)
            .Select(nodes => (IReadOnlyList<QueryNodeFact>)nodes
                .Select(n => new QueryNodeFact(n.Path!, n.Name, n.NodeType, n.Icon))
                .ToArray());

    private IObservable<IReadOnlyList<MeshNode>> Nodes(string query, int limit)
        => Observable.Defer(() => mesh.Query<MeshNode>(new MeshQueryRequest { Query = query, Limit = limit }))
            .Take(1)
            .Select(change => (IReadOnlyList<MeshNode>)change.Items
                .Where(n => !string.IsNullOrEmpty(n.Path))
                .Take(limit)
                .ToArray());

    private TimeProvider Clock()
        => hub.ServiceProvider.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System;

    private IObservable<T> Bounded<T>(IObservable<T> source, string what)
        => Guarded(source.Timeout(SourceBudget), what);

    private IObservable<T> Guarded<T>(IObservable<T> source, string what)
        => source
            .Catch<T, Exception>(ex =>
            {
                // Best-effort by design: a source that fails or overruns contributes NOTHING to this one
                // suggest list and is reported — the grammar's own suggestions must still show.
                logger?.LogWarning(ex, "Query autocomplete: {Source} contributed nothing ({Error})", what, ex.Message);
                return Observable.Empty<T>();
            });

    private static string NameFilter(string prefix)
        => prefix.Length > 0 && prefix.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.')
            ? $" name:*{prefix}*"
            : "";

    private static QueryNodeFact Fact(QueryResult r) => new(r.Path, r.Name, r.NodeType, r.Icon, r.Score);

    private static bool Is(string? field, string name) => string.Equals(field, name, StringComparison.OrdinalIgnoreCase);

    private sealed class ResultComparer : IEqualityComparer<QueryCompletionResult>
    {
        public static readonly ResultComparer Instance = new();

        public bool Equals(QueryCompletionResult? x, QueryCompletionResult? y)
            => x is not null && y is not null
               && x.ReplaceStart == y.ReplaceStart && x.ReplaceEnd == y.ReplaceEnd
               && x.Items.SequenceEqual(y.Items);

        public int GetHashCode(QueryCompletionResult obj) => HashCode.Combine(obj.ReplaceStart, obj.Items.Count);
    }
}
