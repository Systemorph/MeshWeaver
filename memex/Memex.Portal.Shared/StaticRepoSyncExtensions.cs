using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Documentation;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
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
        IReadOnlySet<string> serveFromPartition) where TBuilder : MeshBuilder
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
            if (serveFromPartition.Contains("Agent"))
                services.AddSingleton<IStaticRepoSource, AgentStaticRepoSource>();
            if (serveFromPartition.Contains("Model"))
                services.AddSingleton<IStaticRepoSource, ModelStaticRepoSource>();
            if (serveFromPartition.Contains("Harness"))
                services.AddSingleton<IStaticRepoSource, HarnessStaticRepoSource>();
            if (serveFromPartition.Contains("Skill"))
                services.AddSingleton<IStaticRepoSource, SkillStaticRepoSource>();

            // Runs after the PG schema-provisioning hosted service (registered earlier by
            // AddPartitionedPostgreSqlPersistence) — hosted services start in registration order.
            services.AddHostedService<StaticRepoImportHostedService>();
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
    ILogger<StaticRepoImportHostedService>? logger = null) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!sources.Any())
            return Task.CompletedTask;

        logger?.LogInformation(
            "[StaticRepoImport] starting sync-context init for {Count} source(s).", sources.Count());
        // 🚨 SubscribeOn the thread pool — NOT the host-startup thread. The import's reactive chain
        // (meshService.Query → CreateNode/Overwrite) round-trips the mesh + per-node hubs; running
        // the subscription on the startup thread re-enters the hub schedulers mid-init and DEADLOCKS
        // (it hung the whole import). The chain is pure IObservable (no FromAsync/await), so
        // SubscribeOn moves the entire subscription cleanly off the startup thread. StartAsync
        // returns immediately (Task.CompletedTask) — no async/await, no blocking. See
        // Doc/Architecture/AsynchronousCalls.md.
        _subscription = StaticRepoImporter.ImportAll(hub, logger)
            .SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)
            .Subscribe(
                r => logger?.LogInformation(
                    "[StaticRepoImport] {Partition}: {Outcome} ({Count} node(s)).",
                    r.Partition, r.Outcome, r.Count),
                ex => logger?.LogWarning(ex, "[StaticRepoImport] sync-context init failed."));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}
