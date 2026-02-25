using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Scoped wrapper that automatically injects JsonSerializerOptions from the current IMessageHub
/// and aggregates results from all registered IMeshQueryProvider instances.
/// For source:activity queries, providers that support it (e.g. PostgreSQL) handle activity
/// ordering via SQL JOIN; other providers return normal results.
/// </summary>
public class MeshQuery(
    IEnumerable<IMeshQueryProvider> providers,
    IMessageHub hub) : IMeshQuery
{
    private JsonSerializerOptions Options => hub.JsonSerializerOptions;

    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Always delegate to providers. For source:activity queries, providers that
        // support it (e.g. PostgreSQL) will handle activity ordering via SQL JOIN.
        // Providers that don't understand it will return normal results (source: is
        // a reserved qualifier stripped by the parser, so it doesn't affect filters).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            await foreach (var item in provider.QueryAsync(request, Options, ct))
            {
                if (item is MeshNode node)
                {
                    if (!seen.Add(node.Path))
                        continue; // deduplicate by path, first provider wins
                }
                yield return item;
            }
        }
    }

    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        int limit = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var all = new List<QuerySuggestion>();

        foreach (var provider in providers)
        {
            await foreach (var suggestion in provider.AutocompleteAsync(basePath, prefix, Options, limit, ct))
                all.Add(suggestion);
        }

        foreach (var suggestion in all
            .OrderBy(s => s.Path.Length)
            .ThenByDescending(s => s.Score)
            .ThenBy(s => s.Name)
            .Take(limit))
        {
            yield return suggestion;
        }
    }

    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        string? context = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var all = new List<QuerySuggestion>();

        foreach (var provider in providers)
        {
            await foreach (var suggestion in provider.AutocompleteAsync(basePath, prefix, Options, mode, limit, contextPath, context, ct))
                all.Add(suggestion);
        }

        IEnumerable<QuerySuggestion> ordered = mode switch
        {
            AutocompleteMode.RelevanceFirst => all
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Path.Length)
                .ThenBy(s => s.Name),
            _ => all
                .OrderBy(s => s.Path.Length)
                .ThenByDescending(s => s.Score)
                .ThenBy(s => s.Name)
        };

        foreach (var suggestion in ordered.Take(limit))
        {
            yield return suggestion;
        }
    }

    public IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request)
    {
        // Collect Initial from all providers, merge into a single Initial emission,
        // then forward subsequent (non-Initial) changes from ongoing providers.
        var observables = providers
            .Select(p => p.ObserveQuery<T>(request, Options))
            .ToList();

        if (observables.Count == 0)
            return Observable.Empty<QueryResultChange<T>>();

        if (observables.Count == 1)
            return observables[0];

        return Observable.Create<QueryResultChange<T>>(observer =>
        {
            var initialItems = new List<T>();
            var initialCount = 0;
            var initialTarget = observables.Count;
            ParsedQuery? lastQuery = null;
            var gate = new object();

            var subscriptions = new List<IDisposable>();

            for (var i = 0; i < observables.Count; i++)
            {
                var obs = observables[i];
                var sub = obs.Subscribe(
                    change =>
                    {
                        if (change.ChangeType == QueryChangeType.Initial)
                        {
                            lock (gate)
                            {
                                initialItems.AddRange(change.Items);
                                lastQuery ??= change.Query;
                                initialCount++;

                                if (initialCount == initialTarget)
                                {
                                    observer.OnNext(change with { Items = initialItems.ToList() });
                                }
                            }
                        }
                        else
                        {
                            observer.OnNext(change);
                        }
                    },
                    ex => observer.OnError(ex));

                subscriptions.Add(sub);
            }

            return new System.Reactive.Disposables.CompositeDisposable(subscriptions);
        });
    }

    public async Task<T?> SelectAsync<T>(string path, string property, CancellationToken ct = default)
    {
        foreach (var provider in providers)
        {
            var result = await provider.SelectAsync<T>(path, property, Options, ct);
            if (result != null)
                return result;
        }
        return default;
    }
}
