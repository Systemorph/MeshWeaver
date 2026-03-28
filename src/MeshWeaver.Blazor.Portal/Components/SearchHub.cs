using System.Collections.Concurrent;
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
                await foreach (var s in meshService.AutocompleteAsync(
                    "", "", AutocompleteMode.RelevanceFirst, req.MaxResults,
                    req.ContextPath, context: "search", ct))
                    await pending.Writer.WriteAsync(s, ct);
            }
            else if (req.Input.StartsWith('@'))
            {
                var afterAt = req.Input[1..];
                var lastSlash = afterAt.LastIndexOf('/');
                var basePath = lastSlash >= 0 ? afterAt[..lastSlash] : "";
                var prefix = lastSlash >= 0 ? afterAt[(lastSlash + 1)..] : afterAt;

                await foreach (var s in meshService.AutocompleteAsync(
                    basePath, prefix, AutocompleteMode.RelevanceFirst, req.MaxResults,
                    req.ContextPath, context: "search", ct))
                    await pending.Writer.WriteAsync(s, ct);
            }
            else
            {
                await ExecuteTextSearchAsync(meshService, req, pending, ct);
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
    /// Free-text search: fetches a wider candidate pool from QueryAsync,
    /// scores each result by where the search terms match (name > nodeType > path > content),
    /// adds proximity boost, then streams results ordered by score.
    /// </summary>
    private static async Task ExecuteTextSearchAsync(
        IMeshService meshService, SearchRequest req, PendingSearch pending, CancellationToken ct)
    {
        // Fetch a wider pool so scoring can surface the best matches
        var query = $"*{req.Input}* scope:descendants context:search is:main limit:{CandidatePoolSize}";
        var candidates = new List<QuerySuggestion>();

        await foreach (var obj in meshService.QueryAsync(
            new MeshQueryRequest { Query = query }, ct))
        {
            if (obj is MeshNode n)
            {
                var score = ComputeRelevanceScore(n, req.Input!, req.ContextPath);
                candidates.Add(new QuerySuggestion(
                    n.Path ?? "",
                    n.Name ?? n.Id ?? "",
                    n.NodeType,
                    score,
                    n.Icon));
            }
        }

        // Sort by score descending and stream top results
        foreach (var s in candidates.OrderByDescending(c => c.Score).Take(req.MaxResults))
            await pending.Writer.WriteAsync(s, ct);
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
