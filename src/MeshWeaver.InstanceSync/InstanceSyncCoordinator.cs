using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Persistence.Http;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.InstanceSync;

/// <summary>
/// Owns the <see cref="InstanceSyncWorker"/> lifecycle. One global
/// <see cref="IMeshChangeFeed"/> subscription does double duty:
/// <list type="bullet">
///   <item>events for <c>InstanceSyncConfig</c> nodes reconcile the worker set — a created
///     config starts a worker, a deleted one stops it (that IS the "cancel sync" path), an
///     edited one pokes the worker to re-read its settings;</item>
///   <item>every other event is offered to each worker, which filters to its own space and
///     accumulates the change in its durable manifest.</item>
/// </list>
/// On host start, existing registrations are discovered with one system-identity query so
/// syncing resumes across restarts (pending manifests drain immediately). Runs as an
/// <see cref="IHostedService"/> bound to the host lifecycle — the same shape as
/// <c>ApprovalToPublishHandler</c>.
/// </summary>
public sealed class InstanceSyncCoordinator(
    IMessageHub hub,
    IMeshService meshService,
    IMeshChangeFeed feed,
    InstanceSyncService service,
    IRemoteMeshClientFactory clientFactory,
    InstanceSyncOptions options,
    ILogger<InstanceSyncCoordinator>? logger = null) : IHostedService, IDisposable
{
    // Instance state on a mesh-scoped singleton (never static) — dies with the mesh.
    private readonly ConcurrentDictionary<string, InstanceSyncWorker> workers = new(StringComparer.Ordinal);
    private IDisposable? feedSubscription;

    /// <summary>Subscribes the change feed and discovers existing sync registrations.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        feedSubscription = feed.Subscribe(OnChange);

        // Boot discovery: resume every existing registration. System identity — this is
        // infrastructure enumeration across partitions, not a user read.
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => meshService
                    .Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{InstanceSyncService.ConfigNodeType}"))
                    .Where(change => change.ChangeType == QueryChangeType.Initial)
                    .Take(1))
            .Timeout(TimeSpan.FromSeconds(30))
            .Subscribe(
                change =>
                {
                    foreach (var node in change.Items)
                        EnsureWorker(node.Path);
                    logger?.LogInformation("Instance sync resumed {Count} registration(s)", change.Items.Count);
                },
                ex => logger?.LogWarning(ex,
                    "Instance sync boot discovery failed — registrations resume on their next change event"));

        return Task.CompletedTask;
    }

    /// <summary>Stops every worker and the feed subscription.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The currently active workers (exposed for tests/diagnostics).</summary>
    public IReadOnlyCollection<InstanceSyncWorker> Workers => workers.Values.ToArray();

    private void OnChange(MeshChangeEvent evt)
    {
        // Deleted events do not reliably carry NodeType (several publish paths pass null) —
        // a removed registration is recognized by its path instead.
        if (evt.Kind == MeshChangeKind.Deleted && workers.ContainsKey(evt.Path))
        {
            StopWorker(evt.Path);
            return;
        }

        if (string.Equals(evt.NodeType, InstanceSyncService.ConfigNodeType, StringComparison.Ordinal))
        {
            if (evt.Kind != MeshChangeKind.Deleted)
                EnsureWorker(evt.Path)?.OnConfigChanged();
            return;
        }

        // Content events: each worker filters to its own space (few workers, cheap filter).
        foreach (var worker in workers.Values)
            worker.OnLocalChange(evt);
    }

    private InstanceSyncWorker? EnsureWorker(string configPath)
    {
        if (workers.TryGetValue(configPath, out var existing))
            return existing;

        // {space}/_Sync/{sourceId} — anything else is not a registration path.
        var segments = configPath.Split('/');
        if (segments.Length != 3 || !string.Equals(segments[1], InstanceSyncService.ConfigId, StringComparison.Ordinal))
        {
            logger?.LogWarning("Ignoring InstanceSyncConfig at unexpected path {Path}", configPath);
            return null;
        }

        var created = new InstanceSyncWorker(
            hub, service, clientFactory, options, segments[0], segments[2],
            logger: hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<InstanceSyncWorker>());
        if (!workers.TryAdd(configPath, created))
        {
            created.Dispose(); // concurrent registration won the race
            return workers.TryGetValue(configPath, out var winner) ? winner : null;
        }
        logger?.LogInformation("Instance sync worker started for {Config}", configPath);
        created.Start();
        return created;
    }

    private void StopWorker(string configPath)
    {
        if (workers.TryRemove(configPath, out var worker))
        {
            worker.Dispose();
            logger?.LogInformation("Instance sync worker stopped for {Config} (registration removed)", configPath);
        }
    }

    /// <summary>Disposes the feed subscription and every worker.</summary>
    public void Dispose()
    {
        feedSubscription?.Dispose();
        feedSubscription = null;
        foreach (var path in workers.Keys.ToArray())
            StopWorker(path);
    }
}
