using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The mesh-scoped, STANDING view of who a lock applies to — for the seams that decide outside the
/// permission fold: the query boundary (<c>MeshService</c>, whose SQL-backed providers filter rows in
/// the database and never consult the fold) and the portal's request middleware.
///
/// <para><b>One standing read, from the platform's providers only.</b> The catalog holds ONE live read
/// of the register for the process's lifetime, issued with
/// <see cref="MeshQueryRequest.PlatformProvidersOnly"/>: a fan-out leases every module query provider
/// for the life of its subscription, so a standing read that reached the modules would pin every module
/// generation for as long as the process runs (no update could retire one) and would let module code
/// contribute or withhold platform security data. The register is platform data in the Admin partition;
/// the platform's own providers serve it.</para>
///
/// <para><b>Two shapes.</b> <see cref="IsLocked"/> is the SYNCHRONOUS read: <c>true</c>/<c>false</c>
/// once the register has answered, <c>null</c> before that or after its read faulted — "not known yet"
/// is never "not locked". It reads an immutable snapshot published with <see cref="Volatile"/>, never a
/// subject's state. <see cref="ObserveLocked"/> and <see cref="WhenDetermined"/> are the REACTIVE reads
/// of the same standing read: they wait for its answer and receive its FAULT, so a waiter is never left
/// without either.</para>
///
/// <para>Each standing read is a GENERATION: only the current one may publish or clear the snapshot, so
/// an old read's terminal never erases a newer one's answer. A faulted generation is released, and the
/// next use opens a new one — never a timer. Registered as a singleton per mesh (no static state).</para>
/// </summary>
public sealed class AccountLockCatalog : IDisposable
{
    private readonly IMessageHub hub;
    private readonly ILogger<AccountLockCatalog>? logger;
    private readonly object gate = new();
    private ImmutableHashSet<string>? snapshot;
    private Generation? current;
    private bool disposed;
    // Test seam: the register read the catalog opens. Null in production (PlatformRegisterQuery).
    private readonly Func<IObservable<QueryResultChange<MeshNode>>>? openRegister;

    /// <summary>Creates the catalog over the mesh hub.</summary>
    /// <param name="hub">The mesh hub.</param>
    /// <param name="logger">Operator log for a faulted read.</param>
    public AccountLockCatalog(IMessageHub hub, ILogger<AccountLockCatalog>? logger = null)
        : this(hub, logger, openRegister: null)
    {
    }

    /// <summary>The seam a test drives: the register read the catalog opens.</summary>
    internal AccountLockCatalog(
        IMessageHub hub, ILogger<AccountLockCatalog>? logger, Func<IObservable<QueryResultChange<MeshNode>>>? openRegister)
    {
        this.hub = hub;
        this.logger = logger;
        this.openRegister = openRegister;
    }

    /// <summary>
    /// Whether a lock APPLIES to <paramref name="userId"/> (in force, and they are not a platform
    /// administrator — <see cref="AccountLocks.ObserveLocked(IMessageHub,string?)"/>):
    /// <c>true</c>/<c>false</c> once the register has answered, <c>null</c> while it has not. An id no
    /// lock can name (<see cref="AccountLocks.IsLockable"/>) is <c>false</c> at once.
    /// </summary>
    /// <param name="userId">The subject.</param>
    /// <returns>The verdict, or null when not yet known.</returns>
    public bool? IsLocked(string? userId)
    {
        if (!AccountLocks.IsLockable(userId))
            return false;
        Current();
        return Volatile.Read(ref snapshot) is { } locked ? locked.Contains(userId!) : null;
    }

    /// <summary>
    /// The live verdict for <paramref name="userId"/> from the standing read: emits once the register
    /// has answered and on every change of the verdict; faults when the read faults. Never seeded.
    /// </summary>
    /// <param name="userId">The subject.</param>
    /// <returns>True while a lock applies.</returns>
    public IObservable<bool> ObserveLocked(string? userId)
        => !AccountLocks.IsLockable(userId)
            ? Observable.Return(false)
            : Observable.Defer(() => Current()).Select(locked => locked.Contains(userId!)).DistinctUntilChanged();

    /// <summary>
    /// The verdict for <paramref name="userId"/> as soon as the register has answered — at once when the
    /// snapshot already has. Emits once and completes, or FAULTS with the read's fault.
    /// </summary>
    /// <param name="userId">The subject.</param>
    /// <returns>True when locked.</returns>
    public IObservable<bool> WhenDetermined(string? userId)
        => IsLocked(userId) is { } known
            ? Observable.Return(known)
            : ObserveLocked(userId).TakeDecisionOutsideGate();

    /// <summary>The accounts a lock applies to, live, from the standing read.</summary>
    public IObservable<ImmutableHashSet<string>> Locked => Observable.Defer(() => Current());

    /// <summary>
    /// The register as the platform's providers serve it, live — folded from the query's changes.
    /// Leases no module provider (<see cref="MeshQueryRequest.PlatformProvidersOnly"/>).
    ///
    /// <para>🚨 <b>Only a COMPLETE answer is ever folded.</b> An <c>Initial</c>/<c>Reset</c> whose
    /// providers did not all answer (<see cref="QueryResultChange{T}.SilentProviders"/>) or that a
    /// provider read only in part (<see cref="QueryResultChange{T}.SnapshotIncomplete"/>) is a FLOOR,
    /// and folding it would publish every lock it is missing as "not locked" — fail open. Such a frame
    /// FAULTS the read instead, which clears the snapshot (the verdict is "not known", which every seam
    /// refuses on) and lets the next use open a fresh read. A read that COMPLETES is the same defect
    /// wearing a quieter face — the register stops learning — and faults likewise.</para>
    /// </summary>
    private IObservable<ImmutableDictionary<string, AccountLock>> PlatformRegister()
    {
        var options = hub.JsonSerializerOptions;
        return (openRegister?.Invoke() ?? PlatformRegisterQuery())
            .SelectMany(RejectIncomplete)
            .Concat(Observable.Throw<QueryResultChange<MeshNode>>(new InvalidOperationException(
                "the account-lock register read completed; a standing read must not end")))
            .Scan(ImmutableDictionary<string, MeshNode>.Empty.WithComparers(StringComparer.Ordinal), (byPath, change) =>
            {
                switch (change.ChangeType)
                {
                    case QueryChangeType.Initial:
                    case QueryChangeType.Reset:
                        return change.Items.Where(n => n.Path is not null)
                            .ToImmutableDictionary(n => n.Path!, n => n, StringComparer.Ordinal);
                    case QueryChangeType.Added:
                    case QueryChangeType.Updated:
                        return change.Items.Where(n => n.Path is not null)
                            .Aggregate(byPath, (acc, n) => acc.SetItem(n.Path!, n));
                    case QueryChangeType.Removed:
                        return change.Items.Where(n => n.Path is not null)
                            .Aggregate(byPath, (acc, n) => acc.Remove(n.Path!));
                    default:
                        return byPath;
                }
            })
            .Select(byPath => AccountLocks.Read(byPath.Values, options));
    }

    /// <summary>
    /// The production register read: the anchored register query, platform providers only, constructed
    /// AND subscribed as System (<c>RunAsSystem</c>). Stamping the request alone is not enough: the
    /// query boundary also refuses on the AMBIENT caller — a token whose scopes allow no Read is
    /// answered with an empty Initial — so a catalog first opened during such a caller's request would
    /// otherwise publish "nobody is locked" for every person, for good.
    /// </summary>
    private IObservable<QueryResultChange<MeshNode>> PlatformRegisterQuery()
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var request = MeshQueryRequest.FromQuery(AccountLocks.Query).AsSystem() with { PlatformProvidersOnly = true };
        return hub.ServiceProvider.GetService<AccessService>().RunAsSystem(() => mesh.Query<MeshNode>(request));
    }

    /// <summary>A frame that is a complete answer, or a fault naming why it is not one.</summary>
    internal static IObservable<QueryResultChange<MeshNode>> RejectIncomplete(QueryResultChange<MeshNode> change)
    {
        if (change.SilentProviders is { Count: > 0 } silent)
            return Observable.Throw<QueryResultChange<MeshNode>>(new InvalidOperationException(
                $"the account-lock register answer is INCOMPLETE: query provider(s) [{string.Join(", ", silent)}] "
                + "did not answer, so a lock missing from it cannot be read as \"not locked\""));
        if (change.SnapshotIncomplete)
            return Observable.Throw<QueryResultChange<MeshNode>>(new InvalidOperationException(
                "the account-lock register answer is INCOMPLETE: a provider read it only in part, so a lock "
                + "missing from it cannot be read as \"not locked\""));
        return Observable.Return(change);
    }

    /// <summary>The current generation's live set, opening one when there is none.</summary>
    private IObservable<ImmutableHashSet<string>> Current()
    {
        lock (gate)
        {
            if (disposed)
                return Observable.Empty<ImmutableHashSet<string>>();
            if (current is not null)
                return current.Answers;
            var generation = new Generation();
            current = generation;
            generation.Subscription.Disposable = AccountLocks.EffectivelyLocked(hub, PlatformRegister()).Subscribe(
                locked =>
                {
                    lock (gate)
                    {
                        if (!ReferenceEquals(current, generation))
                            return;
                        Volatile.Write(ref snapshot, locked);
                    }
                    generation.Answers.OnNext(locked);
                },
                ex =>
                {
                    lock (gate)
                    {
                        // Only the current generation may clear: an old read's terminal must never
                        // erase what a newer read has already published.
                        if (!ReferenceEquals(current, generation))
                            return;
                        current = null;
                        Volatile.Write(ref snapshot, null);
                    }
                    // 🚨 The MESH going away ends the read — the teardown drain completes every pooled
                    // query — and that is not a verdict about anyone. It ENDS the verdict streams
                    // instead of faulting them: a fault would make every live read gated on the lock
                    // tear its provider down mid-teardown, ahead of the module teardown that owns those
                    // subscriptions (ModuleLeasesAtMeshTeardownTest). Nothing new is admitted on an
                    // ended verdict; what is already served ends with the mesh, by its own teardown.
                    if (hub.IsDisposing)
                    {
                        logger?.LogDebug(ex, "The account-lock register read ended with the mesh");
                        generation.Answers.OnCompleted();
                        return;
                    }
                    if (ex is ObjectDisposedException)
                        logger?.LogDebug(ex, "The account-lock register read ended with its hub");
                    else
                        logger?.LogWarning(ex,
                            "The account-lock register could not be read — admission seams wait for it, "
                            + "and the next use re-opens the read");
                    // Every current waiter receives the fault; the next use opens a new generation.
                    generation.Answers.OnError(ex);
                });
            return generation.Answers;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Generation? closing;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            closing = current;
            current = null;
        }
        closing?.Subscription.Dispose();
        closing?.Answers.OnCompleted();
    }

    /// <summary>One standing read and the latest answer it gave, replayed to every later observer.</summary>
    private sealed class Generation
    {
        public SingleAssignmentDisposable Subscription { get; } = new();
        public ReplaySubject<ImmutableHashSet<string>> Answers { get; } = new(1);
    }
}
