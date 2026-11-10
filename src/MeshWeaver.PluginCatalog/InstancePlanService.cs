using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The PLAN on a registered instance's record — the licence every registry decision is made
/// against (#2804) — and the one way it changes: a global admin promotes it here.
///
/// <para>Registration writes the initial plan (the baseline, or the plan a registration key was
/// minted for); nothing else on the instance's side can raise it. The write is one field on the
/// <see cref="MeshWeaverInstance"/> record, and because <see cref="InstanceRegistryAuthenticator"/>
/// reads that record off its live mirror (#3119) the next request decides with the new plan: on
/// THIS replica as soon as <see cref="SetPlan"/> completes, because it completes only once that
/// mirror carries the write (#6818), and on every other replica once its change feed delivers it.</para>
///
/// <para>In <c>MeshWeaver.PluginCatalog</c> rather than the portal's instance service so the
/// admin tab (which lives here) can call it, and so the plan has ONE writer wherever the registry
/// is hosted.</para>
/// </summary>
public sealed class InstancePlanService(IMessageHub hub, ILogger<InstancePlanService> logger)
{
    /// <summary>
    /// The path of the instance node registered under <paramref name="instanceId"/>, or null.
    /// A listing query, which is the valid shape for "which node carries this id": instances live
    /// in their owners' partitions, so there is no path to point-read, and a stale negative is
    /// harmless here — the admin sees "no such instance" and tries again. Runs as System, because
    /// the claim is global and an admin must find an instance whoever registered it.
    /// </summary>
    public IObservable<string?> FindInstancePath(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return Observable.Return<string?>(null);

        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        // Keyed by id, filed under whichever user registered it — no partition to anchor to, so
        // the read declares its cost (#3202 — fan-out is opt-in); the durable fix is an id → owner
        // index in a pinned partition.
        var request = new MeshQueryRequest
        {
            Query = MeshWideQuery.Declare(
                $"nodeType:{MeshWeaverInstanceNodeType.NodeType} id:{instanceId.Trim()}"),
        };
        // 🚨 The MERGED Initial, never the first frame of the progressive fan-in: that one is seeded
        // empty per provider, so an instance held by a provider that answers asynchronously (the
        // Postgres store, in production) is not in it — the lookup then said "no registered
        // instance" for every id.
        return accessService.RunAsSystem(() => meshService.Query<MeshNode>(request))
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Take(1)
            // 🚨 No timer of its own (#5894). This lookup used to carry a 10 s Timeout, written when
            // the fan-in's Initial had no bound and an unanswered provider hung its caller for ever.
            // The fan-in now FAULTS at its own budget (QueryProviderStalledException at
            // MeshOperationOptions.QueryInitialBudget, 15 s on the default ladder), so the local
            // 10 s was a second, SHORTER bound on the same wait: the form gave up while the query
            // was still legitimately inside its budget. One bound, the query's.
            .Select(change => change.Items
                // The key-hash INDEX rows share the node type but live under the global index
                // namespace and carry a hash prefix as their id — they never match an instance id,
                // and the filter makes that explicit rather than incidental.
                .FirstOrDefault(r => !r.Path.StartsWith(
                    MeshWeaverInstanceNodeType.IndexNamespace + "/", StringComparison.Ordinal))
                ?.Path);
    }

    /// <summary>
    /// Sets the plan on the instance at <paramref name="instancePath"/> to <paramref name="plan"/>
    /// — the promotion (or demotion) a global admin makes. The caller validates the id against the
    /// registry's own ladder (<see cref="PlanTierLadder"/>); an unknown plan stored here would
    /// license nothing (fail closed), which is why the admin tab refuses it before calling.
    ///
    /// <para>🚨 A GLOBAL ADMINISTRATOR's act (<c>hub.IsGlobalAdmin()</c>, read off the caller's own
    /// identity), and the record is then read and written as System — the same shape as
    /// <c>RevokeKey</c>. The record lives in its registrant's partition
    /// (<c>{owner}/MeshWeaverInstance/{id}</c>), and a global administrator is a platform admin, not a
    /// data superuser: under the caller's own identity the read is refused for every instance the
    /// admin did not register. Anyone else is refused before anything is read.</para>
    /// </summary>
    /// <returns>The updated node. Cold — subscribe to write.</returns>
    public IObservable<MeshNode> SetPlan(string instancePath, string plan)
    {
        var canonical = PlanTierRanks.Canonical(plan);
        if (canonical.Length == 0)
            return Observable.Throw<MeshNode>(new ArgumentException("A plan id is required.", nameof(plan)));
        if (string.IsNullOrWhiteSpace(instancePath))
            return Observable.Throw<MeshNode>(new ArgumentException("An instance path is required.", nameof(instancePath)));

        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        // Read off the CALLER's identity before the System scope replaces it — the audit names them.
        var admin = accessService.Context?.ObjectId is { Length: > 0 } id ? id : "unknown";
        return hub.IsGlobalAdmin()
            .Take(1)
            .SelectMany(isAdmin => isAdmin
                ? accessService.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream(instancePath)
                    .Update(current => current with
                    {
                        Content = current.ContentAs<MeshWeaverInstance>(hub.JsonSerializerOptions) is { } instance
                            ? instance with
                            {
                                Plan = canonical,
                                PlanSetAt = DateTimeOffset.UtcNow,
                                PlanSetVia = $"global administrator {admin}",
                            }
                            : current.Content,
                    }))
                    .SelectMany(written => MirrorCarries(instancePath, written, canonical, accessService))
                : Observable.Throw<MeshNode>(new UnauthorizedAccessException(
                    $"setting the plan of '{instancePath}' is a global administrator's act on the registry")))
            .Do(node =>
            {
                var instance = node.ContentAs<MeshWeaverInstance>(hub.JsonSerializerOptions);
                if (instance is null)
                    return;
                // No verdict to forget: the authenticator reads the instance off its live mirror, and
                // MirrorCarries has just seen that mirror carry the write — so the next request on
                // THIS replica decides with the new plan (#6818). Other replicas see it when their
                // change feed delivers the commit (#3119).
                logger.LogInformation("Instance {InstanceId} set to plan {Plan}", instance.InstanceId, canonical);
            });
    }

    /// <summary>
    /// Redeems a registration key (<c>mwr_…</c>) for the ALREADY-REGISTERED instance at
    /// <paramref name="instancePath"/>: when the key is valid and unspent and its plan ranks ABOVE
    /// the instance's, the instance moves to that plan and the key's use is stamped — an UPGRADE
    /// only, never a downgrade (Doc/Architecture/PluginRegistry → "A key raises a registered
    /// instance's plan").
    ///
    /// <para>Why it exists: a fleet machine registers OPEN (free) the moment it is installed, and
    /// only afterwards does its enrolment hand it the plan-bearing key — which the registration lane
    /// then ignored, because an instance that already holds a credential never registers again. A
    /// global administrator raised every such plan by hand. This is the same licence decision a
    /// registration with that key would have taken, applied to an instance that already exists.</para>
    ///
    /// <para>🚨 <b>Authorization is the CALLER's, and it is double possession.</b> The registry
    /// endpoint that calls this has authenticated the instance's OWN durable key and passes that
    /// instance's path — never a path from the request. The registration key is the second half:
    /// minted by a registry administrator for a plan, bounded by its expiry and usage limit. The
    /// record is then read and written as System (it lives in its registrant's partition).</para>
    ///
    /// <para>Outcomes: <see cref="PlanRedemptionOutcome.Upgraded"/> (written, use stamped);
    /// <see cref="PlanRedemptionOutcome.Unchanged"/> (the instance already stands on that plan or an
    /// equivalent one — the use is stamped all the same, so a single-use key minted for this
    /// instance is spent rather than left live); a refusal (key invalid/spent/expired/revoked, key
    /// carries no plan or one the ladder does not know, or a LOWER plan) changes nothing and stamps
    /// nothing.</para>
    /// </summary>
    /// <param name="instancePath">The AUTHENTICATED instance's record path.</param>
    /// <param name="rawRegistrationKey">The presented <c>mwr_</c> key.</param>
    /// <returns>The outcome. Cold — subscribe to redeem.</returns>
    public IObservable<PlanRedemption> RedeemRegistrationKey(string instancePath, string rawRegistrationKey)
    {
        var ladder = hub.ServiceProvider.GetService<PlanTierLadder>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        // The ladder AS SYSTEM: the HTTP caller is anonymous, and on a registry with
        // Access:DenyAnonymous a listing under that identity is EMPTY — every plan would read unknown.
        return (ladder is null
                ? Observable.Return(PlanTierRanks.Empty)
                : accessService.RunAsSystem(() => ladder.Read().Take(1)))
            .SelectMany(ranks => RedeemRegistrationKey(instancePath, rawRegistrationKey, ranks));
    }

    /// <summary>As <see cref="RedeemRegistrationKey(string, string)"/>, deciding with a given ladder
    /// — the seam the tests pin every branch through.</summary>
    internal IObservable<PlanRedemption> RedeemRegistrationKey(
        string instancePath, string rawRegistrationKey, PlanTierRanks ranks)
    {
        if (string.IsNullOrWhiteSpace(instancePath))
            return Observable.Throw<PlanRedemption>(new ArgumentException("An instance path is required.", nameof(instancePath)));
        var keys = hub.ServiceProvider.GetRequiredService<RegistrationKeyService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();

        return keys.Resolve(rawRegistrationKey)
            .SelectMany(resolved => accessService.RunAsSystem(() =>
                    hub.GetMeshNode(instancePath, TimeSpan.FromSeconds(10)).Take(1))
                .SelectMany(node =>
                {
                    var instance = node?.ContentAs<MeshWeaverInstance>(hub.JsonSerializerOptions);
                    if (instance is null)
                        return Observable.Throw<PlanRedemption>(new InvalidOperationException(
                            $"no registered instance at '{instancePath}'"));
                    if (resolved is null)
                        return Observable.Return(Refused(new PlanRedemption(
                            PlanRedemptionOutcome.InvalidKey, instance.InstanceId, instance.Plan,
                            "the registration key is unknown, revoked, expired or already used up")));

                    var (outcome, reason) = RegistrationKeyPlanUpgrade.Decide(instance.Plan, resolved.Key.Tier, ranks);
                    var keyPlan = PlanTierRanks.Canonical(resolved.Key.Tier);
                    switch (outcome)
                    {
                        case PlanRedemptionOutcome.Upgraded:
                            return accessService.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream(instancePath)
                                    .Update(current =>
                                    {
                                        var held = current.ContentAs<MeshWeaverInstance>(hub.JsonSerializerOptions);
                                        // Re-decided on the record the write is applied to: a plan an
                                        // admin raised in between is never lowered by a stale decision.
                                        if (held is null
                                            || RegistrationKeyPlanUpgrade.Decide(held.Plan, keyPlan, ranks).Outcome
                                                != PlanRedemptionOutcome.Upgraded)
                                            return current;
                                        return current with
                                        {
                                            Content = held with
                                            {
                                                Plan = keyPlan,
                                                PlanSetAt = DateTimeOffset.UtcNow,
                                                PlanSetVia = $"registration key {resolved.KeyPath}",
                                            },
                                        };
                                    }))
                                .SelectMany(written =>
                                {
                                    var now = written.ContentAs<MeshWeaverInstance>(hub.JsonSerializerOptions)?.Plan;
                                    // The re-decision above kept the record (its plan changed meanwhile and
                                    // the key no longer raises it): nothing was written, nothing is used.
                                    if (!string.Equals(now, keyPlan, StringComparison.Ordinal))
                                        return Observable.Return(Refused(new PlanRedemption(
                                            PlanRedemptionOutcome.RefusedLower, instance.InstanceId, now,
                                            $"the instance's plan changed to '{PlanOrBaseline(now)}' meanwhile and the key's '{keyPlan}' no longer raises it",
                                            resolved.KeyPath)));
                                    return MirrorCarries(instancePath, written, keyPlan, accessService)
                                        .SelectMany(_ => keys.StampUse(resolved.KeyPath))
                                        .Select(_ =>
                                        {
                                            logger.LogInformation(
                                                "Instance {InstanceId} raised from plan {From} to {To} by redeeming registration key {KeyPath}",
                                                instance.InstanceId, PlanOrBaseline(instance.Plan), keyPlan, resolved.KeyPath);
                                            return new PlanRedemption(outcome, instance.InstanceId, keyPlan, reason, resolved.KeyPath);
                                        });
                                });
                        case PlanRedemptionOutcome.Unchanged:
                            return keys.StampUse(resolved.KeyPath)
                                .Select(_ =>
                                {
                                    logger.LogInformation(
                                        "Instance {InstanceId} presented registration key {KeyPath} for plan {Plan}: {Reason} — "
                                        + "plan unchanged, the key's use is stamped",
                                        instance.InstanceId, resolved.KeyPath, keyPlan, reason);
                                    return new PlanRedemption(outcome, instance.InstanceId, instance.Plan, reason, resolved.KeyPath);
                                });
                        default:
                            return Observable.Return(Refused(new PlanRedemption(
                                outcome, instance.InstanceId, instance.Plan, reason, resolved.KeyPath)));
                    }
                }));
    }

    private PlanRedemption Refused(PlanRedemption refusal)
    {
        logger.LogWarning(
            "Plan redemption for instance {InstanceId} refused ({Outcome}): {Reason}. Nothing was changed; the key was not used.",
            refusal.InstanceId, refusal.Outcome, refusal.Reason);
        return refusal;
    }

    private static string PlanOrBaseline(string? plan) =>
        PlanTierRanks.Canonical(plan) is { Length: > 0 } p ? p : PlanTierRanks.BaselinePlan;

    /// <summary>
    /// 🚨 READ-YOUR-WRITES on the mirror the registry decides with (#6818). The write's terminal is
    /// the OWNER's ack, and the ack and the commit echo that advances this process's live mirror are
    /// two different messages to two different hubs — nothing orders them. So "the promotion
    /// completed" did not mean "the authenticator will see it": a bundle request issued right after
    /// <see cref="SetPlan"/> could still read the pre-promotion record off the mirror and be refused
    /// the package the instance had just been promoted to. The promotion now completes only once the
    /// same mirror <see cref="InstanceRegistryAuthenticator"/> reads (<c>GetMeshNodeStream</c>, the
    /// process-wide cache) carries it: a version past the one the write was diffed against, or the
    /// plan itself (a write that changed nothing mints no version). No timer of its own — the caller's
    /// bound is the bound, and the echo of a write the owner acked is always delivered.
    /// </summary>
    private IObservable<MeshNode> MirrorCarries(
        string instancePath, MeshNode written, string canonical, AccessService accessService) =>
        // Its own System scope: this subscribes on the thread that delivered the ack, where the
        // ambient identity is whatever that thread carried, and the mirror's read gate captures
        // identity at subscribe time — the same reason InstanceRegistryAuthenticator.ReadLive
        // re-seals.
        accessService.RunAsSystem(() => hub.GetMeshNodeStream(instancePath)
            .Where(node => node is not null
                && ((written.Version > 0 && node.Version > written.Version)
                    || string.Equals(
                        node.ContentAs<MeshWeaverInstance>(hub.JsonSerializerOptions)?.Plan,
                        canonical, StringComparison.Ordinal)))
            .Take(1));
}

/// <summary>What a plan redemption (<see cref="InstancePlanService.RedeemRegistrationKey(string, string)"/>) did.</summary>
public enum PlanRedemptionOutcome
{
    /// <summary>The instance moved UP to the key's plan; the key's use is stamped.</summary>
    Upgraded = 0,

    /// <summary>The instance already stands on that plan (or one covering at least as much); the
    /// key's use is stamped, the plan is untouched.</summary>
    Unchanged = 1,

    /// <summary>The key's plan ranks BELOW the instance's — a key never lowers a plan. Nothing used.</summary>
    RefusedLower = 2,

    /// <summary>The key carries no plan, or one the registry's ladder does not know. Nothing used.</summary>
    RefusedNoPlan = 3,

    /// <summary>The key is unknown, revoked, expired or used up. Nothing used.</summary>
    InvalidKey = 4,
}

/// <summary>The outcome of a plan redemption, with the plan the instance stands on afterwards.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="InstanceId">The instance.</param>
/// <param name="Plan">The plan on its record afterwards (null = the baseline).</param>
/// <param name="Reason">Why, in words for a log line.</param>
/// <param name="KeyPath">The key's node path, when the key resolved.</param>
public sealed record PlanRedemption(
    PlanRedemptionOutcome Outcome, string InstanceId, string? Plan, string Reason, string? KeyPath = null)
{
    /// <summary>Whether the instance now stands on (at least) the key's plan and the key is spent for it.</summary>
    public bool Applied => Outcome is PlanRedemptionOutcome.Upgraded or PlanRedemptionOutcome.Unchanged;
}

/// <summary>
/// The PURE upgrade-only rule a redeemed registration key is decided by — whether the key's plan
/// raises the instance's. On the registry's ladder (<see cref="PlanTierRanks"/>):
/// <list type="bullet">
/// <item>a key with no plan, or one the ladder does not know, raises nothing (fail closed — a typo
///   must never widen a licence);</item>
/// <item>an all-access plan (<c>dedicated</c>) covers everything: a key for one raises any plan
///   that is not all-access, and nothing lowers it;</item>
/// <item>otherwise the RANKS decide: higher raises, equal is unchanged, lower is refused. An
///   instance plan that is blank or unknown stands at the baseline, exactly as the licence rule
///   reads it (<see cref="PlanTierRanks.CoversInstance(string?, string?)"/>).</item>
/// </list>
/// </summary>
public static class RegistrationKeyPlanUpgrade
{
    /// <summary>Decides one redemption. Pure and total.</summary>
    /// <param name="instancePlan">The plan on the instance's record (blank = the baseline).</param>
    /// <param name="keyPlan">The plan the key was minted for.</param>
    /// <param name="ranks">The registry's ladder.</param>
    public static (PlanRedemptionOutcome Outcome, string Reason) Decide(
        string? instancePlan, string? keyPlan, PlanTierRanks ranks)
    {
        var key = PlanTierRanks.Canonical(keyPlan);
        if (key.Length == 0)
            return (PlanRedemptionOutcome.RefusedNoPlan, "the registration key carries no plan");
        if (ranks.RankOf(key) is not { } keyRank)
            return (PlanRedemptionOutcome.RefusedNoPlan,
                $"the key's plan '{key}' is not on this registry's tier ladder — an unknown plan licenses nothing");
        var current = PlanTierRanks.Canonical(instancePlan) is { Length: > 0 } named ? named : PlanTierRanks.BaselinePlan;
        if (string.Equals(current, key, StringComparison.OrdinalIgnoreCase))
            return (PlanRedemptionOutcome.Unchanged, $"the instance already stands on plan '{key}'");
        var currentAll = ranks.IsAllAccess(current);
        var keyAll = ranks.IsAllAccess(key);
        if (currentAll && !keyAll)
            return (PlanRedemptionOutcome.RefusedLower,
                $"the instance's plan '{current}' is all-access; the key's '{key}' would lower it");
        if (currentAll && keyAll)
            return (PlanRedemptionOutcome.Unchanged, $"plans '{current}' and '{key}' are both all-access");
        if (keyAll)
            return (PlanRedemptionOutcome.Upgraded, $"'{key}' is all-access and raises '{current}'");
        var currentRank = ranks.RankOf(current) ?? PlanTierRanks.BaselineRank;
        return keyRank > currentRank
            ? (PlanRedemptionOutcome.Upgraded, $"'{key}' (rank {keyRank}) raises '{current}' (rank {currentRank})")
            : keyRank == currentRank
                ? (PlanRedemptionOutcome.Unchanged, $"'{key}' ranks the same as '{current}' ({keyRank})")
                : (PlanRedemptionOutcome.RefusedLower,
                    $"the key's plan '{key}' (rank {keyRank}) ranks below the instance's '{current}' (rank {currentRank}) — a key never lowers a plan");
    }
}
