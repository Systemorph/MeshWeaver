using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// In-process implementation of <see cref="IMeshChangeFeed"/> and
/// <see cref="IMeshInvalidationFeed"/>. Logical post-commit events use one subject; direct
/// publishes additionally fan into the cache subject. The storage adapter's process-local change
/// channel reaches only the cache subject, so monolith and Orleans hosts invalidate commits made by
/// every process without duplicating logical side effects. That storage channel is the ONLY
/// cross-process carrier: the Orleans memory-stream relay is retired (#4802), so a host that runs
/// more than one process over one store needs a backend that delivers every process's commits to
/// every process (PostgreSQL LISTEN/NOTIFY) — see <c>CrossProcessChangeDelivery</c>.
/// </summary>
public class InProcessMeshChangeFeed : IMeshChangeFeed, IMeshInvalidationFeed, IDisposable
{
    private readonly Subject<MeshChangeEvent> _subjectLifetime = new();
    private readonly Subject<MeshChangeEvent> _invalidationLifetime = new();
    private readonly ISubject<MeshChangeEvent> _subject;
    private readonly ISubject<MeshChangeEvent> _invalidations;
    private readonly StorageChangeFeedRelay? _storageRelay;
    private readonly Subject<MeshChangeEvent> _durableLifetime = new();
    private readonly ISubject<MeshChangeEvent> _durable;
    private readonly Subject<ChangeFeedGap> _gapsLifetime = new();
    private readonly ISubject<ChangeFeedGap> _gaps;
    private readonly IDisposable? _gapRelay;
    private readonly ILogger? logger;
    private bool _disposed;

    /// <summary>
    /// Creates a standalone in-process feed. Retained as a real parameterless constructor so
    /// already-compiled consumers keep their existing constructor binding.
    /// </summary>
    public InProcessMeshChangeFeed()
    {
        // Publish and PublishLocal have independent callers (hub threads and storage LISTEN). Rx Subject is not safe for concurrent OnNext calls; the
        // synchronized wrappers preserve one ordered callback at a time in this process.
        _subject = Subject.Synchronize(_subjectLifetime);
        _invalidations = Subject.Synchronize(_invalidationLifetime);
        _gaps = Subject.Synchronize(_gapsLifetime);
        _durable = Subject.Synchronize(_durableLifetime);
    }

    /// <summary>
    /// Creates the mesh-scoped feed and attaches the storage-backed per-process relay.
    /// Dependency injection selects this constructor in a persistence-enabled host; direct
    /// standalone callers keep using <see cref="InProcessMeshChangeFeed()"/>.
    /// </summary>
    public InProcessMeshChangeFeed(
        IStorageAdapter storage,
        ILogger<InProcessMeshChangeFeed>? logger = null) : this()
    {
        this.logger = logger;
        _storageRelay = new StorageChangeFeedRelay(storage, PublishInvalidation, logger);
        // Plugins#3000: the backend's declared holes reach every process-local cache through the
        // same mesh-scoped feed as its notifications. Logged once here at Warning because a gap is
        // the one event after which this replica may have been serving stale state.
        _gapRelay = storage.ChangeFeedGaps.Subscribe(
            PublishGap,
            ex => logger?.LogError(ex,
                "Storage change-feed GAP relay stopped — this process will no longer be told when "
                + "its cross-process change feed lost notifications"));
    }

    /// <inheritdoc />
    /// <remarks>
    /// 🚨 Each subscriber is ISOLATED, exactly as <see cref="IMeshInvalidationFeed.Subscribe"/>
    /// callbacks are: a raw Subject aborts its fan-out at the first observer that throws, so one
    /// cache faulting while it drops its state (or one torn down mid-delivery) would keep every
    /// later subscriber stale — the very failure a gap exists to end.
    /// </remarks>
    public IObservable<ChangeFeedGap> Gaps => Observable.Create<ChangeFeedGap>(observer =>
        _gaps.Subscribe(
            gap =>
            {
                try
                {
                    observer.OnNext(gap);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex,
                        "Mesh change-feed gap subscriber failed for '{Source}'; continuing with the others",
                        gap.Source);
                }
            },
            observer.OnError,
            observer.OnCompleted));

    private void PublishGap(ChangeFeedGap gap)
    {
        if (_disposed)
            return;
        logger?.LogWarning(
            "Change feed '{Source}' lost delivery between {LostAt:O} and {ResumedAt:O} ({Reason}) — "
            + "commits made by other processes in that window were never announced here; "
            + "process-local caches now re-read their authoritative state",
            gap.Source, gap.LostAt, gap.ResumedAt, gap.Reason ?? "no reason given");
        // Subscribers are isolated in Gaps; this catch is only the backstop that keeps anything
        // escaping the subject itself from reaching the backend's listener loop, where it would
        // read as a connection error and cost another reconnect.
        try
        {
            _gaps.OnNext(gap);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex,
                "Delivering the change-feed gap for '{Source}' faulted", gap.Source);
        }
    }

    /// <summary>
    /// Publishes a mesh change event to all local subscribers (no-op once disposed).
    /// </summary>
    /// <param name="change">The change event to publish.</param>
    public void Publish(MeshChangeEvent change)
    {
        if (_disposed)
            return;
        _subject.OnNext(change);
        _invalidations.OnNext(change);
    }

    /// <summary>
    /// Publishes an event another process committed to this process's invalidators only, without
    /// re-running logical consumers. The storage relay is the platform's own caller; the public
    /// signature is kept for already-compiled callers (the retired Orleans stream relay used it).
    /// </summary>
    public void PublishLocal(MeshChangeEvent change)
    {
        PublishInvalidation(change);
    }

    /// <summary>
    /// Publishes only to process-local cache invalidators. Storage relays use this path so a
    /// database echo cannot re-run logical side effects such as mail or instance synchronization.
    /// </summary>
    internal void PublishInvalidation(MeshChangeEvent change)
    {
        if (_disposed)
            return;
        _invalidations.OnNext(change);
        // Only this path is a commit the store announced; a direct Publish is not (DurableCommits).
        _durable.OnNext(change);
    }

    /// <inheritdoc />
    /// <remarks>Each subscriber isolated, as <see cref="Gaps"/> is: one that throws must not keep the
    /// next from seeing the commit.</remarks>
    public IObservable<MeshChangeEvent> DurableCommits => Observable.Create<MeshChangeEvent>(observer =>
        _durable.Subscribe(
            change =>
            {
                try
                {
                    observer.OnNext(change);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex,
                        "Mesh durable-commit subscriber failed for {Kind} {Path}; continuing with the others",
                        change.Kind, change.Path);
                }
            },
            observer.OnError,
            observer.OnCompleted));

    /// <summary>
    /// Subscribes a handler to mesh change events, optionally filtered by change kind.
    /// </summary>
    /// <param name="handler">The callback invoked for each matching change event.</param>
    /// <param name="filter">When set, only events of this kind are delivered; otherwise all events are delivered.</param>
    /// <returns>A disposable that ends the subscription when disposed.</returns>
    public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
        => Subscribe(_subject, handler, filter, null);

    IDisposable IMeshInvalidationFeed.Subscribe(
        Action<MeshChangeEvent> handler,
        MeshChangeKind? filter)
        => Subscribe(_invalidations, handler, filter, "invalidation");

    private IDisposable Subscribe(
        ISubject<MeshChangeEvent> subject,
        Action<MeshChangeEvent> handler,
        MeshChangeKind? filter,
        string? channel)
    {
        return subject.Subscribe(e =>
        {
            if (filter is not null && e.Kind != filter.Value)
                return;
            // Preserve the logical feed's existing failure semantics: a logical consumer can be
            // part of a post-commit operation whose caller must see its failure. Invalidation is
            // idempotent process-local maintenance, so one bad cache must not starve the others.
            if (channel is null)
            {
                handler(e);
                return;
            }
            try
            {
                handler(e);
            }
            catch (Exception ex)
            {
                // One cache must not prevent the remaining process-local subscribers from seeing
                // this commit, nor tear down the feed for later events.
                logger?.LogError(ex,
                    "Mesh {Channel} feed subscriber failed for {Kind} {Path}; continuing",
                    channel, e.Kind, e.Path);
            }
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _storageRelay?.Dispose();
        _gapRelay?.Dispose();
        _gaps.OnCompleted();
        _gapsLifetime.Dispose();
        _durable.OnCompleted();
        _durableLifetime.Dispose();
        _subject.OnCompleted();
        _invalidations.OnCompleted();
        _subjectLifetime.Dispose();
        _invalidationLifetime.Dispose();
    }
}
