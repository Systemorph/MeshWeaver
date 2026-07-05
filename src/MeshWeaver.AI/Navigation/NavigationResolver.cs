using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Navigation;

/// <summary>
/// The reactive orchestration around the pure <see cref="NavigationTargetResolver"/>: it turns a raw
/// navigation row into a concrete <see cref="NavigationResolution"/> by checking node existence and running
/// the resilient search fallback / intent→skill match against the live mesh. Everything is
/// <see cref="IObservable{T}"/> — compose and Subscribe, never await (AsynchronousCalls.md). Both the
/// user-typed <c>/navigate</c> skill (client) and the agent's <c>NavigateTo</c> tool (server) call this,
/// so "take me there" resolves the same way no matter who asks.
/// </summary>
public sealed class NavigationResolver
{
    private const int SearchTimeoutSeconds = 5;
    // Bound on how long the authoritative single-node existence read waits before deciding "not there"
    // and falling back to search — short so a wrong path corrects quickly, long enough for the live read.
    private const int ExistsTimeoutSeconds = 2;

    private readonly IMessageHub hub;
    private readonly IMeshService mesh;
    private readonly ILogger<NavigationResolver> logger;

    /// <summary>Creates a resolver bound to the mesh services on <paramref name="hub"/>.</summary>
    public NavigationResolver(IMessageHub hub)
    {
        this.hub = hub;
        mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        logger = hub.ServiceProvider.GetRequiredService<ILogger<NavigationResolver>>();
    }

    /// <summary>
    /// Resolves a navigation row (the <c>/navigate</c> argument or the <c>NavigateTo</c> tool path) to a
    /// concrete target. Single path-shaped argument → direct path first (with URL correction and a resilient
    /// search fallback); free-text → an intent→skill match, else the best node search. Never throws and never
    /// dead-ends: a failed search yields an <see cref="NavigationTargetKind.Unresolved"/> result with
    /// alternatives, not an error.
    /// </summary>
    public IObservable<NavigationResolution> Resolve(string? row, string? contextPath = null)
    {
        switch (NavigationTargetResolver.Classify(row))
        {
            case NavigationInputKind.Empty:
                return Observable.Return(NavigationResolution.Unresolved("Nothing to navigate to."));
            case NavigationInputKind.DirectPath:
                return ResolveDirect(row!.Trim(), contextPath);
            default:
                return ResolvePhrase(row!.Trim());
        }
    }

    private IObservable<NavigationResolution> ResolveDirect(string row, string? contextPath)
    {
        var normalized = NavigationTargetResolver.NormalizePath(row);
        var corrected = !string.Equals(normalized, row.TrimStart('@'), StringComparison.Ordinal);

        // An app route (a page, e.g. /search?…) always "exists" and can't render in the side panel.
        if (NavigationTargetResolver.IsRouteLike(normalized))
            return Observable.Return(new NavigationResolution
            {
                Target = normalized,
                Kind = NavigationTargetKind.Route,
                WasCorrected = corrected,
            });

        var nodePath = normalized.TrimStart('/');

        // A BARE single-segment token may be relative to the thread's context ("@MyChild" under context
        // "ACME/Project"). Try it as an absolute path first, then under the context, before searching. A
        // multi-segment path or an explicit "@/absolute" is treated as absolute — no context prefix.
        var contextCandidate = !string.IsNullOrEmpty(contextPath)
                               && !nodePath.Contains('/')
                               && !row.TrimStart().StartsWith("@/", StringComparison.Ordinal)
            ? $"{contextPath!.Trim('/')}/{nodePath}"
            : null;

        return ReadExact(nodePath).SelectMany(node =>
        {
            if (node is not null)
                return Observable.Return(NodeResolution(node, corrected, null));
            if (contextCandidate is not null)
                return ReadExact(contextCandidate).SelectMany(ctxNode =>
                    ctxNode is not null
                        ? Observable.Return(NodeResolution(ctxNode, wasCorrected: true, null))
                        : SearchFallback(nodePath, row));
            return SearchFallback(nodePath, row);
        });
    }

    // Resilient fallback — the node isn't there verbatim, so find the closest real one, or report honestly.
    private IObservable<NavigationResolution> SearchFallback(string nodePath, string row)
    {
        var term = NavigationTargetResolver.LastSegment(nodePath);
        return SearchNodes(term, 20).Select(candidates =>
        {
            var best = NavigationTargetResolver.PickBest(nodePath, candidates)
                       ?? NavigationTargetResolver.PickBest(term, candidates);
            return best is not null
                ? NodeResolution(best, wasCorrected: true,
                    $"Couldn't find “{nodePath}” — opened the closest match “{best.Path}”.", candidates)
                : NavigationResolution.Unresolved($"Nothing in the mesh matches “{row}”.")
                    with { Alternatives = TopPaths(candidates) };
        });
    }

    // Authoritative single-node existence read (CqrsAndContentAccess.md: GetMeshNodeStream, not a lagged
    // query) — exact-path filtered so a routing fallback can't return an ancestor. Times out to null (the
    // node isn't there) → the caller runs the search fallback.
    private IObservable<MeshNode?> ReadExact(string nodePath) =>
        hub.GetWorkspace().GetMeshNodeStream(nodePath)
            .Where(n => n is not null
                        && string.Equals(n.Path, nodePath, StringComparison.OrdinalIgnoreCase))
            .Take(1)
            .Select(n => (MeshNode?)n)
            .Timeout(TimeSpan.FromSeconds(ExistsTimeoutSeconds))
            .Catch((Exception _) => Observable.Return((MeshNode?)null));

    private IObservable<NavigationResolution> ResolvePhrase(string phrase)
    {
        // 1) Prefer a SKILL — a skill can do more than a route change ("change my model" → /model).
        return SearchNodes("nodeType:Skill", 100)
            .SelectMany(skills =>
            {
                var skill = NavigationTargetResolver.PickBestSkill(phrase, skills);
                if (skill is not null)
                    return Observable.Return(new NavigationResolution
                    {
                        Target = skill.Path,
                        Kind = NavigationTargetKind.Skill,
                        WasCorrected = true,
                        Message = $"Runs {skill.Name ?? "/" + skill.Id}.",
                    });

                // 2) Otherwise, make sense of the phrase by searching the mesh for the best node.
                return SearchNodes(phrase, 20).Select(candidates =>
                {
                    var best = NavigationTargetResolver.PickBest(phrase, candidates);
                    return best is not null
                        ? NodeResolution(best, wasCorrected: true,
                            $"Opened the best match for “{phrase}”: “{best.Path}”.", candidates)
                        : NavigationResolution.Unresolved($"Couldn't find anything for “{phrase}”.")
                            with { Alternatives = TopPaths(candidates) };
                });
            });
    }

    private static NavigationResolution NodeResolution(
        MeshNode node, bool wasCorrected, string? message, IReadOnlyList<MeshNode>? alternatives = null) =>
        new()
        {
            Target = node.Path,
            Kind = NavigationTargetKind.Node,
            WasCorrected = wasCorrected,
            Message = message,
            Alternatives = alternatives is null ? [] : TopPaths(alternatives, exclude: node.Path),
        };

    private static IReadOnlyList<string> TopPaths(
        IReadOnlyList<MeshNode> nodes, string? exclude = null, int take = 3) =>
        nodes.Where(n => !string.IsNullOrEmpty(n.Path)
                    && !string.Equals(n.Path, exclude, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Path!).Distinct().Take(take).ToList();

    private IObservable<IReadOnlyList<MeshNode>> SearchNodes(string query, int limit) =>
        mesh.Query<MeshNode>(new MeshQueryRequest { Query = query, Limit = limit })
            .Take(1)
            .Select(change => (IReadOnlyList<MeshNode>)change.Items.ToList())
            .Timeout(TimeSpan.FromSeconds(SearchTimeoutSeconds))
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Navigation search failed for query {Query}", query);
                return Observable.Return((IReadOnlyList<MeshNode>)[]);
            });
}
