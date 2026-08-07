using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;


/// <summary>
/// Canonical client-side surface for permission checks. Application code
/// asks the hub for an answer; the extension dispatches through an
/// <see cref="EffectivePermissionsDelegate"/> registered in DI at startup
/// time. When <c>AddRowLevelSecurity()</c> ran, that delegate is
/// <see cref="PermissionEvaluator.GetEffectivePermissions(MeshWeaver.Messaging.IMessageHub, string, string)"/>; otherwise it's
/// the default <c>Observable.Return(Permission.All)</c>. <strong>No runtime
/// branching at the call site</strong> — same lambda for both worlds.
///
/// <para>Each extension returns an <see cref="IObservable{T}"/> end-to-end —
/// no Task, no await, no <c>FirstAsync()</c> bridge in src/. Tests bridge to
/// Task at their edge with <c>.FirstAsync().ToTask()</c>.</para>
/// </summary>
public static class HubPermissionExtensions
{
    /// <summary>
    /// Effective permissions for the current user (resolved from
    /// <see cref="AccessService.Context"/> / <see cref="AccessService.CircuitContext"/>)
    /// on <paramref name="nodePath"/>.
    /// </summary>
    public static IObservable<Permission> GetEffectivePermissions(
        this IMessageHub hub,
        string nodePath)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var userId = ResolveUserId(hub);
        return ResolveEvaluator(hub)(hub, nodePath, userId);
    }

    /// <summary>
    /// Effective permissions for the explicit <paramref name="userId"/> on
    /// <paramref name="nodePath"/>.
    /// </summary>
    public static IObservable<Permission> GetEffectivePermissions(
        this IMessageHub hub,
        string nodePath,
        string userId)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return ResolveEvaluator(hub)(hub, nodePath, userId);
    }

    /// <summary>
    /// True when the current user has <paramref name="permission"/> on
    /// <paramref name="nodePath"/>.
    /// </summary>
    public static IObservable<bool> CheckPermission(
        this IMessageHub hub,
        string nodePath,
        Permission permission)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (permission == Permission.None)
            return Observable.Return(true);
        return hub.GetEffectivePermissions(nodePath).Select(p => p.HasFlag(permission));
    }

    /// <summary>
    /// True when <paramref name="userId"/> has <paramref name="permission"/> on
    /// <paramref name="nodePath"/>.
    /// </summary>
    public static IObservable<bool> CheckPermission(
        this IMessageHub hub,
        string nodePath,
        string userId,
        Permission permission)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (permission == Permission.None)
            return Observable.Return(true);
        return hub.GetEffectivePermissions(nodePath, userId).Select(p => p.HasFlag(permission));
    }

    /// <summary>
    /// True when the current user (resolved from <see cref="AccessService.Context"/> /
    /// <see cref="AccessService.CircuitContext"/>) is a <b>global admin</b> — i.e. an
    /// admin on the <b>Admin partition</b> (<see cref="Permission.All"/> at scope
    /// <c>Admin</c>, granted by an <c>AccessAssignment</c> in <c>Admin/_Access</c>).
    /// <para>This is the ONE canonical platform-admin predicate — every "is this user a
    /// global/platform admin?" check goes through here, never an ad-hoc role-name or
    /// root-scope check.</para>
    ///
    /// <para>🚨 It answers "may this user run platform actions?", NOT "may this user read
    /// this data". A global admin is NOT a superuser: the grant is scoped to the Admin
    /// partition and confers <b>no Read on any other node</b> — spaces and user partitions
    /// evaluate to <see cref="Permission.None"/> for them, so gated content stays gated for
    /// admins too. Use this predicate to gate admin UI and platform operations; never as a
    /// stand-in for a read check on content. Doc/Architecture/AccessControl.md.</para>
    /// </summary>
    public static IObservable<bool> IsGlobalAdmin(this IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return hub.IsGlobalAdmin(ResolveUserId(hub));
    }

    /// <summary>
    /// True when <paramref name="userId"/> is a global admin — an admin on the Admin
    /// partition (<see cref="Permission.All"/> at scope <c>Admin</c>). See
    /// <see cref="IsGlobalAdmin(IMessageHub)"/>.
    /// </summary>
    public static IObservable<bool> IsGlobalAdmin(this IMessageHub hub, string userId)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return hub.GetEffectivePermissions(PermissionEvaluator.AdminScope, userId)
            .Select(p => p.HasFlag(Permission.All));
    }

    /// <summary>
    /// Takes ONE authorization answer and <b>leaves the evaluator's Rx gate</b> before the
    /// caller's continuation runs. Use this — never a bare <c>.Take(1)</c> — whenever what
    /// follows the decision does REAL WORK (storage I/O, a hub write, a change-feed publish,
    /// invoking a downstream handler) rather than just projecting to a value or a UI control.
    ///
    /// <para><b>Why (issue #899).</b>
    /// <see cref="PermissionEvaluator.GetEffectivePermissions(IMessageHub,string,string)"/> is
    /// a <c>seed.Concat(enriched)</c> chain over an <c>Observable.CombineLatest</c> fold whose
    /// sources are cached <c>Replay(1)</c> queries. On a warm cache it therefore emits
    /// <b>synchronously during <c>Subscribe</c>, while holding that gate</b> (one gate per
    /// ancestor scope, nested). A bare <c>.Take(1).SelectMany(&lt;whole handler&gt;)</c> runs
    /// the ENTIRE handler body inside the lock. If the body then publishes a change, the
    /// (synchronous, by contract) fan-out walks shared gates — <c>PersistenceService.Changes</c>'s
    /// <c>Merge</c>, the process-wide <c>Replay(1)</c> in <c>IMeshNodeStreamCache</c> — and on
    /// into ANOTHER hub's fold. Two hubs doing that concurrently take {own fold gate, shared
    /// gate} in opposite orders and deadlock, with neither a success nor a failure response
    /// ever posted (the recursive-delete wedge in #899).</para>
    ///
    /// <para><b>This is the half that is safe to remove.</b> Synchronous change-feed delivery
    /// is a load-bearing contract — <c>PathResolutionService</c>'s resolution cache
    /// ("runs synchronously on the publisher's thread"), <c>MeshNodeStreamCache</c>'s
    /// failure-state reset ("the VERY NEXT read heals") and <c>Workspace</c>'s remote-stream
    /// eviction all depend on the invalidation landing before the writing call returns. Making
    /// the fan-out asynchronous breaks read-your-own-writes; releasing the gate does not. A
    /// cycle needs BOTH ingredients, so killing this one is sufficient.</para>
    ///
    /// <para>The decision is still TAKEN inside the fold — the evaluation and the answer are
    /// unchanged; only the continuation is moved off the gate, onto
    /// <see cref="Scheduler.Default"/>. This is what any pipeline that already hops through
    /// <c>IIoPool</c> gets for free, which is exactly why the Postgres-backed paths never
    /// reproduced the wedge and the fully-synchronous in-memory path did.</para>
    ///
    /// <para>🚨 Not needed for pure projections (a fold feeding a layout area,
    /// <c>.Select(p =&gt; control)</c>): those finish inside the gate and touch nothing shared.
    /// A hop there would only cost a scheduler round-trip. And like any <c>Take(1)</c>, this
    /// CLOSES the stream — never put it on a hot observable that a live data-bound view reads.</para>
    /// </summary>
    /// <typeparam name="T">The decision type — <see cref="Permission"/>, <c>bool</c>, or a
    /// validation result projected from one.</typeparam>
    /// <param name="decision">The authorization decision stream (hot and never-completing —
    /// it rides the live <c>AccessAssignment</c> synced query, hence the <c>Take(1)</c>).</param>
    public static IObservable<T> TakeDecisionOutsideGate<T>(this IObservable<T> decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.Take(1).SelectMany(HopOffGate);
    }

    /// <summary>
    /// Re-emits <paramref name="value"/> on <see cref="Scheduler.Default"/> so the subscriber's
    /// continuation runs on a pool thread instead of on the emitting (gate-holding) thread.
    ///
    /// <para>🚨 <c>Observable.Return(value, scheduler)</c> inside <c>SelectMany</c>, NOT
    /// <c>ObserveOn(scheduler)</c>. Both leave the gate, but Rx's <c>ObserveOn</c> runs a
    /// long-running scheduled loop per subscription: measured on Rx 6.1.0, 64 live
    /// <c>ObserveOn(Scheduler.Default)</c> subscriptions took the process from 13 to <b>77</b>
    /// threads (released on dispose), while the <c>Observable.Return</c> form cost <b>3</b>. A
    /// permission check is subscribed per call, so <c>ObserveOn</c> would be a thread per
    /// in-flight check.</para>
    ///
    /// <para><b>Identity survives the hop.</b> <see cref="Scheduler.Default"/> queues through
    /// the thread pool with <c>ExecutionContext</c> capture, so the <c>AsyncLocal</c>
    /// <c>AccessService.Context</c> that is live on the emitting thread is still live in the
    /// continuation (verified against Rx 6.1.0). No <c>CarryAccessContext</c> wrap is needed
    /// on top of this — see Doc/Architecture/AccessContextPropagation.md.</para>
    /// </summary>
    private static IObservable<T> HopOffGate<T>(T value)
        => Observable.Return(value, Scheduler.Default);

    /// <summary>
    /// Resolve a role definition (built-in or custom) by id.
    /// </summary>
    public static IObservable<Role?> GetRole(this IMessageHub hub, string roleId)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return PermissionEvaluator.GetRole(hub, roleId);
    }

    /// <summary>All available roles — built-in + custom Role MeshNodes.</summary>
    public static IObservable<Role> GetRoles(this IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return PermissionEvaluator.GetRoles(hub);
    }

    /// <summary>
    /// The current <see cref="PartitionAccessPolicy"/> at <paramref name="targetNamespace"/>,
    /// or <c>null</c> if none.
    /// </summary>
    public static IObservable<PartitionAccessPolicy?> GetPolicy(
        this IMessageHub hub, string targetNamespace)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return PermissionEvaluator.GetPolicy(hub, targetNamespace);
    }

    /// <summary>
    /// The mesh path to REDIRECT a viewer to when they lack access to <paramref name="targetNamespace"/>
    /// — the nearest scope's <see cref="PartitionAccessPolicy.RedirectOnDenied"/> (normalized, no leading
    /// '/'), or <c>null</c> if none is configured. The GUI reads this on a real access-denied to send the
    /// viewer to a public page ("if no access, redirect here") instead of a dead-end error.
    /// </summary>
    public static IObservable<string?> GetRedirectOnDenied(
        this IMessageHub hub, string targetNamespace)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return PermissionEvaluator.GetRedirectOnDenied(hub, targetNamespace);
    }

    private static EffectivePermissionsDelegate ResolveEvaluator(IMessageHub hub) =>
        hub.Configuration.Get<EffectivePermissionsDelegate>()
        ?? MessageHubPermissionExtensions.DefaultEvaluator;

    private static string ResolveUserId(IMessageHub hub)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var context = accessService?.Context ?? accessService?.CircuitContext;
        var userId = context?.ObjectId;
        if (string.IsNullOrEmpty(userId) || context?.IsVirtual == true)
            userId = WellKnownUsers.Anonymous;
        return userId;
    }
}
