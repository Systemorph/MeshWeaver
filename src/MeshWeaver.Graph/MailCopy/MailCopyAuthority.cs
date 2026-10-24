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
///   <item><description>The IDENTITY the call runs under: a person may act on their own copy, and on
///   anybody else's only with the permission on <c>{owner}/_MailCopy</c> that the act needs. The sync
///   that runs in the background has no person (no context, or System) and passes; what started it
///   was authorized where it was asked for.</description></item>
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
        => Observable.Defer(() =>
        {
            // No mesh: the work itself reports that, and it can do nothing without one.
            if (hub is null)
                return work();
            if (string.IsNullOrWhiteSpace(owner) || owner.Contains('/'))
                return Observable.Throw<T>(new ArgumentException("The owner of a mail copy is one user id.", nameof(owner)));
            var access = hub.ServiceProvider.GetService<AccessService>();
            access?.ImpersonationGuard.Check(caller, surface, new AccessContext
            {
                ObjectId = AccessService.SystemObjectId,
                Name = AccessService.SystemObjectId,
                OnBehalfOf = owner,
            });
            var who = access?.Context;
            if (who is null || string.IsNullOrWhiteSpace(who.ObjectId)
                || InMeshImpersonationGuard.IsPlatformPrincipal(who)
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
