using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.ControlLane;

/// <summary>
/// ONE complete reading of one query — the rows, and whether they are an ANSWER. Ported from
/// MeshWeaver.Plugins' <c>InstanceActionControlPlane.IndexReading</c> so the lane's operations
/// read exactly as the in-process actions do: a reading that faulted, never produced a complete
/// frame, had a silent provider, or read over NO partition is a FLOOR and is never read as "none".
/// </summary>
/// <param name="Query">What was asked.</param>
/// <param name="Rows">What it listed.</param>
/// <param name="Answered">Whether a complete frame arrived.</param>
/// <param name="Partitions">What the store says it read from; null = not reported (unknown).</param>
/// <param name="SilentProviders">Providers counted as empty without answering.</param>
/// <param name="Fault">Why the read failed, when it did.</param>
public sealed record MeshReading(
    string Query, ImmutableList<MeshNode> Rows, bool Answered,
    IReadOnlyList<string>? Partitions, IReadOnlyList<string> SilentProviders, string? Fault = null)
{
    /// <summary>Budget for one complete reading.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(90);

    /// <summary>True when the rows are a real answer — the only kind that may be read as "none here".</summary>
    public bool IsAnswer => Answered && Fault is null && SilentProviders.Count == 0 && Partitions is not { Count: 0 };

    /// <summary>Why the rows are not an answer; null when they are. Pure.</summary>
    public string? WhyNotAnAnswer =>
        Fault is not null ? $"the read failed — {Fault}"
        : !Answered ? "the query completed without a complete frame"
        : SilentProviders.Count > 0
            ? $"provider(s) {string.Join(", ", SilentProviders)} completed without answering and were counted as empty, so the frame is a floor"
        : Partitions is { Count: 0 } ? "the store read from NO partition — zero rows over nothing is not an answer"
        : null;

    /// <summary>
    /// Reads <paramref name="query"/> AS SYSTEM, complete (no clip), as ONE reading. Never errors:
    /// a fault, a timeout or an empty completion is a reading that is not an answer. A fault after a
    /// complete frame keeps the rows it delivered (they are still positive evidence). Cold; the
    /// caller supplies the system identity on the subscribing thread.
    /// </summary>
    public static IObservable<MeshReading> Read(IMeshService mesh, string query, TimeSpan? budget = null) =>
        mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query).AsSystem().RequireViewer().Complete())
            .Materialize()
            .Scan(new Frame(ImmutableDictionary<string, MeshNode>.Empty, false, null, [], null), Fold)
            .Where(frame => frame.Complete || frame.Fault is not null)
            .Select(frame => frame.Reading(query))
            .Take(1)
            .Timeout(budget ?? DefaultBudget)
            .DefaultIfEmpty(new MeshReading(query, [], false, null, []))
            .Catch((Exception ex) => Observable.Return(new MeshReading(query, [], false, null, [],
                $"{ex.GetType().Name}: {ex.Message}")));

    private sealed record Frame(
        ImmutableDictionary<string, MeshNode> Rows, bool Complete,
        IReadOnlyList<string>? Partitions, IReadOnlyList<string> SilentProviders, string? Fault)
    {
        public MeshReading Reading(string query) =>
            new(query, Rows.Values.ToImmutableList(), Complete, Partitions, SilentProviders, Fault);
    }

    private static Frame Fold(Frame state, Notification<QueryResultChange<MeshNode>> notification) =>
        notification.Kind switch
        {
            NotificationKind.OnError => state with
            {
                Fault = $"{notification.Exception!.GetType().Name}: {notification.Exception.Message}",
            },
            NotificationKind.OnNext when notification.Value.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset =>
                new Frame(Accumulate(ImmutableDictionary<string, MeshNode>.Empty, notification.Value), true,
                    notification.Value.Partitions, notification.Value.SilentProviders ?? (IReadOnlyList<string>)[], state.Fault),
            NotificationKind.OnNext => state with { Rows = Accumulate(state.Rows, notification.Value) },
            _ => state,
        };

    private static ImmutableDictionary<string, MeshNode> Accumulate(
        ImmutableDictionary<string, MeshNode> rows, QueryResultChange<MeshNode> change)
    {
        foreach (var item in change.Items)
            rows = change.ChangeType == QueryChangeType.Removed ? rows.Remove(item.Path) : rows.SetItem(item.Path, item);
        return rows;
    }
}
