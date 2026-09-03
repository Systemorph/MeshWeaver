using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The mesh-scoped live catalog of <see cref="UiContribution"/> nodes — ONE query subscription per
/// silo, shared by every per-node hub's menu aggregation (the <c>DeckSlidesCache</c> pattern:
/// per-hub consumers map a shared replayed stream instead of each holding its own query).
///
/// <para>Fully REACTIVE: the query stream's Initial/Reset carry the complete set and
/// Added/Updated/Removed fold as deltas, so a plugin installing or updating contributions changes
/// every open menu without any generation counter, hub recycle or watchdog (the design's
/// snapshot+bump alternative in #1645 is superseded by this — simpler and it can't go stale).
/// Read under the SYSTEM identity: contributions are platform UI data readable by construction;
/// per-VIEWER gating (permission + admin) happens in the aggregator against the viewer's own
/// streams, never here.</para>
///
/// <para>Fault-tolerant: a query error logs and degrades to the LAST known set (or empty), then
/// the subscription retries on the next subscriber epoch — the menu keeps rendering its compiled
/// items no matter what this catalog does.</para>
/// </summary>
public sealed class UiContributionCatalog : IDisposable
{
    private readonly IMessageHub hub;
    private readonly ILogger<UiContributionCatalog>? logger;
    private readonly BehaviorSubject<ImmutableDictionary<string, (MeshNode Node, UiContribution Content)>> state =
        new(ImmutableDictionary<string, (MeshNode, UiContribution)>.Empty);
    private readonly object gate = new();
    private IDisposable? subscription;

    /// <summary>Creates the catalog; the query subscription starts lazily on first read.</summary>
    public UiContributionCatalog(IMessageHub hub, ILogger<UiContributionCatalog>? logger = null)
    {
        this.hub = hub;
        this.logger = logger;
    }

    /// <summary>
    /// The live contribution set, replayed to every subscriber. First access starts the shared
    /// query subscription.
    /// </summary>
    public IObservable<ImmutableList<(MeshNode Node, UiContribution Content)>> Contributions
    {
        get
        {
            EnsureSubscribed();
            return state.Select(s => s.Values.ToImmutableList());
        }
    }

    /// <summary>
    /// Synchronous snapshot of the current set — for callers that enumerate contexts at RENDER
    /// time (the menu renderer's context list). First access starts the shared subscription, so a
    /// cold snapshot may be empty; the renderer re-runs on every area render, which picks up the
    /// warmed set.
    /// </summary>
    public ImmutableList<(MeshNode Node, UiContribution Content)> Current
    {
        get
        {
            EnsureSubscribed();
            return state.Value.Values.ToImmutableList();
        }
    }

    private void EnsureSubscribed()
    {
        if (subscription is not null)
            return;
        lock (gate)
        {
            if (subscription is not null)
                return;
            var meshService = hub.ServiceProvider.GetService(typeof(IMeshService)) as IMeshService;
            var accessService = hub.ServiceProvider.GetService(typeof(AccessService)) as AccessService;
            if (meshService is null)
            {
                // A mesh without the query surface (minimal fixtures) — the catalog stays empty
                // and the menu renders its compiled items only.
                subscription = System.Reactive.Disposables.Disposable.Empty;
                return;
            }

            IDisposable Subscribe()
            {
                using (accessService?.ImpersonateAsSystem())
                {
                    return meshService
                        // Contributions are authored wherever the contributing plugin lives, so the
                        // catalog is mesh-wide by nature (#3202 — fan-out is opt-in).
                        .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(UiContributionNodeType.NodeType)))
                        .Subscribe(OnChange, ex =>
                        {
                            // Degrade to the last known set; the compiled menu is unaffected.
                            logger?.LogWarning(ex, "UiContribution catalog query faulted; menu contributions frozen at the last known set");
                        });
                }
            }

            subscription = Subscribe();
        }
    }

    private void OnChange(QueryResultChange<MeshNode> change)
    {
        var current = state.Value;
        switch (change.ChangeType)
        {
            case QueryChangeType.Initial:
            case QueryChangeType.Reset:
                current = ImmutableDictionary<string, (MeshNode, UiContribution)>.Empty;
                foreach (var node in change.Items ?? [])
                    current = Fold(current, node);
                break;
            case QueryChangeType.Added:
            case QueryChangeType.Updated:
                foreach (var node in change.Items ?? [])
                    current = Fold(current, node);
                break;
            case QueryChangeType.Removed:
                foreach (var node in change.Items ?? [])
                    if (node?.Path is { Length: > 0 } removed)
                        current = current.Remove(removed);
                break;
        }
        state.OnNext(current);
    }

    private ImmutableDictionary<string, (MeshNode, UiContribution)> Fold(
        ImmutableDictionary<string, (MeshNode, UiContribution)> current, MeshNode? node)
    {
        if (node?.Path is not { Length: > 0 } path)
            return current;
        // ContentAs is the bad-data-tolerant read; an unreadable contribution logs and is skipped —
        // one broken node never takes the catalog down.
        var content = node.ContentAs<UiContribution>(hub.JsonSerializerOptions, logger);
        return content is null ? current.Remove(path) : current.SetItem(path, (node, content));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        subscription?.Dispose();
        state.OnCompleted();
        state.Dispose();
    }
}
