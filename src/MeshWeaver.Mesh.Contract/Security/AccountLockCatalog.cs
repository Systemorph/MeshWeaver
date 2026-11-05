using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The mesh-scoped, SYNCHRONOUS view of the account-lock register
/// (<see cref="AccountLocks.ObserveAll(IMessageHub)"/>) — for the two seams that must decide on the
/// calling thread: the query boundary (<c>MeshService</c>, whose SQL-backed providers filter rows in
/// the database and never consult the permission fold) and the portal's request middleware. Both
/// read the SAME shared subscription the fold reads, so the four enforcement points never disagree
/// about who is locked.
///
/// <para><b>Three answers, never two.</b> <see cref="IsLocked"/> is <c>true</c>/<c>false</c> once the
/// register has answered and <c>null</c> before that (or after its read faulted): "not known yet" is
/// not "not locked". A seam that can wait (<see cref="WhenDetermined"/>) waits; the permission fold —
/// the authority every node read and write passes — never answers before the register has.</para>
///
/// <para>Registered as a singleton per mesh (its lifetime IS the mesh's; no static state). The read
/// opens on first use; a faulted read is released and re-opened by the next use, never by a timer.</para>
/// </summary>
public sealed class AccountLockCatalog : IDisposable
{
    private readonly IMessageHub hub;
    private readonly ILogger<AccountLockCatalog>? logger;
    private readonly BehaviorSubject<ImmutableDictionary<string, AccountLock>?> state = new(null);
    private readonly object gate = new();
    private IDisposable? subscription;
    private bool disposed;

    /// <summary>Creates the catalog over the mesh hub's node-stream cache.</summary>
    /// <param name="hub">The mesh hub.</param>
    /// <param name="logger">Operator log for a faulted read.</param>
    public AccountLockCatalog(IMessageHub hub, ILogger<AccountLockCatalog>? logger = null)
    {
        this.hub = hub;
        this.logger = logger;
    }

    /// <summary>
    /// Whether <paramref name="userId"/> is locked: <c>true</c>/<c>false</c> once the register has
    /// answered, <c>null</c> while it has not. An id no lock can name (<see cref="AccountLocks.IsLockable"/>)
    /// is <c>false</c> at once.
    /// </summary>
    /// <param name="userId">The subject.</param>
    /// <returns>The verdict, or null when not yet known.</returns>
    public bool? IsLocked(string? userId)
    {
        if (!AccountLocks.IsLockable(userId))
            return false;
        EnsureSubscribed();
        return state.Value is { } locks ? AccountLocks.IsLockedIn(locks, userId!) : null;
    }

    /// <summary>
    /// The verdict for <paramref name="userId"/> as soon as the register has answered — at once when
    /// it already has. Emits once and completes.
    /// </summary>
    /// <param name="userId">The subject.</param>
    /// <returns>True when locked.</returns>
    public IObservable<bool> WhenDetermined(string? userId)
    {
        if (!AccountLocks.IsLockable(userId))
            return Observable.Return(false);
        EnsureSubscribed();
        return state.Where(s => s is not null).Take(1).Select(s => AccountLocks.IsLockedIn(s!, userId!));
    }

    /// <summary>The live register — every record, lifted ones included — once it has answered.</summary>
    public IObservable<ImmutableDictionary<string, AccountLock>> Register
    {
        get
        {
            EnsureSubscribed();
            return state.Where(s => s is not null).Select(s => s!);
        }
    }

    private void EnsureSubscribed()
    {
        lock (gate)
        {
            if (subscription is not null || disposed)
                return;
            // Assigned before Subscribe returns so a synchronous fault's release below finds it.
            var holder = new SingleAssignmentDisposable();
            subscription = holder;
            holder.Disposable = AccountLocks.ObserveAll(hub).Subscribe(
                // After Dispose the subject is COMPLETED, never disposed, so a late emission from the
                // mesh's own teardown is ignored rather than thrown on a pool thread.
                locks => state.OnNext(locks),
                ex =>
                {
                    lock (gate)
                    {
                        // The mesh going away tears the shared read down with it — on another thread,
                        // possibly after this catalog was disposed. That is teardown, not a fault.
                        if (disposed)
                            return;
                        if (ReferenceEquals(subscription, holder))
                            subscription = null;
                    }
                    state.OnNext(null);
                    if (ex is ObjectDisposedException)
                        logger?.LogDebug(ex, "The account-lock register read ended with its hub");
                    else
                        logger?.LogWarning(ex,
                            "The account-lock register could not be read — admission seams wait for it, "
                            + "and the next use re-opens the read");
                });
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            subscription?.Dispose();
            subscription = null;
        }
        // Completed, not disposed: Value stays readable and a late OnNext is a no-op.
        state.OnCompleted();
    }
}
