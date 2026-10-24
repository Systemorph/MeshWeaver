using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.MailCopy;

/// <summary>
/// The caller check in front of every System write and owner-viewer read of the mail copy
/// (<see cref="MeshMailCopyStore"/>, <see cref="MeshMailAttachmentStore"/>): authorize as the CALLER,
/// then execute as System (Doc/Architecture/AuthorizeAsCallerExecuteAsSystem).
///
/// <para>Two checks, both before any work is composed:</para>
/// <list type="number">
///   <item><description>The CODE that called the store is judged by the in-mesh impersonation guard,
///   exactly as if it had called <c>RunAsSystemFor</c> itself. The guard reads the immediate calling
///   assembly, and for a call into these stores that is always this platform assembly — so without
///   this the stores would launder an in-mesh caller's System write.</description></item>
///   <item><description>The IDENTITY the call was made under, read when the store is CALLED — the
///   request context, else the circuit's — never when the cold result is later subscribed (a scope
///   that ended in between must not decide). A person may act on their own copy, and on anybody
///   else's only with the permission on <c>{owner}/_MailCopy</c> that the act needs. System and hub
///   principals pass: the background sync DECLARES itself System (<see cref="MailCopyEngine.Kick"/>).
///   No identity at all, or an anonymous one (an empty id), is refused — a lost identity is never
///   read as trusted background work.</description></item>
/// </list>
/// </summary>
internal static class MailCopyAuthority
{
    /// <summary>
    /// <paramref name="work"/>, composed only once <paramref name="caller"/> and the ambient identity
    /// may act on <paramref name="owner"/>'s copy with <paramref name="permission"/>. A refusal is an
    /// error on the returned observable; nothing of the work runs. Cold.
    /// </summary>
    public static IObservable<T> Gated<T>(IMessageHub? hub, Assembly caller, string surface, string owner,
        Permission permission, Func<IObservable<T>> work)
    {
        // 🚨 The caller is read HERE, at the call — not in the Defer below, which runs whenever the cold
        // result is subscribed, possibly after the caller's scope has ended and under somebody else's.
        var access = hub?.ServiceProvider.GetService<AccessService>();
        var who = access is null ? null : access.Context ?? access.CircuitContext;
        return Observable.Defer(() =>
        {
            // No mesh: the work itself reports that, and it can do nothing without one.
            if (hub is null)
                return work();
            if (string.IsNullOrWhiteSpace(owner) || owner.Contains('/'))
                return Observable.Throw<T>(new ArgumentException("The owner of a mail copy is one user id.", nameof(owner)));
            // A host without access control has nobody to authorize.
            if (access is null)
                return work();
            access.ImpersonationGuard.Check(caller, surface, new AccessContext
            {
                ObjectId = AccessService.SystemObjectId,
                Name = AccessService.SystemObjectId,
                OnBehalfOf = owner,
            });
            if (who is null || string.IsNullOrWhiteSpace(who.ObjectId))
                return Observable.Throw<T>(new UnauthorizedAccessException(
                    $"The mail copy of '{owner}' is reachable only by a signed-in person or by work that runs as System; this call carries {(who is null ? "no identity" : "an anonymous one")}."));
            if (InMeshImpersonationGuard.IsPlatformPrincipal(who)
                || string.Equals(who.ObjectId, owner, StringComparison.OrdinalIgnoreCase))
                return work();
            var scope = MailCopyNodeTypes.NamespaceFor(owner);
            return hub.CheckPermissionOutcome(scope, who.ObjectId, permission).Take(1)
                .SelectMany(outcome => outcome.IsGranted
                    ? work()
                    : Observable.Throw<T>(outcome.IsUndetermined
                        ? new InvalidOperationException(
                            $"Whether '{who.ObjectId}' may act on the mail copy of '{owner}' could not be checked: {outcome.UndeterminedReason}")
                        : new UnauthorizedAccessException(
                            $"'{who.ObjectId}' may not act on the mail copy of '{owner}': {permission} on '{scope}' is required.")));
        });
    }
}
