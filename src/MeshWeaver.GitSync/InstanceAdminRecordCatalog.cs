using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The live set of one kind of record an instance's global administrators keep in the
/// <c>Admin</c> partition — the instance's package sources, its GitHub App's metadata
/// (<c>Doc/Architecture/InstanceRepositoryAccess</c>). A mesh-scoped singleton holding ONE query
/// subscription (<c>namespace:Admin nodeType:{type}</c>, as system), in the shape of the Instance
/// Secrets catalog: a standing watch that re-opens itself after a fault, and a
/// <see cref="WhenLoaded"/> that tells "nothing is stored" from "not read yet".
///
/// <para>🚨 Why a query and not a point read: a record may not EXIST yet (no source added, no App
/// connected), and a point read of an absent node is a routing NotFound that also trips the path's
/// storm-breaker — which then fast-fails the very write that would create it. The listing answers
/// existence; the content rides on it.</para>
/// </summary>
/// <typeparam name="TContent">The record's content type.</typeparam>
public abstract class InstanceAdminRecordCatalog<TContent> : IDisposable where TContent : class
{
    private readonly IMessageHub hub;
    private readonly ILogger? logger;
    private readonly string query;
    private readonly string name;
    private readonly BehaviorSubject<ImmutableDictionary<string, TContent>> state =
        new(ImmutableDictionary<string, TContent>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));
    private readonly BehaviorSubject<bool> loaded = new(false);
    private readonly object gate = new();
    private IDisposable? subscription;

    /// <summary>Creates the catalog over <paramref name="nodeType"/> in the <c>Admin</c> partition.</summary>
    /// <param name="hub">The mesh hub.</param>
    /// <param name="nodeType">The record's node type.</param>
    /// <param name="logger">Optional logger.</param>
    protected InstanceAdminRecordCatalog(IMessageHub hub, string nodeType, ILogger? logger)
    {
        this.hub = hub;
        this.logger = logger;
        name = nodeType;
        query = $"namespace:{InstanceAdminRecords.Partition} nodeType:{nodeType}";
    }

    /// <summary>The hub the catalog reads on.</summary>
    protected IMessageHub Hub => hub;

    /// <summary>Every record, keyed by node path. Empty until the first listing.</summary>
    public ImmutableDictionary<string, TContent> Entries
    {
        get
        {
            EnsureSubscribed();
            return state.Value;
        }
    }

    /// <summary>The record at <paramref name="path"/>, or null.</summary>
    public TContent? Entry(string path) =>
        Entries.TryGetValue(path, out var content) ? content : null;

    /// <summary>Emits the set on subscribe and whenever it changes.</summary>
    public IObservable<ImmutableDictionary<string, TContent>> Changes
    {
        get
        {
            EnsureSubscribed();
            return state.AsObservable();
        }
    }

    /// <summary>Emits once when the first listing has arrived — immediately if it already has.</summary>
    public IObservable<Unit> WhenLoaded
    {
        get
        {
            EnsureSubscribed();
            return loaded.Where(l => l).Take(1).Select(_ => Unit.Default);
        }
    }

    /// <summary>Starts the query now rather than at the first read. Idempotent.</summary>
    public void Start() => EnsureSubscribed();

    private void EnsureSubscribed()
    {
        if (subscription is not null)
            return;
        lock (gate)
        {
            if (subscription is not null)
                return;
            var meshService = hub.ServiceProvider.GetService<IMeshService>();
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            if (meshService is null || accessService is null)
            {
                subscription = System.Reactive.Disposables.Disposable.Empty;
                loaded.OnNext(true);
                return;
            }
            subscription = ActivityControlPlaneExtensions.SubscribeHubWatcher(
                hub,
                () => Observable.Defer(() => accessService.RunAsSystem(
                        () => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query))))
                    .SelectMany(RejectIncompleteListing),
                OnChange,
                logger,
                $"[{name}] catalog query");
        }
    }

    /// <summary>
    /// 🚨 A full listing that is only a FLOOR is a fault, never a listing (the Instance Secrets
    /// catalog's rule): an Initial/Reset whose fan-in names silent providers, or that is marked
    /// incomplete, may be MISSING records — folded, it would read an unread source or a pending App
    /// flow as absent, drop a default package or refuse a valid callback. It goes down the watcher's
    /// error path instead, which re-opens the query; the previous snapshot stays, and a catalog that
    /// never listed completely stays not-loaded.
    /// </summary>
    internal static IObservable<QueryResultChange<MeshNode>> RejectIncompleteListing(QueryResultChange<MeshNode> change)
    {
        if (change.ChangeType is not (QueryChangeType.Initial or QueryChangeType.Reset))
            return Observable.Return(change);
        if (change.SilentProviders is { Count: > 0 } silent)
            return Observable.Throw<QueryResultChange<MeshNode>>(new InvalidOperationException(
                $"the listing is INCOMPLETE: query provider(s) [{string.Join(", ", silent)}] did not answer"));
        if (change.SnapshotIncomplete)
            return Observable.Throw<QueryResultChange<MeshNode>>(new InvalidOperationException(
                "the listing is INCOMPLETE: a provider dropped part of its read"));
        return Observable.Return(change);
    }

    private void OnChange(QueryResultChange<MeshNode> change)
    {
        var current = state.Value;
        switch (change.ChangeType)
        {
            case QueryChangeType.Initial:
            case QueryChangeType.Reset:
                current = current.Clear();
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
                    if (node?.Path is { } path)
                        current = current.Remove(path);
                break;
        }
        state.OnNext(current);
        if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset && !loaded.Value)
            loaded.OnNext(true);
    }

    private ImmutableDictionary<string, TContent> Fold(ImmutableDictionary<string, TContent> current, MeshNode? node)
    {
        if (node?.Path is not { Length: > 0 } path)
            return current;
        var content = node.ContentAs<TContent>(hub.JsonSerializerOptions, logger);
        return content is null || !Accepts(path, content) ? current.Remove(path) : current.SetItem(path, content);
    }

    /// <summary>
    /// Whether a listed record is taken into the set. A record must sit where its own content says
    /// it belongs, so a node elsewhere under <c>Admin</c> cannot shadow the real one. Pure.
    /// </summary>
    /// <param name="path">The node's path.</param>
    /// <param name="content">The node's content.</param>
    protected abstract bool Accepts(string path, TContent content);

    /// <inheritdoc />
    public void Dispose()
    {
        subscription?.Dispose();
        state.OnCompleted();
        state.Dispose();
        loaded.OnCompleted();
        loaded.Dispose();
    }
}

/// <summary>Shared constants and the one write path of the instance's own admin records.</summary>
public static class InstanceAdminRecords
{
    /// <summary>The partition the records live in.</summary>
    public const string Partition = "Admin";

    /// <summary>
    /// Runs <paramref name="write"/> as system once the CALLER is confirmed a global administrator —
    /// rights first, as the caller, and only then the write (policy
    /// <c>authorize-as-caller-execute-as-system</c>). A caller who is not a global administrator gets
    /// <paramref name="refusal"/>, and nothing is written. Cold.
    /// </summary>
    /// <typeparam name="T">The write's result.</typeparam>
    /// <param name="hub">The caller's hub. The caller's identity is read at SUBSCRIBE time.</param>
    /// <param name="refusal">The refusal for a caller who is not a global administrator.</param>
    /// <param name="write">The write, given the caller's user id; runs as system.</param>
    public static IObservable<T> AsGlobalAdmin<T>(
        IMessageHub hub, Func<GitSyncRefusal> refusal, Func<string, IObservable<T>> write) =>
        Observable.Defer(() =>
        {
            var access = hub.ServiceProvider.GetService<AccessService>();
            var user = (access?.Context ?? access?.CircuitContext)?.ObjectId;
            if (string.IsNullOrEmpty(user) || access is null)
                return Observable.Throw<T>(refusal().ToException());
            return hub.IsGlobalAdmin(user)
                .TakeDecisionOutsideGate()
                .Take(1)
                .SelectMany(isAdmin => isAdmin
                    ? access.RunAsSystem(() => write(user))
                    : Observable.Throw<T>(refusal().ToException()));
        });

    /// <summary>Whether a create failed only because a concurrent create won. Pure.</summary>
    public static bool IsAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }
}
