using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Channels;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Portal.Components;

/// <summary>
/// Hosted hub that executes search queries off the Blazor UI thread.
/// The handler runs on the hub's own scheduler, streaming results
/// back to the caller via a Channel stored in a side-table (not in the message,
/// because the delivery pipeline touches serialization).
/// </summary>
internal sealed class SearchHub
{
    /// <summary>
    /// Fetch more candidates than displayed so relevance scoring can surface
    /// the best matches even if the DB returns them in a different order.
    /// </summary>
    private const int CandidatePoolSize = 50;

    private readonly IMessageHub _hub;

    /// <summary>
    /// Side-table mapping correlation id -> channel + cancellation.
    /// Kept out of the message to avoid serialization of non-serializable types.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PendingSearch> Pending = new();

    public SearchHub(IMessageHub parentHub)
    {
        _hub = parentHub.GetHostedHub(
            new Address($"{parentHub.Address}/_Search"),
            config => config.WithHandler<SearchRequest>(ExecuteSearchAsync));
    }

    /// <summary>
    /// Posts a search request to the hosted hub and returns an async enumerable
    /// that yields results as the hub handler streams them back via Channel.
    /// The caller's thread is free between yields.
    /// </summary>
    public IAsyncEnumerable<QuerySuggestion> SearchAsync(
        string? input, string? contextPath, int maxResults, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateUnbounded<QuerySuggestion>();
        Pending[id] = new PendingSearch(channel.Writer, ct);

        _hub.Post(new SearchRequest(id, input?.Trim(), contextPath, maxResults));

        return ReadAndCleanup(id, channel.Reader, ct);
    }

    private static async IAsyncEnumerable<QuerySuggestion> ReadAndCleanup(
        string id,
        ChannelReader<QuerySuggestion> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(ct))
                yield return item;
        }
        finally
        {
            Pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Runs on the hosted hub's scheduler — not the Blazor UI thread.
    /// Looks up the Channel and CancellationToken from the side-table.
    /// </summary>
    private static async Task<IMessageDelivery> ExecuteSearchAsync(
        IMessageHub hub, IMessageDelivery<SearchRequest> delivery, CancellationToken hubCt)
    {
        var req = delivery.Message;

        if (!Pending.TryGetValue(req.Id, out var pending))
            return delivery.Processed();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(pending.Ct, hubCt);
        var ct = linked.Token;

        try
        {
            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

            if (string.IsNullOrEmpty(req.Input))
            {
                // Empty input: show recently accessed items ordered by last access time.
                // Subscribe-based — await on a hub-touching observable deadlocks the
                // hub pump (see AsynchronousCalls.md).
                var query = $"source:accessed scope:descendants is:main sort:LastModified-desc context:search limit:{req.MaxResults}";
                StreamInitialResults(meshService, query, n => new QuerySuggestion(
                    n.Path ?? "",
                    n.Name ?? n.Id ?? "",
                    n.NodeType,
                    0,
                    n.Icon), pending);
            }
            else if (req.Input.StartsWith('@'))
            {
                var afterAt = req.Input[1..];
                var lastSlash = afterAt.LastIndexOf('/');
                var basePath = lastSlash >= 0 ? afterAt[..lastSlash] : "";
                var prefix = lastSlash >= 0 ? afterAt[(lastSlash + 1)..] : afterAt;

                // IObservable surface — collect once at the boundary then push
                // into the channel. No await foreach (producer is IObservable now).
                var items = await meshService.AutocompleteAsync(
                        basePath, prefix, AutocompleteMode.RelevanceFirst, req.MaxResults,
                        req.ContextPath, context: "search")
                    .ToList()
                    .ToTask(ct);
                foreach (var s in items)
                    await pending.Writer.WriteAsync(s, ct);
            }
            else
            {
                ExecuteTextSearch(meshService, req, pending);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            pending.Writer.Complete();
        }

        return delivery.Processed();
    }

    /// <summary>
    /// Free-text search: subscribes to <see cref="IMeshService.ObserveQuery{T}"/>
    /// for the initial candidate pool, scores each row by match quality, and
    /// pumps the top-N scored results into the pending channel. Fire-and-forget
    /// subscription — no <c>await</c> on hub-touching observables.
    /// </summary>
    private static void ExecuteTextSearch(
        IMeshService meshService, SearchRequest req, PendingSearch pending)
    {
        var query = $"*{req.Input}* scope:descendants context:search is:main limit:{CandidatePoolSize}";
        meshService.ObserveQuery<MeshNode>(new MeshQueryRequest { Query = query })
            .Take(1)
            .Subscribe(
                change =>
                {
                    var candidates = new List<QuerySuggestion>();
                    foreach (var n in change.Items)
                    {
                        var score = ComputeRelevanceScore(n, req.Input!, req.ContextPath);
                        candidates.Add(new QuerySuggestion(
                            n.Path ?? "",
                            n.Name ?? n.Id ?? "",
                            n.NodeType,
                            score,
                            n.Icon));
                    }
                    foreach (var s in candidates.OrderByDescending(c => c.Score).Take(req.MaxResults))
                        pending.Writer.TryWrite(s);
                });
    }

    /// <summary>
    /// Helper: subscribe to a query's initial emission, project each MeshNode
    /// into a <see cref="QuerySuggestion"/>, and write to the pending channel.
    /// Fire-and-forget — no await on hub-touching observables.
    /// </summary>
    private static void StreamInitialResults(
        IMeshService meshService,
        string query,
        Func<MeshNode, QuerySuggestion> project,
        PendingSearch pending)
    {
        meshService.ObserveQuery<MeshNode>(new MeshQueryRequest { Query = query })
            .Take(1)
            .Subscribe(change =>
            {
                foreach (var n in change.Items)
                    pending.Writer.TryWrite(project(n));
            });
    }

    /// <summary>
    /// Scores a MeshNode by how well it matches the search input.
    /// Name matches score highest — this is the "goodness of match" measure.
    /// Uses the same tier structure as autocomplete scoring.
    /// </summary>
    private static double ComputeRelevanceScore(MeshNode node, string searchInput, string? contextPath)
    {
        var name = node.Name ?? "";
        var terms = searchInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        double totalScore = 0;
        var scoredTerms = 0;

        foreach (var rawTerm in terms)
        {
            var term = rawTerm.Trim('*');
            if (string.IsNullOrEmpty(term)) continue;

            scoredTerms++;
            if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                totalScore += 100;
            else if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
                totalScore += 80;
            else if ((node.Path ?? "").Contains(term, StringComparison.OrdinalIgnoreCase))
                totalScore += 20;
            else if ((node.NodeType ?? "").Contains(term, StringComparison.OrdinalIgnoreCase))
                totalScore += 10;
            else
                totalScore += 1; // matched in content/description
        }

        // Normalize so multi-word queries don't get inflated scores
        var score = scoredTerms > 0 ? totalScore / scoredTerms : 1;

        // Proximity boost: nodes closer to the user's current context rank higher
        score += PathProximity.ComputeBoost(contextPath, node.Path);

        return score;
    }

    /// <summary>
    /// Serialization-safe message — only contains plain data types.
    /// </summary>
    private record SearchRequest(string Id, string? Input, string? ContextPath, int MaxResults);

    private record PendingSearch(ChannelWriter<QuerySuggestion> Writer, CancellationToken Ct);
}
