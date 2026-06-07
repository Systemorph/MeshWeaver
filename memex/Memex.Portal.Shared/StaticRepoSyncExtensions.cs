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

        if (serveFromPartition.Contains("Agent"))
            builder.AddMeshNodes(PartitionDefNode("Agent", "Built-in agents (DB-synced)"));
        if (serveFromPartition.Contains("Model"))
        {
            builder.AddMeshNodes(PartitionDefNode("Model", "Model catalog (DB-synced)"));
            // The model catalog's provider/model content lives under the "_Provider" partition.
            builder.AddMeshNodes(PartitionDefNode(
                ModelProviderNodeType.RootNamespace, "Model providers (DB-synced)"));
        }
        // "Doc" already declares its PartitionDefinition in AddDocumentation.

        builder.ConfigureServices(services =>
        {
            if (serveFromPartition.Contains("Doc"))
                services.AddSingleton<IStaticRepoSource, DocumentationStaticRepoSource>();
            if (serveFromPartition.Contains("Agent"))
                services.AddSingleton<IStaticRepoSource, AgentStaticRepoSource>();
            if (serveFromPartition.Contains("Model"))
                services.AddSingleton<IStaticRepoSource, ModelStaticRepoSource>();

            // Runs after the PG schema-provisioning hosted service (registered earlier by
            // AddPartitionedPostgreSqlPersistence) — hosted services start in registration order.
            services.AddHostedService<StaticRepoImportHostedService>();
            return services;
        });
        return builder;
    }

    // Declares a partition so PostgreSqlPartitionSubscriptionHostedService provisions its PG schema.
    // Mirrors the "Documentation" partition node AddDocumentation adds.
    private static MeshNode PartitionDefNode(string @namespace, string description) =>
        new($"StaticRepo_{@namespace.Trim('_')}", "Admin/Partition")
        {
            NodeType = "Partition",
            Name = description,
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = @namespace,
                DataSource = "PostgreSql",
                Description = description,
                Versioned = false
            }
        };
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
        _subscription = StaticRepoImporter.ImportAll(hub, logger)
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
