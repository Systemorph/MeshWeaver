using System.Reactive.Concurrency;
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
/// the type's rule. That standing Read is decided as the caller, by the same two questions the
/// gate asked, through the standard-read path — and FOLLOWED LIVE
/// (<see cref="DeclaredPointRead.ObserveStandingRead"/>, MeshWeaver#7298): a standing grant removed
/// after the subscription was admitted ends it, unless the declaration admits the caller in its
/// place. A re-check in flight holds rather than ends.</para>
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
        // 🚨 A stream the NODE-STREAM CACHE opened is never followed here, whichever identity it
        // carries. The cache is the shared reader every per-user read goes through, and it applies
        // the per-user admission — the declaration included, revision by revision — at its OWN read
        // gate (MeshNodeStreamCache.AdmittedThroughDeclaration). A cache stream opened for a user's
        // write carries that user's context, but it is not that user's subscription: following it
        // decided the shared stream as that one user, ran their standing Read on the owner hub, and
        // delivered the owner's revisions to the cache through the follow's verdict chain
        // (measured in MeshWeaver.Plugins' OperationRequestOnControlTest: the request hub's control
        // plane then composed its next read as the approver instead of as System, and the DSL
        // prelude it reads was invisible to him).
        //
        // 🚨 Only the INTERNAL cache (review of #7016, thread 4338481261). A participant ingress
        // re-stamps the identity but keeps the participant's own Sender, and SubscribeRequest is
        // participant-postable — so a cache-shaped sender on an ingress-stamped delivery is a claim,
        // never the cache, and is followed like any other subscriber.
        if (string.Equals(delivery.Sender?.Type, MeshNodeCacheIdentity.HubAddressType, StringComparison.Ordinal)
            && !delivery.IsFromParticipant())
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

        // The standing Read — the type's rule, else the fold, AS the caller — FOLLOWED LIVE through the
        // platform's standard-read path (DeclaredPointRead.ObserveStandingRead), shared by every
        // revision of this subscription.
        //
        // 🚨 LIVE, not decided once (MeshWeaver#7298, the residual of #7016). It was the first outcome
        // only, so a standing grant removed after the SubscribeRequest was admitted never ended the
        // subscription: the owner kept forwarding every revision to a caller who could no longer read
        // the node. Now a grant that turns into a DEFINITIVE denial hands the decision to the
        // declaration (which, for a node no declaration opens to this caller, refuses — and the
        // subscription ends); a re-check IN FLIGHT (null) holds — it admits no new revision and ends
        // nothing, so an unrelated access commit that merely re-decides the verdict cannot end a
        // subscription whose grant still stands.
        //
        // 🚨 TRI-STATE, as every point-read seam decides it (review of #7016, thread 4336475542): a
        // GRANT admits; only a DEFINITIVE denial may consult the declaration; an UNDETERMINED outcome
        // — a fault, a timeout, an empty answer — refuses outright. Folding undetermined into "no
        // standing Read" handed the decision to the declaration, which could then admit a caller
        // whose check had failed: the one re-decision no seam may make.
        var lifetime = new System.Reactive.Disposables.CompositeDisposable();
        var standing = DeclaredPointRead.ObserveStandingRead(owner, hubPath, caller)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "The standing Read of {Path} for {User} could not be decided — undetermined, refused", hubPath, caller.ObjectId);
                return Observable.Return<PermissionCheckOutcome?>(
                    PermissionCheckOutcome.Undetermined($"the standing Read on '{hubPath}' failed ({ex.GetType().Name})"));
            })
            .DefaultIfEmpty(PermissionCheckOutcome.Undetermined($"the standing Read on '{hubPath}' answered nothing"))
            .Replay(1)
            // 🚨 Owned by THIS subscription, never by the owner hub (review of #7016, thread
            // 4338167103): the hub overload registers a fresh composite on the hub for every follow,
            // and a hub registration is never removed before the hub shuts down — so every
            // subscribe/unsubscribe cycle on a long-lived node hub retained one. The connection is
            // released when the followed sequence ends (ClientReadFollow.Lifetime); the settled
            // verdict still replays to every later revision of the same subscription.
            .AutoConnectOwnedBy(lifetime, owner.ServiceProvider.GetRequiredService<ReleaseLane>(), nameof(DeclaredReadSubscriptionFollow));

        return new ClientReadFollow(
            revision => VerdictOf(standing, () => DeclaredRead(owner, hubPath, caller, shape, revision, logger), budget, Scheduler.Default),
            budget,
            lifetime);
    }

    /// <summary>
    /// One revision's live verdict from the standing Read: a grant admits, an undetermined outcome
    /// refuses, a definitive denial hands the decision to <paramref name="declaredRead"/>.
    ///
    /// <para>🚨 A re-check IN FLIGHT (<c>null</c>) maps to a HOLD — no answer until the deadline below —
    /// INSIDE the switch — never filtered out ahead of it (review of #7329, thread 4391236736).
    /// Filtered, the pending marker never reached <c>Switch</c>, so a declaration check started under
    /// the withdrawn denial stayed subscribed and could still answer <c>true</c> — forwarding a
    /// revision on a verdict that was already being re-decided, even when the re-check then settled
    /// undetermined. As a hold it cancels that check: a pending revision waits for
    /// the settled answer (bounded by the first-verdict budget), and an admitted one stays admitted.
    /// A pending period itself is bounded upstream (<see cref="DeclaredPointRead.ObserveStandingRead"/>).</para>
    ///
    /// <para>🚨 EVERY declaration check the switch selects has its FIRST answer bounded by
    /// <paramref name="budget"/> (review of #7329, thread 4391891410). <see cref="RevisionAdmission"/>
    /// bounds only a revision's first verdict; once a revision was admitted under a grant, a later
    /// Granted → pending → Denied hand-over started a NEW declaration check nobody bounded, and a
    /// stalled storage or owner lookup there kept the subscription open past the budget on a grant
    /// that no longer stood. Past the budget, on a fault, or on an empty answer the check is a
    /// refusal; after its first answer it is followed live, unbounded.</para>
    ///
    /// <para>🚨 And the WHOLE unresolved transition shares ONE deadline (review of #7329, thread
    /// 4392164760). From the moment the standing Read stops granting — a pending marker or a
    /// definitive denial — until a verdict settles it (a grant, an undetermined outcome, or the
    /// declaration's first answer), every pending marker and every declaration check selected in
    /// between counts against the same <paramref name="budget"/>. Bounded per selection, a stalled
    /// declaration lookup under access commits cycling Denied → pending → Denied faster than the
    /// budget kept an admitted subscription open forever: each pending marker cancelled the
    /// declaration's deadline, and each settled denial ended the pending period's. Past the shared
    /// deadline the verdict is a refusal.</para>
    /// </summary>
    /// <param name="standing">The followed standing Read, <c>null</c> while a re-check is in flight.</param>
    /// <param name="declaredRead">The declaration's live verdict for the revision, subscribed only under a definitive denial.</param>
    /// <param name="budget">How long an unresolved transition — pending markers and declaration checks together — may last before it is a refusal.</param>
    /// <param name="scheduler">Where that deadline runs.</param>
    internal static IObservable<bool> VerdictOf(
        IObservable<PermissionCheckOutcome?> standing, Func<IObservable<bool>> declaredRead, TimeSpan budget, IScheduler scheduler) =>
        Observable.Defer(() =>
        {
            // Per subscription: the deadline of the unresolved transition in progress, if any.
            var period = new UnresolvedPeriod();
            return standing
                .Select(outcome =>
                {
                    if (outcome is null)
                    {
                        // Holds the verdict already given, until the shared deadline.
                        var (_, pendingDue) = period.Continue(scheduler.Now + budget);
                        return Observable.Timer(pendingDue, scheduler).Select(_ => false);
                    }
                    if (outcome.IsGranted || outcome.IsUndetermined)
                    {
                        period.Settle();
                        return Observable.Return(outcome.IsGranted);
                    }
                    var (generation, due) = period.Continue(scheduler.Now + budget);
                    return Observable.Defer(declaredRead)
                        .Timeout(Observable.Timer(due, scheduler), _ => Observable.Never<long>())
                        .Catch((Exception _) => Observable.Return(false))
                        .DefaultIfEmpty(false)
                        // The declaration's first answer settles the transition; a later selection
                        // (a newer generation) is not settled by this, now cancelled, check.
                        .Do(_ => period.SettleIf(generation));
                })
                .Switch();
        });

    /// <summary>
    /// The deadline of one unresolved transition of <see cref="VerdictOf"/>, per subscription. The
    /// selector advances it serially; a declaration check settles it from wherever it answers, and
    /// only while no later selection has replaced that check — so the state is one immutable value
    /// swapped atomically.
    /// </summary>
    private sealed class UnresolvedPeriod
    {
        private sealed record State(long Generation, DateTimeOffset? Deadline);

        private State state = new(0, null);

        /// <summary>A new selection inside the transition: keeps its deadline, or starts one at <paramref name="startDeadline"/>.</summary>
        /// <returns>The selection's generation and the transition's deadline.</returns>
        public (long Generation, DateTimeOffset Deadline) Continue(DateTimeOffset startDeadline)
        {
            while (true)
            {
                var current = Volatile.Read(ref state);
                var next = new State(current.Generation + 1, current.Deadline ?? startDeadline);
                if (ReferenceEquals(Interlocked.CompareExchange(ref state, next, current), current))
                    return (next.Generation, next.Deadline!.Value);
            }
        }

        /// <summary>A settled verdict ends the transition.</summary>
        public void Settle()
        {
            while (true)
            {
                var current = Volatile.Read(ref state);
                if (ReferenceEquals(Interlocked.CompareExchange(ref state, new State(current.Generation + 1, null), current), current))
                    return;
            }
        }

        /// <summary>Ends the transition only if the selection of <paramref name="generation"/> is still the current one.</summary>
        public void SettleIf(long generation)
        {
            var current = Volatile.Read(ref state);
            if (current.Generation == generation && current.Deadline is not null)
                Interlocked.CompareExchange(ref state, current with { Deadline = null }, current);
        }
    }

    /// <summary>
    /// The declaration's live verdict for one served revision: decided on the node revision itself
    /// when the subscription serves the node, and always from the owner's current state.
    ///
    /// <para>🚨 The branch is the REQUEST SHAPE, never the revision's CLR type (review of #7016,
    /// thread 4336475780). A node-read revision is converted with <see cref="ObjectAsExtensions.As{T}"/>;
    /// one that is absent or cannot be read as a node is refused, never decided on the owner's
    /// current state alone — that would let a revision the declaration refuses (a revoked one)
    /// through whenever it arrived untyped.</para>
    /// </summary>
    private static IObservable<bool> DeclaredRead(
        IMessageHub owner, string hubPath, AccessContext caller, PointReadRequest shape, object? revision, ILogger? logger)
    {
        var ownerCurrent = DeclaredPointRead.Admits(owner, hubPath, caller, shape.Area);
        if (shape.Area is not null)
            return ownerCurrent;
        return revision.As<MeshNode>(owner.JsonSerializerOptions, logger, hubPath) is { } node
            ? DeclaredPointRead.AdmitsRevision(owner, node, caller)
                .CombineLatest(ownerCurrent, (thisRevision, current) => thisRevision && current)
            : Observable.Return(false);
    }
}
