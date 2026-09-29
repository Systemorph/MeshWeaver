using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Testing.FaultInjection;

/// <summary>
/// The <b>first-frame delay</b>: an extra query provider that joins the mesh's fan-in for one
/// partition and says nothing until released. The fan-in (<c>MeshQuery</c>) waits for every
/// matching provider's first frame before it emits a combined <c>Initial</c>, bounded by
/// <c>MeshOperationOptions.QueryInitialBudget</c>, so holding this provider holds the FIRST FRAME of
/// every query into that partition — exactly the slow partition read of a rolling portal
/// (MeshWeaver.Plugins#2511 measured a first frame at 18 s against a 10 s grace).
///
/// <para>Queries that do not name <see cref="Partition"/> are answered at once with an empty frame, so
/// the rest of the mesh is untouched. Once released, the provider answers with an empty
/// <c>Initial</c> and stays open like any live provider; the real providers carry the rows.</para>
///
/// <para>Register it as an additional <see cref="IMeshQueryProvider"/> (see
/// <c>FaultInjectionMeshExtensions.AddHeldFirstFrame</c>) and hold it through <see cref="Hold"/>.</para>
/// </summary>
public sealed class HeldFirstFrameQueryProvider(string partition) : IMeshQueryProvider
{
    private FaultSwitch? _hold;

    /// <summary>The partition (first path segment) whose queries this provider can hold.</summary>
    public string Partition { get; } = partition;

    /// <inheritdoc />
    public string Name => $"FaultInjection.HeldFirstFrame({Partition})";

    /// <summary>
    /// From now on, the first frame of every query into <see cref="Partition"/> waits until the returned
    /// switch is released. Each held query is an arrival on the switch.
    /// </summary>
    public FaultSwitch Hold()
    {
        var hold = new FaultSwitch($"hold the first query frame of '{Partition}'");
        System.Threading.Interlocked.Exchange(ref _hold, hold)?.Release();
        return hold;
    }

    /// <inheritdoc />
    public bool Matches(IReadOnlyList<string> queryNamespaces)
        => queryNamespaces.Any(ns => Names(ns));

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        => Held(request, new QueryResultChange<T>
        {
            ChangeType = QueryChangeType.Initial,
            Items = Array.Empty<T>(),
            Timestamp = DateTimeOffset.UtcNow,
        });

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request, JsonSerializerOptions options)
        => Held(request, (IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath, string prefix, JsonSerializerOptions options,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
        string? contextPath = null, string? context = null)
        => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

    /// <inheritdoc />
    public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
        => Observable.Return<T?>(default);

    private bool Names(string text)
        => text.Equals(Partition, StringComparison.OrdinalIgnoreCase)
           || text.StartsWith(Partition + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The same partition test <see cref="Matches"/> applies (the query's <c>namespace:</c> values
    /// and its path's first segment, exactly as the fan-in extracts them), so the two can never
    /// disagree about which queries are held.
    /// </summary>
    private bool Targets(string query)
    {
        var parsed = new QueryParser().Parse(query);
        if (parsed.ExtractNamespaces().Any(Names))
            return true;
        return !string.IsNullOrEmpty(parsed.Path) && Names(parsed.Path);
    }

    private IObservable<TFrame> Held<TFrame>(MeshQueryRequest request, TFrame emptyFrame)
        => Observable.Defer(() =>
        {
            var frame = Observable.Return(emptyFrame).Concat(Observable.Never<TFrame>());
            var mine = request.EffectiveQueries.Any(Targets);
            return mine && System.Threading.Volatile.Read(ref _hold) is { IsClosed: true } hold
                ? hold.Gate(frame, $"first frame of '{string.Join(" | ", request.EffectiveQueries)}'")
                : frame;
        });
}
