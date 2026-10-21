using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Security;

/// <summary>
/// Ends a DIRECT owner-side subscription that a node type's declared point read admitted, the moment
/// that delegated read ends — revoked, expired, its audience narrowed, its declaration withdrawn
/// (MeshWeaver#4306, follow-up 2). The <see cref="ClientSubscriptionReadAdmission"/> every per-node
/// hub registers beside its access gate (<see cref="AccessControlPipeline.AddAccessControlPipeline"/>).
///
/// <para>🚨 <b>The window it closes.</b> The <c>[RequiresPermission]</c> delivery gate admits a
/// <see cref="SubscribeRequest"/> through the declaration on its FIRST verdict, and the owner's
/// synchronization stream then forwarded every later revision for the subscription's whole life. A
/// direct subscriber — one that subscribed to the node's own hub instead of going through the
/// node-stream cache, whose read gate already followed the admission — therefore kept receiving the
/// node after its link was revoked or had expired, including the revoking revision itself.</para>
///
/// <para><b>What is followed, and what is not.</b> Only a subscription the gate can have admitted
/// through the declaration: a point-read shape (<see cref="DeclaredPointRead.RequestShape"/>), a
/// trusted caller context, and a caller that holds no standing Read through the permission fold or
/// the type's rule. That standing Read is decided ONCE, as the caller, by the same two questions the
/// gate asked, through the standard-read path (<see cref="DeclaredPointRead.ObserveStandardRead"/>,
/// its first outcome); while it held, the declaration was
/// never consulted, so there is nothing of the declaration's to follow. Following a standing GRANT
/// live is the platform's live permission path, not this declaration-specific signal.</para>
///
/// <para><b>Per revision, fail closed.</b> A followed subscription forwards a revision of the node
/// only once the declaration admits THAT revision (<see cref="DeclaredPointRead.AdmitsRevision"/>)
/// and the owner's current state still admits the caller (<see cref="DeclaredPointRead.Admits"/>);
/// a layout-area subscription follows the latter. The first refusal ends the subscription with a
/// <c>StreamErrorEvent</c> and disposes the server-side stream. A verdict that stalls past the
/// permission-establishment budget, faults or answers nothing is a refusal.</para>
/// </summary>
public static class DeclaredReadSubscriptionFollow
{
    /// <summary>The <see cref="ClientSubscriptionReadAdmission"/> for a per-node hub; see the type remarks.</summary>
    /// <param name="owner">The node hub serving the subscription.</param>
    /// <param name="delivery">The admitted subscribe delivery.</param>
    /// <returns>Null when the subscription cannot have been admitted through a declaration; otherwise how each revision is decided.</returns>
    public static ClientReadFollow? Follow(IMessageHub owner, IMessageDelivery<SubscribeRequest> delivery)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(delivery);
        // No evaluator ⇒ the mesh is declared ungated; the gate consulted nothing.
        if (owner.Configuration.Get<EffectivePermissionsDelegate>() is null)
            return null;
        var hubPath = string.Join("/", owner.Address.Segments);
        // Only a point read of this node — the node itself or a declared area — is ever admitted
        // through a declaration. Any other shape was admitted by a standing grant or not at all.
        if (DeclaredPointRead.RequestShape(delivery.Message, hubPath) is not { } shape)
            return null;
        var access = owner.ServiceProvider.GetRequiredService<AccessService>();
        // The gate consults a declaration only for a TRUSTED context; without one it admitted nothing
        // through a declaration.
        if (AccessControlPipeline.ResolveCaller(delivery, access) is not { } caller)
            return null;
        // Platform identities (System, a hub, the node-stream cache's hydrator) are never admitted by
        // a declaration (DeclaredPointRead refuses System; the others hold standing reads).
        if (caller.IsHub
            || AccessService.IsPlatformPrincipal(caller.ObjectId)
            || string.Equals(caller.ObjectId, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase)
            || string.Equals(caller.ObjectId, MeshNodeCacheIdentity.Address, StringComparison.Ordinal))
            return null;
        // A hub-level rule (WithPublicRead) short-circuits the gate before any fold: static, so it
        // stands for the subscription's life.
        if (owner.Configuration.Get<HubPermissionRuleSet>() is { } hubRules
            && hubRules.HasPermission(Permission.Read, delivery, caller.ObjectId))
            return null;

        var logger = owner.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(DeclaredReadSubscriptionFollow));
        var budget = (owner.ServiceProvider.GetService<MeshOperationOptions>() ?? new MeshOperationOptions())
            .PermissionEstablishmentBudget;

        // The standing Read — the type's rule, else the fold, AS the caller — decided ONCE through the
        // platform's standard-read path (DeclaredPointRead.ObserveStandardRead, its first outcome),
        // shared by every revision of this subscription. Undetermined is not a standing Read: the
        // declaration is then the only thing that can still admit the caller.
        var standing = DeclaredPointRead.ObserveStandardRead(owner, hubPath, caller)
            .Take(1)
            .Select(outcome => outcome.IsGranted)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "The standing Read of {Path} for {User} could not be decided — following the declaration", hubPath, caller.ObjectId);
                return Observable.Return(false);
            })
            .DefaultIfEmpty(false)
            .Replay(1)
            .AutoConnectOwnedBy(owner, nameof(DeclaredReadSubscriptionFollow));

        return new ClientReadFollow(
            revision => standing
                .Select(granted => granted
                    ? Observable.Return(true)
                    : DeclaredRead(owner, hubPath, caller, shape, revision))
                .Switch(),
            budget);
    }

    /// <summary>The declaration's live verdict for one served revision: decided on the node revision itself when the subscription serves the node, and always from the owner's current state.</summary>
    private static IObservable<bool> DeclaredRead(
        IMessageHub owner, string hubPath, AccessContext caller, PointReadRequest shape, object? revision)
    {
        var ownerCurrent = DeclaredPointRead.Admits(owner, hubPath, caller, shape.Area);
        return shape.Area is null && revision is MeshNode node
            ? DeclaredPointRead.AdmitsRevision(owner, node, caller)
                .CombineLatest(ownerCurrent, (thisRevision, current) => thisRevision && current)
            : ownerCurrent;
    }
}
