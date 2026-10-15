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
        return hub.IsGlobalAdmin()
            .Take(1)
            .SelectMany(isAdmin => isAdmin
                ? accessService.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream(instancePath)
                    .Update(current => current with
                    {
                        Content = current.ContentAs<MeshWeaverInstance>(hub.JsonSerializerOptions) is { } instance
                            ? instance with { Plan = canonical }
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
