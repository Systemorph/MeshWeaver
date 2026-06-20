using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared;

/// <summary>
/// Boot seed that ships compiled <c>Release</c>s WHEREVER we ship code NodeTypes — the
/// documentation partition AND every shipped sample/platform partition (<c>ACME</c>, <c>FutuRe</c>,
/// <c>Northwind</c>, <c>Cornerstone</c>, <c>MeshWeaver</c>, …). At startup it finds every code
/// NodeType in those partitions that has no usable build yet and triggers a release for it — under
/// the <b>System</b> identity, exactly like the per-NodeType first-build kickoff.
///
/// <para><b>Why:</b> a shipped partition's NodeTypes otherwise compile ON-DEMAND the first time a
/// user navigates to them — and that on-demand compile path (its <c>_Activity/compile-*</c> writes)
/// is exactly what storm-failed on atioz 2026-06-18 when the activity write ran without a writer
/// identity. Pre-building the releases at provision/deploy time as System means the runtime path is
/// always a cache hit: no on-demand compile, no phantom <c>_Activity/compile-*</c> subscribe storm.
/// Several shipped partitions (<c>Doc</c>) are read-only — only a System-credentialed compile can
/// fill their cache and write the <c>Release</c> node, which is precisely the credential split this
/// workflow establishes.</para>
///
/// <para>Each release runs entirely as System (no <c>RequestedReleaseBy</c>), so the compile fills
/// the cache and the <c>Release</c> node is created even on a read-only partition. Idempotent: a
/// NodeType that already has a usable build is skipped, so re-boots are cheap. Fire-and-forget off
/// the thread pool — never blocks host startup.</para>
/// </summary>
public static class ShippedReleaseSeed
{
    /// <summary>
    /// The partitions whose code NodeTypes ship pre-built releases. The platform documentation plus
    /// the bundled sample spaces — i.e. every partition we ship NodeTypes in. User/Space partitions
    /// created at runtime are NOT here: those NodeTypes are authored by users and released by them
    /// (the <see cref="Permission.Compile"/>-gated "Create Release" button).
    /// </summary>
    public static readonly IReadOnlyList<string> ShippedPartitions =
    [
        "Doc", "ACME", "FutuRe", "Northwind", "Cornerstone", "MeshWeaver"
    ];

    /// <summary>
    /// Trigger a System release for every un-built code NodeType under each of
    /// <paramref name="partitions"/>. Returns the path of each NodeType a release was triggered for
    /// (one <c>OnNext</c> per trigger). Composed entirely from <c>IObservable</c> — no
    /// <c>await</c>/<c>FromAsync</c>; every partition's work runs under one System scope.
    /// </summary>
    public static IObservable<string> SeedReleases(
        IMessageHub hub, IEnumerable<string> partitions, ILogger? logger = null)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Empty<string>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var workspace = hub.GetWorkspace();

        return Observable.Using(
            () => AccessContextScope.AsSystem(accessService),
            _ => partitions
                .ToObservable()
                .SelectMany(partition => SeedPartition(meshService, workspace, partition, logger)));
    }

    private static IObservable<string> SeedPartition(
        IMeshService meshService, IWorkspace workspace, string partitionNamespace, ILogger? logger) =>
        meshService
            // List the NodeType nodes in the partition. Query is fine here — we only need the
            // PATHS; the authoritative per-node content is re-read off the live stream below
            // (query rows carry stale Content by design).
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{partitionNamespace} nodeType:{MeshNode.NodeTypePath} scope:subtree"))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .SelectMany(result => result.Items)
            .Select(n => n.Path)
            .Distinct()
            .SelectMany(path => workspace.GetMeshNodeStream(path)
                .Where(node => node?.Content is NodeTypeDefinition)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(10))
                .Where(node =>
                {
                    // Skip NodeTypes that already have a usable, current build — the runtime path
                    // is already a cache hit for them. Trigger only the un-built ones.
                    var def = (NodeTypeDefinition)node!.Content!;
                    return def.CompilationStatus != CompilationStatus.Ok
                           || string.IsNullOrEmpty(def.LatestReleasePath);
                })
                .SelectMany(node =>
                {
                    var nodePath = node!.Path;
                    logger?.LogInformation(
                        "[ShippedReleaseSeed] triggering System release for un-built shipped NodeType {Path}",
                        nodePath);
                    // Canonical request-via-stream-update trigger, as System — RequestedReleaseBy
                    // stays null so the Release node is created under System (a read-only shipped
                    // partition like Doc admits no user write).
                    return workspace.GetMeshNodeStream(nodePath)
                        .Update(curr =>
                        {
                            if (curr?.Content is not NodeTypeDefinition def) return curr!;
                            return curr with
                            {
                                Content = def with
                                {
                                    RequestedReleaseAt = DateTimeOffset.UtcNow,
                                    RequestedReleaseForce = false,
                                    RequestedReleaseBy = null
                                }
                            };
                        })
                        .Select(_ => nodePath);
                })
                // A single un-buildable NodeType (or a partition that isn't present in this
                // deployment) must not abort the whole seed.
                .Catch<string, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[ShippedReleaseSeed] release trigger failed for {Path} (skipped)", path);
                    return Observable.Empty<string>();
                }))
            .Catch<string, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[ShippedReleaseSeed] partition {Partition} not present / not queryable (skipped)",
                    partitionNamespace);
                return Observable.Empty<string>();
            });
}

/// <summary>
/// Boot hook that runs <see cref="ShippedReleaseSeed.SeedReleases"/> once the mesh is up.
/// Mirrors <c>StaticRepoImportHostedService</c>: reactive, fire-and-forget, <c>SubscribeOn</c> the
/// thread pool so it never re-enters the hub schedulers on the startup thread.
/// </summary>
public sealed class ShippedReleaseSeedHostedService(
    IMessageHub hub,
    ILogger<ShippedReleaseSeedHostedService>? logger = null) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger?.LogInformation(
            "[ShippedReleaseSeed] pre-building shipped code-NodeType releases as System for {Partitions}.",
            string.Join(", ", ShippedReleaseSeed.ShippedPartitions));
        _subscription = ShippedReleaseSeed
            .SeedReleases(hub, ShippedReleaseSeed.ShippedPartitions, logger)
            .SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)
            .Subscribe(
                path => logger?.LogInformation(
                    "[ShippedReleaseSeed] release triggered for {Path}.", path),
                ex => logger?.LogWarning(ex, "[ShippedReleaseSeed] seed failed."),
                () => logger?.LogInformation("[ShippedReleaseSeed] seed complete."));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}
