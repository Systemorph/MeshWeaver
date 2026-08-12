using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Documentation;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Diagnostics;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared;

/// <summary>
/// Wires static-repo → DB synchronization for the partitions selected by
/// <see cref="StaticRepoSyncFeatureOptions"/>: registers each partition's
/// <see cref="IStaticRepoSource"/>, declares its <see cref="PartitionDefinition"/> (so the PG
/// schema is provisioned), and starts the import hosted service that runs
/// <see cref="StaticRepoImporter.ImportAll"/> on boot. The read-only in-memory static providers
/// for these partitions are skipped at their registration sites (AddAgentType / AddLanguageModelType
/// / AddModelProviderType / AddDocumentation, gated on the same set) so Postgres serves them and
/// accepts the import's writes. No-op when no partition is selected. See
/// <c>Doc/Architecture/StaticRepoImport.md</c>.
/// </summary>
public static class StaticRepoSyncExtensions
{
    public static TBuilder AddStaticRepoSync<TBuilder>(this TBuilder builder,
        IReadOnlySet<string> serveFromPartition,
        IReadOnlyDictionary<string, PartitionSyncMode>? syncModeOverrides = null)
        where TBuilder : MeshBuilder
    {
        if (serveFromPartition.Count == 0)
            return builder; // sync disabled — in-memory serving everywhere, no import.

        // NOTE: partition SCHEMAS are provisioned reactively by the importer itself, via the standard
        // IPartitionStorageProvider.EnsurePartitionProvisioned (reactive + pooled). We do NOT declare
        // PartitionDefinition nodes here to force a schema — that path provisioned the wrong-case
        // schema. See StaticRepoImporter.Run + Doc/Architecture/StaticRepoImport.md.
        builder.ConfigureServices(services =>
        {
            if (serveFromPartition.Contains("Doc"))
                services.AddSingleton<IStaticRepoSource, DocumentationStaticRepoSource>();

            // AI content (Agent / Provider / Harness / Skill) is ONE bundle: register every built-in
            // AI source together whenever any AI partition is served, so a config that names only some
            // of them can NEVER silently drop one (the recurring "Skill was never imported" bug). The
            // set + the bundle live next to the sources in MeshWeaver.AI — adding a new AI content type
            // there auto-joins the import; there is no per-partition allow-list here to forget. (The
            // expand-to-the-whole-bundle also happens in MemexConfiguration so AddAI's serve-from-DB
            // gating stays consistent with the import.)
            if (serveFromPartition.Overlaps(AiContentSources.ContentPartitions))
                services.AddBuiltInAiContentSources();

            // The boot signal the NodeType pre-warm sweep sequences on: without it the sweep's ONE
            // enumeration snapshot can be taken while this import is still landing content, and
            // every type it misses is left to compile on a user's first click. See
            // StaticRepoImportSettled.
            services.AddSingleton<StaticRepoImportSettled>();

            // Runs after the PG schema-provisioning hosted service (registered earlier by
            // AddPartitionedPostgreSqlPersistence) — hosted services start in registration order.
            // A factory (not AddHostedService<T>) so the deploy-time per-partition mode overrides
            // (Features:StaticRepoSync:Modes) reach ImportAll; null/empty = every source keeps its default.
            services.AddSingleton<IHostedService>(sp => new StaticRepoImportHostedService(
                sp.GetRequiredService<IMessageHub>(),
                sp.GetServices<IStaticRepoSource>(),
                syncModeOverrides,
                sp.GetService<ILogger<StaticRepoImportHostedService>>(),
                sp.GetRequiredService<StaticRepoImportSettled>()));
            return services;
        });
        return builder;
    }
}

/// <summary>
/// Boot hook that runs <see cref="StaticRepoImporter.ImportAll"/> once registered
/// <see cref="IStaticRepoSource"/>s exist. Reactive + fire-and-forget: it does NOT block host
/// startup (the PG schema is already provisioned by the time this StartAsync runs, since the
/// schema hosted service is registered earlier). ImportAll impersonates System and is idempotent
/// (fingerprint short-circuit), so re-runs are cheap.
/// </summary>
internal sealed class StaticRepoImportHostedService(
    IMessageHub hub,
    IEnumerable<IStaticRepoSource> sources,
    IReadOnlyDictionary<string, PartitionSyncMode>? syncModeOverrides = null,
    ILogger<StaticRepoImportHostedService>? logger = null,
    StaticRepoImportSettled? settled = null) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!sources.Any())
        {
            settled?.MarkSettled();
            return Task.CompletedTask;
        }

        logger?.LogInformation(
            "[StaticRepoImport] starting sync-context init for {Count} source(s).", sources.Count());
        // 🚨 SubscribeOn the thread pool — NOT the host-startup thread. The import's reactive chain
        // (meshService.Query → CreateNode/Overwrite) round-trips the mesh + per-node hubs; running
        // the subscription on the startup thread re-enters the hub schedulers mid-init and DEADLOCKS
        // (it hung the whole import). The chain is pure IObservable (no FromAsync/await), so
        // SubscribeOn moves the entire subscription cleanly off the startup thread. StartAsync
        // returns immediately (Task.CompletedTask) — no async/await, no blocking. See
        // Doc/Architecture/AsynchronousCalls.md.
        // Pass the built-in AI catalog partitions as the #354 boot-index assertion set: after the
        // import, ImportAll verifies each SERVED catalog (Agent/Provider/Harness/Skill that a source
        // actually backs this run) is registered in the cross-schema search index, force-re-syncing it
        // and surfacing a startup-failure notification for any that materialized but stayed unindexed.
        // What this import COSTS, appended to the line that already reports its outcome — no new log
        // volume. Cumulative since the import started, so the last partition's line is the whole
        // import's bill. The suspected driver of the memex-cloud growth (2.5 GB → 24.5 GB at
        // 130–340 MB/min) is this path plus the sync-stream churn it causes, and until now nothing
        // inside the process reported what one import cost — the growth was only ever inferrable from
        // a container gauge hours later. See MemoryDelta.
        var importMemory = MemoryDelta.Start();

        _subscription = StaticRepoImporter
            .ImportAll(hub, logger, syncModeOverrides, AiContentSources.ContentPartitions)
            .SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)
            .Subscribe(
                r => logger?.LogInformation(
                    "[StaticRepoImport] {Partition}: {Outcome} ({Count} node(s)) — {Memory} "
                    + "(cumulative since import start).",
                    r.Partition, r.Outcome, r.Count, importMemory.ToString()),
                ex =>
                {
                    logger?.LogWarning(ex, "[StaticRepoImport] sync-context init failed.");
                    // Settle on failure TOO: a faulted import must never park the NodeType bake
                    // forever. The sweep proceeds and bakes whatever did land.
                    settled?.MarkSettled();
                },
                () => settled?.MarkSettled());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}
