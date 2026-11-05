using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The mesh-scoped view of the account-lock register (<see cref="AccountLocks.ObserveAll(IMessageHub)"/>)
/// for the seams that decide outside the permission fold: the query boundary (<c>MeshService</c>,
/// whose SQL-backed providers filter rows in the database and never consult the fold) and the
/// portal's request middleware. Everything here reads the SAME shared subscription the fold reads,
/// so the enforcement points never disagree about who is locked.
///
/// <para><b>Two shapes.</b> <see cref="IsLocked"/> is the SYNCHRONOUS read: <c>true</c>/<c>false</c>
/// once the register has answered, <c>null</c> before that or after its read faulted — "not known
/// yet" is never "not locked". It reads an immutable snapshot published with
/// <see cref="Volatile"/>, never a notification subject, so a caller holding a gate of its own can
/// never meet a subject's gate held by a replay (the lock-order shape
/// <c>CatalogLoadedSignalLockOrderTest</c> documents). <see cref="ObserveLocked"/> and
/// <see cref="WhenDetermined"/> are the REACTIVE reads, built straight on the shared register query:
/// they wait for its answer and receive its FAULT, so a waiter is never left without either.</para>
///
/// <para>Registered as a singleton per mesh (its lifetime IS the mesh's; no static state). The
/// snapshot read opens on first use; a faulted read is released and re-opened by the next use, never
/// by a timer. Each read is a GENERATION: only the generation that is current may publish or clear the
/// snapshot, so the terminal of an old read can never erase what a newer one published.</para>
/// </summary>
public sealed class AccountLockCatalog : IDisposable
{
    private readonly IMessageHub hub;
    private readonly ILogger<AccountLockCatalog>? logger;
    private readonly object gate = new();
    private ImmutableHashSet<string>? snapshot;
    private SingleAssignmentDisposable? generation;
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
    /// Whether a lock APPLIES to <paramref name="userId"/> (in force, and they are not a platform
    /// administrator — <see cref="AccountLocks.ObserveLocked(IMessageHub,string?)"/>):
    /// <c>true</c>/<c>false</c> once the register has answered, <c>null</c> while it has not. An id no lock can name (<see cref="AccountLocks.IsLockable"/>)
    /// is <c>false</c> at once.
    /// </summary>
    /// <param name="userId">The subject.</param>
    /// <returns>The verdict, or null when not yet known.</returns>
    public bool? IsLocked(string? userId)
    {
        if (!AccountLocks.IsLockable(userId))
            return false;
        EnsureSubscribed();
        return Volatile.Read(ref snapshot) is { } locked ? locked.Contains(userId!) : null;
    }

    /// <summary>
    /// The live verdict for <paramref name="userId"/>: emits once the register has answered and on every
    /// change of the verdict; faults when the register's read faults. Never seeded.
    /// </summary>
    /// <param name="userId">The subject.</param>
    /// <returns>True while locked.</returns>
    public IObservable<bool> ObserveLocked(string? userId) => AccountLocks.ObserveLocked(hub, userId);

    /// <summary>
    /// The verdict for <paramref name="userId"/> as soon as the register has answered — at once when
    /// the snapshot already has. Emits once and completes, or FAULTS with the register's read fault.
    /// </summary>
    /// <param name="userId">The subject.</param>
    /// <returns>True when locked.</returns>
    public IObservable<bool> WhenDetermined(string? userId)
        => IsLocked(userId) is { } known
            ? Observable.Return(known)
            : ObserveLocked(userId).TakeDecisionOutsideGate();

    /// <summary>The live register — every record, lifted ones included — once it has answered.</summary>
    public IObservable<ImmutableDictionary<string, AccountLock>> Register => AccountLocks.ObserveAll(hub);

    private void EnsureSubscribed()
    {
        lock (gate)
        {
            if (generation is not null || disposed)
                return;
            // The generation is current BEFORE Subscribe, so a synchronous first answer (or fault)
            // finds itself current.
            var current = new SingleAssignmentDisposable();
            generation = current;
            current.Disposable = AccountLocks.ObserveEffectivelyLocked(hub).Subscribe(
                locks =>
                {
                    lock (gate)
                    {
                        if (ReferenceEquals(generation, current))
                            Volatile.Write(ref snapshot, locks);
                    }
                },
                ex =>
                {
                    lock (gate)
                    {
                        // Only the current generation may clear: an old read's terminal must never
                        // erase what a newer read has already published.
                        if (disposed || !ReferenceEquals(generation, current))
                            return;
                        generation = null;
                        Volatile.Write(ref snapshot, null);
                    }
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
            generation?.Dispose();
            generation = null;
        }
    }
}
