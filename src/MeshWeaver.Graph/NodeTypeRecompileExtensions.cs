using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The ONE recompile step every node-update channel runs after landing content: derive the
/// NodeTypes whose resolved sources the update touched (<see cref="RecompileClosure"/> — the
/// type's own node, its own <c>Source/</c>/<c>Test/</c> subtree, AND every type reaching a changed
/// node via a <c>shared=@…</c> source reference) and request their release under the SYSTEM
/// identity, dependencies before dependents.
///
/// <para>Without this step an update is only half-applied: the nodes read current while the mesh
/// keeps executing the previous assembly — the "assembly is the deliverable" failure the GitSync
/// deploy procedure existed to hand-patch (sync → recompile by hand → verify
/// <c>compiledSources</c>), paid again on 2026-08-03/04. The GitSync re-import channel calls this
/// as part of the sync transaction; the release requests are ISSUED within the transaction (the
/// compiles themselves run on the per-type hubs' compile watchers, serialized on the Compile
/// IoPool — an update must not block on Roslyn).</para>
/// </summary>
public static class NodeTypeRecompileExtensions
{
    private static readonly TimeSpan TypeEnumerationBudget = TimeSpan.FromSeconds(30);

    /// <summary>Budget for the one adopt-before-compile attempt (#1707 slice 3) — bundle reads
    /// plus per-type seeds; on elapse every affected type simply compiles, as it always did.</summary>
    private static readonly TimeSpan PrebuiltSeedBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Requests a release for every NodeType affected by <paramref name="changedNodePaths"/>
    /// (written or pruned node paths). Emits the release-requested type paths, in dependency
    /// order, exactly once; an empty change set emits an empty list without querying anything.
    ///
    /// <para>Runs as SYSTEM end-to-end — the type enumeration spans every partition
    /// (infrastructure, not a user-attributable read; mirrors <c>DynamicTypePreWarmer</c>), and the
    /// release trigger is a node write on types the syncing user may not own, consistent with how
    /// the update's own writes land. A derivation failure is surfaced LOUDLY on
    /// <paramref name="progress"/> (an <see cref="LogLevel.Error"/> line — on an activity sink this
    /// flips the terminal status) and emits empty rather than failing the caller: the content is
    /// applied either way, and the operator must learn the assemblies were left stale.</para>
    ///
    /// <para>Types are enumerated from the query index (<c>nodeType:NodeType</c>) — eventually
    /// consistent, which is correct for an UPDATE channel: the affected types existed before the
    /// update by definition. A type CREATED by the very same update needs no release — its first
    /// activation compiles it fresh.</para>
    /// </summary>
    /// <param name="hub">The hub the update runs on.</param>
    /// <param name="changedNodePaths">The node paths the update wrote or pruned.</param>
    /// <param name="progress">Optional progress sink (an activity's <c>ctx.Log</c>): receives the
    /// recompile set, per-type release failures, and derivation failures.</param>
    public static IObservable<IReadOnlyList<string>> ReleaseAffectedNodeTypes(
        this IMessageHub hub,
        IReadOnlyCollection<string> changedNodePaths,
        Action<string, LogLevel>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (changedNodePaths.Count == 0)
            return Observable.Return<IReadOnlyList<string>>([]);

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.NodeTypeRecompileExtensions");
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();

        return Observable.Using(
                () => AccessContextScope.AsSystem(accessService),
                _ => meshService
                    .Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{MeshNode.NodeTypePath}"))
                    .Take(1)
                    .Timeout(TypeEnumerationBudget))
            .Select(change =>
            {
                // Shape-tolerant definition read (ContentAs): a degraded JsonElement content must
                // still contribute its declared Sources — silently dropping it would silently skip
                // its recompile, the exact failure class this step closes.
                var types = change.Items
                    .Where(n => !string.IsNullOrEmpty(n.Path) && n.State == MeshNodeState.Active)
                    .GroupBy(n => n.Path!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => g.First().ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions, logger),
                        StringComparer.OrdinalIgnoreCase);

                var affected = RecompileClosure.AffectedTypes(types, changedNodePaths);
                var ordered = RecompileClosure.OrderAffected(types, affected, out var cyclic);
                if (cyclic.Count > 0)
                {
                    var cycleList = string.Join(", ", cyclic);
                    logger?.LogWarning(
                        "[Recompile] Dependency cycle among affected NodeTypes: {Cyclic} — released in deterministic flush order.",
                        cycleList);
                    progress?.Invoke(
                        $"⚠ Dependency cycle among NodeTypes: {cycleList} — recompiled in flush order, not dependency order.",
                        LogLevel.Warning);
                }
                return ordered;
            })
            .SelectMany(ordered =>
            {
                if (ordered.Count == 0)
                    return Observable.Return(ordered);
                logger?.LogInformation(
                    "[Recompile] {Count} NodeType(s) affected by {Changed} changed node(s): {Types}",
                    ordered.Count, changedNodePaths.Count, string.Join(", ", ordered));
                progress?.Invoke(
                    $"Recompiling {ordered.Count} NodeType(s): {string.Join(", ", ordered)}",
                    LogLevel.Information);

                // #1707 slice 3 — adopt-before-compile: give the deployment's prebuilt bundle
                // sources one bounded chance to supply these types' assemblies FIRST (a git
                // push's commit is typically baked by the content repo's CI under this same
                // framework identity). An adopted type then SATISFIES its release request in the
                // watcher (Ok + sources current + usable build ⇒ no Roslyn); anything not adopted
                // compiles exactly as before. Never faults, never blocks past its budget; a host
                // without bundle consumption has no consumer and skips straight through.
                var consumer = hub.ServiceProvider.GetService<IPrebuiltAssemblyConsumer>();
                var seed = consumer is null
                    ? Observable.Return(0)
                    : consumer.SeedForTypes(ordered)
                        .Take(1)
                        .Timeout(PrebuiltSeedBudget)
                        .Catch<int, Exception>(ex =>
                        {
                            logger?.LogWarning(ex,
                                "[Recompile] Prebuilt adoption attempt failed — every affected type compiles instead.");
                            return Observable.Return(0);
                        });

                return seed.Select(adopted =>
                {
                    if (adopted > 0)
                    {
                        logger?.LogInformation(
                            "[Recompile] Adopted {Adopted} prebuilt assembly(ies) for the affected set — "
                            + "their release requests settle without compiling.", adopted);
                        progress?.Invoke(
                            $"Adopted {adopted} prebuilt assembly(ies) — the rest compile.",
                            LogLevel.Information);
                    }
                    // The release trigger is a node write; SYSTEM-impersonated per the channel's own
                    // write identity (RequestNodeTypeRelease captures the caller synchronously, so
                    // the scope around the loop covers every request). Requested for EVERY affected
                    // type, adopted included — the watcher's satisfied-path consumes the trigger for
                    // current builds, and this stays the one place that guarantees a request lands.
                    using (accessService?.ImpersonateAsSystem())
                        foreach (var path in ordered)
                            hub.RequestNodeTypeRelease(path, onError: msg =>
                            {
                                logger?.LogError(
                                    "[Recompile] Release request for {Path} failed: {Message}", path, msg);
                                progress?.Invoke(
                                    $"Recompile of {path} could not be requested: {msg} — its assembly is STALE until compiled manually.",
                                    LogLevel.Error);
                            });
                    return ordered;
                });
            })
            .Select(ordered => (IReadOnlyList<string>)ordered)
            .Catch<IReadOnlyList<string>, Exception>(ex =>
            {
                // LOUD, not fatal: the content landed; what failed is deriving/issuing the
                // recompiles. The Error line flips an owning activity's terminal status, so the
                // stale-assembly state is impossible to miss — while the update itself stands.
                logger?.LogError(ex, "[Recompile] Deriving the recompile set failed.");
                progress?.Invoke(
                    $"Recompile derivation failed: {ex.Message} — affected NodeType assemblies are STALE until compiled manually.",
                    LogLevel.Error);
                return Observable.Return<IReadOnlyList<string>>([]);
            });
    }
}
