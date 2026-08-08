using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Turns "a module exists in the configured plugin repo" into "this instance carries it" — without
/// anyone creating a Space by hand (#833).
///
/// <para><b>The problem.</b> On real instances modules arrive through per-Space
/// <c>{Space}/_GitSync</c> entries, not the plugin catalog: memex carries 37 sync configs and zero
/// install records. So a module with no entry is simply <b>not on the mesh, and nothing says so</b> —
/// <c>Planning</c> and <c>Ifrs17</c> were absent purely because nobody had made their entries, while
/// nine siblings from the same repo were present. And the entry could not be made by hand either: a
/// <c>_GitSync</c> needs its Space to exist, and creating a Space the ordinary way makes the creator
/// <b>Admin on a partition the repo owns</b> — forbidden, and re-minted by every human-run sync.
/// "Add a module to an instance" had no safe self-service path at all.</para>
///
/// <para><b>The unit of configuration is the REPO, not the Space.</b> Two flags per configured
/// source (<c>PluginCatalog:Sources:N:AutoDiscover</c> / <c>:AutoSync</c>, i.e. the deployment's Helm
/// values — the same place the source itself lives):</para>
/// <list type="bullet">
///   <item><b>AutoDiscover</b> — enumerate the repo's modules and record, per module, whether this
///     instance carries it. Writes nothing into a Space. This alone fixes the core complaint: absence
///     stops being invisible.</item>
///   <item><b>AutoSync</b> — additionally PROVISION the missing ones: Space, declared access, and the
///     <c>{Space}/_GitSync</c> entry, followed by the first import.</item>
/// </list>
///
/// <para><b>🚨 Everything is created as SYSTEM, and that is the whole point.</b> The Space root is
/// created under <c>ImpersonateAsSystem</c>, so <c>createdBy</c> is <c>system-security</c> — and both
/// grant-minting paths (<c>SpacePostCreationHandler</c>'s creator grant and
/// <c>EnsurePartitionBootstrap</c>'s heal) skip a System creator by construction. No human Admin
/// grant is ever minted on a repo-owned partition, so there is none for
/// <see cref="SystemOwnedAccessRetractionHandler"/> to retract afterwards; the seven-second window it
/// exists to close never opens. The triggering user (a webhook, a boot) is not the creator and gains
/// nothing.</para>
///
/// <para><b>When it runs.</b> Once at boot (after the default install has settled — it is the same
/// partitions), and again on every green build of the repo, reacting to the <c>BuildCompletion</c>
/// node <c>MeshWeaver.GitSync</c>'s webhook writes. Scans are SERIALIZED through one subject +
/// <c>Concat</c>: each scan writes partitions, and a fan-out of cold partition creations is exactly
/// what crashes writes into a fresh mesh. No polling, no timers, no watchdog.</para>
///
/// <para><b>Idempotent by construction.</b> A module that already has a <c>_GitSync</c> (or an
/// install record) is reported and stepped over; re-running writes nothing. A module whose path is
/// already occupied by somebody else's Space is NEVER adopted — wiring a <c>_GitSync</c> onto it
/// would make that partition system-owned and retract its owner's access. Removed modules are
/// surfaced as <see cref="ModuleDiscoveryStatus.Orphaned"/>, never deleted.</para>
///
/// <para>Instance-scoped, not static: the subscriptions live and die with the mesh
/// (<c>Doc/Architecture/NoStaticState</c>). Reactive throughout — no <c>async</c>/<c>await</c>.</para>
/// </summary>
public sealed class ModuleDiscoveryService : IHostedService, IDisposable
{
    /// <summary>How long a mesh-wide state query gets before the scan gives up on it and reports an
    /// empty answer (which fails CLOSED: an unknown state provisions nothing).</summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long the single-node existence probe waits on a host with no
    /// <see cref="IStorageAdapter"/> to read from directly.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly IMessageHub hub;
    private readonly ILogger<ModuleDiscoveryService>? logger;
    private readonly CompositeDisposable subscriptions = new();

    /// <summary>The scan queue. One subject + <c>Concat</c> is how "one at a time" is expressed on the
    /// mesh — never a lock or a semaphore (AGENTS.md). Instance state; one per mesh.</summary>
    private readonly Subject<ScanRequest> scans = new();

    /// <summary>
    /// The ENQUEUE side of <see cref="scans"/>, and the only thing that may be pushed to.
    ///
    /// <para>🚨 This service has TWO producers by design — the boot pass emits from a pool thread,
    /// and every build-node emission arrives on the hub's scheduler — so concurrent <c>OnNext</c> is
    /// the normal case, not a corner. A bare <c>Subject&lt;T&gt;</c> is not thread-safe for that: two
    /// producers can interleave inside one notification and tear it or throw. <c>Concat</c> does NOT
    /// help — it serializes CONSUMPTION (which inner observable runs when), and says nothing about
    /// who may call <c>OnNext</c> concurrently.</para>
    ///
    /// <para><c>Subject.Synchronize</c> is Rx's own answer and stays inside the reactive model — this
    /// is not a hand-woven async gate: it guards a synchronous hand-off, never a wait.</para>
    /// </summary>
    private readonly ISubject<ScanRequest> enqueue;

    /// <summary>Build-node paths already subscribed, so two sources naming the same repo do not open
    /// two subscriptions. Instance state; one per mesh.</summary>
    private readonly ConcurrentDictionary<string, byte> watchedBuilds = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="ModuleDiscoveryService"/> class.</summary>
    /// <param name="hub">The hub this service runs on.</param>
    /// <param name="logger">Diagnostics.</param>
    public ModuleDiscoveryService(IMessageHub hub, ILogger<ModuleDiscoveryService>? logger = null)
    {
        this.hub = hub;
        this.logger = logger;
        enqueue = Subject.Synchronize(scans);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        subscriptions.Dispose();
        scans.Dispose();
    }

    private void Start()
    {
        var sources = DiscoverableSources();
        if (sources.Count == 0)
        {
            logger?.LogDebug(
                "[ModuleDiscovery] no configured source has AutoDiscover on — nothing is enumerated.");
            return;
        }

        // The scan pipeline. ObserveOn the pool so a scan NEVER runs on the thread that queued it —
        // a build-node emission arrives on the hub's scheduler, and running a partition-creating
        // scan there re-enters the hub mid-turn. Concat, so scans never overlap.
        subscriptions.Add(scans
            .ObserveOn(TaskPoolScheduler.Default)
            .Select(request => Scan(request.Source, request.GitRef)
                .Catch((Exception exception) =>
                {
                    // A scan that throws must not tear down the pipeline that serves every other
                    // source and every later build. Logged at Error — a scan failing entirely is a
                    // misconfiguration, not a routine outcome.
                    logger?.LogError(exception,
                        "[ModuleDiscovery] scanning {Name} @ {Ref} failed; the record is left as it was.",
                        request.Source.Name, request.GitRef);
                    return Observable.Empty<ModuleDiscovery>();
                }))
            .Concat()
            .Subscribe(
                record => logger?.LogInformation(
                    "[ModuleDiscovery] {Repo} @ {Ref}: {Summary}",
                    record.RepositoryUrl, record.GitRef, Summarize(record)),
                exception => logger?.LogError(exception, "[ModuleDiscovery] the scan pipeline faulted.")));

        // The boot scan waits for the default install to settle: both touch the same partitions, and
        // InstanceAutoRegistrationService.Completed is the documented signal for exactly this. It
        // always emits (its own pass catches everything), so this is a sequencing gate, not a wait
        // that can hang. SubscribeOn the pool: Completed is an AsyncSubject and may replay
        // synchronously onto the host-startup thread.
        var defaults = hub.ServiceProvider.GetService<InstanceAutoRegistrationService>();
        var ready = defaults is null
            ? Observable.Return(Unit.Default)
            : defaults.Completed.Take(1).Select(_ => Unit.Default);
        subscriptions.Add(ready
            .SubscribeOn(TaskPoolScheduler.Default)
            .Subscribe(
                _ =>
                {
                    // Through `enqueue`, never `scans` — a build emission can land on the hub
                    // scheduler at the same moment (see the field's remarks).
                    foreach (var source in sources)
                        enqueue.OnNext(new ScanRequest(source, source.GitRef));
                },
                exception => logger?.LogError(exception,
                    "[ModuleDiscovery] the boot scan could not start.")));

        foreach (var source in sources)
            WatchGreenBuilds(source);
    }

    /// <summary>
    /// The configured sources that opted into discovery. Read straight from
    /// <c>PluginCatalog:Sources</c> through the SHARED reader, so "what the registry serves", "what
    /// the default install reads" and "what discovery enumerates" can never drift apart.
    ///
    /// <para>A source that did not opt in is filtered out HERE, once — which is what makes "both
    /// flags off" mean literally nothing runs: no listing, no query, no record. Internal so a test
    /// can pin that against a real configuration rather than a re-implementation of the rule.</para>
    /// </summary>
    /// <param name="configuration">The configuration to read; null resolves it from the container.</param>
    internal IReadOnlyList<ConfiguredPackageSource> DiscoverableSources(IConfiguration? configuration = null)
    {
        var config = configuration ?? hub.ServiceProvider.GetService<IConfiguration>();
        if (config is null)
            return [];
        return PackageSources.FromConfiguration(hub, config, logger)
            .Where(s => s.AutoDiscover)
            .ToList();
    }

    /// <summary>
    /// Re-scans the source whenever its repo builds green — the same <c>BuildCompletion</c> node
    /// <see cref="PluginUpdateWatcher"/> subscribes to, and the same reason: a webhook already tells
    /// us when the repo changed, so nothing here polls. The scan runs at the build's HEAD sha, so a
    /// module is discovered from exactly the tree that was proven green.
    /// </summary>
    private void WatchGreenBuilds(ConfiguredPackageSource source)
    {
        var (owner, repo) = ModuleDiscovery.SplitRepo(source.RepoPath);
        if (owner.Length == 0 || repo.Length == 0)
            return;   // a local path / single-segment source never receives webhooks

        var buildPath = BuildCompletion.PathFor(owner, repo);
        if (!watchedBuilds.TryAdd(buildPath, 0))
            return;

        logger?.LogInformation(
            "[ModuleDiscovery] {Name} watches {BuildPath} for green builds (autoSync={AutoSync}).",
            source.Name, buildPath, source.AutoSync);

        subscriptions.Add(hub.GetMeshNodeStream(buildPath)
            .Select(node => node?.ContentAs<BuildCompletion>(hub.JsonSerializerOptions, logger))
            .Where(build => build is { HeadSha.Length: > 0 })
            .Select(build => build!)
            // A rebuild of the SAME commit re-lists an identical tree; the scan would be a no-op, so
            // skip it. This is a cheap re-emission guard, not the "did anything change" decision —
            // that is per module, against what the instance actually carries.
            .DistinctUntilChanged(build => build.HeadSha)
            .Subscribe(
                // Through `enqueue`: this emission arrives on the hub's scheduler while the boot
                // pass may still be pushing from a pool thread.
                build => enqueue.OnNext(new ScanRequest(source, build.HeadSha)),
                exception => logger?.LogWarning(exception,
                    "[ModuleDiscovery] the build stream for {BuildPath} faulted.", buildPath)));
    }

    /// <summary>
    /// ONE scan of ONE source: enumerate the repo's modules at <paramref name="gitRef"/>, diff
    /// against what this instance already carries, provision what is missing when the source opted
    /// in, and write the <see cref="ModuleDiscovery"/> record. Cold — the work runs on Subscribe.
    ///
    /// <para>Internal so a test drives the very same method the boot pass and the build watcher do:
    /// production differs only in where the source comes from.</para>
    /// </summary>
    /// <param name="source">The configured source to scan.</param>
    /// <param name="gitRef">The git ref to enumerate at.</param>
    internal IObservable<ModuleDiscovery> Scan(ConfiguredPackageSource source, string gitRef)
    {
        var effectiveRef = string.IsNullOrWhiteSpace(gitRef) ? "HEAD" : gitRef;
        return source.Source.ListPackages(effectiveRef)
            .Take(1)
            .SelectMany(modules => ReadInstanceState()
                .SelectMany(state => ReadPreviousRecord(source)
                    .SelectMany(previous => Reconcile(source, effectiveRef, modules, state, previous))));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  What this instance already carries
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The two ways content arrives on an instance, read once per scan: the per-Space
    /// <c>_GitSync</c> entries (how it actually happens in production) and the plugin catalog's
    /// install records. Read as SYSTEM — a scan runs with no user, and neither the sync configs nor
    /// the install records are anonymous-readable.
    ///
    /// <para>A query that fails yields an EMPTY answer, and empty state provisions nothing new that
    /// matters: an unknown instance state must never be read as "nothing is here, create everything".
    /// The occupancy probe below is the second gate that makes that safe.</para>
    /// </summary>
    private IObservable<InstanceState> ReadInstanceState()
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();

        IObservable<IReadOnlyList<MeshNode>> Query(string query) => Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query)))
            .Take(1)
            .Timeout(QueryTimeout)
            .Select(change => (IReadOnlyList<MeshNode>)change.Items.ToList())
            .Catch((Exception exception) =>
            {
                logger?.LogWarning(exception,
                    "[ModuleDiscovery] reading instance state ('{Query}') failed; treating it as empty.",
                    query);
                return Observable.Return<IReadOnlyList<MeshNode>>([]);
            });

        var installed = Query(
            $"path:{PackageInstaller.InstalledPartition} scope:children "
            + $"nodeType:{PackageInstaller.PackageNodeType}");
        var syncConfigs = Query($"nodeType:{GitHubSyncService.ConfigNodeType}");

        return Observable.Zip(installed, syncConfigs, (records, configs) => new InstanceState(
            records.Select(n => n.Id).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            configs
                // {Space}/_GitSync (primary) and {Space}/_GitSync/{sourceId} (additional) both
                // resolve to the owning partition through its namespace's first segment.
                .Select(n => (
                    Partition: AccessAssignmentGuard.PartitionOf(n.Namespace ?? ""),
                    Config: n.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions, logger)))
                .Where(x => x.Partition.Length > 0)
                .GroupBy(x => x.Partition, StringComparer.OrdinalIgnoreCase)
                .ToImmutableDictionary(
                    g => g.Key,
                    g => g.Select(x => x.Config).FirstOrDefault(c => c is not null) ?? new GitHubSyncConfig(),
                    StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Whether a node already occupies <paramref name="path"/>. A persistence-direct read (never a
    /// point <c>GetMeshNodeStream</c> on a path that is expected to be ABSENT — that NotFound-storms
    /// the router, and the whole point here is that most probes come back empty).
    ///
    /// <para>A host with no <see cref="IStorageAdapter"/> falls back to the authoritative single-node
    /// read rather than answering <c>false</c>. Answering "not occupied" without looking is what made
    /// an existing Space report <see cref="ModuleDiscoveryStatus.Failed"/> instead of
    /// <see cref="ModuleDiscoveryStatus.Occupied"/> — and since accurate reporting IS the product in
    /// discovery-only mode, the probe has to be answerable in every configuration, not merely in the
    /// one that has a storage adapter.</para>
    /// </summary>
    private IObservable<bool> IsOccupied(string path)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        var probe = storage is not null
            ? Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => storage.Read(path, hub.JsonSerializerOptions))
            : Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => hub.GetMeshNode(path, ProbeTimeout));
        return probe
            .Take(1)
            .Select(node => node is not null)
            .Catch((Exception exception) =>
            {
                logger?.LogWarning(exception,
                    "[ModuleDiscovery] probing '{Path}' failed; treating it as occupied so nothing is "
                    + "created over content that may be there.", path);
                // Fail CLOSED: an unreadable probe must not be read as "the path is free".
                return Observable.Return(true);
            });
    }

    /// <summary>The previous scan's record, keyed by module id — the source of
    /// <c>FirstSeenAt</c>/<c>ProvisionedAt</c> continuity and of "did this module's status CHANGE",
    /// which is what keeps a re-scan from re-notifying.</summary>
    private IObservable<ImmutableDictionary<string, DiscoveredModule>> ReadPreviousRecord(
        ConfiguredPackageSource source)
    {
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return Observable.Return(ImmutableDictionary<string, DiscoveredModule>.Empty);
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => storage.Read(ModuleDiscovery.PathFor(source.RepoPath), hub.JsonSerializerOptions))
            .Take(1)
            .Select(node => node?.ContentAs<ModuleDiscovery>(hub.JsonSerializerOptions, logger)?.Modules
                    is { } modules
                ? modules.ToImmutableDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase)
                : ImmutableDictionary<string, DiscoveredModule>.Empty)
            .Catch((Exception exception) =>
            {
                logger?.LogWarning(exception,
                    "[ModuleDiscovery] reading the previous discovery record for {Name} failed.",
                    source.Name);
                return Observable.Return(ImmutableDictionary<string, DiscoveredModule>.Empty);
            });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The scan itself
    // ══════════════════════════════════════════════════════════════════════════

    private IObservable<ModuleDiscovery> Reconcile(
        ConfiguredPackageSource source,
        string gitRef,
        IReadOnlyList<PackageManifest> modules,
        InstanceState state,
        ImmutableDictionary<string, DiscoveredModule> previous)
    {
        var now = DateTimeOffset.UtcNow;

        // 🚨 Dependency order, for the same reason the default install needs it: provisioning a
        // module before the one it declares fails outright ("NodeType(s) not registered"). An
        // unattended pass has to derive the order a human would pick implicitly.
        var ordered = PackageDependencyGraph.InDependencyOrder(modules, logger);

        var evaluated = ordered.Count == 0
            ? Observable.Return<IList<DiscoveredModule>>([])
            : ordered
                .Select(module => Evaluate(source, module, state, previous, now))
                // Concat, never Merge: each provisioning creates a partition and may compile node
                // types; a parallel fan-out onto a cold mesh is how writes into fresh partitions
                // crash each other.
                .ToObservable()
                .Concat()
                .ToList();

        return evaluated.SelectMany(entries =>
        {
            var listed = entries.Select(e => e.Id).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            var orphans = Orphans(source, state, previous, listed, now);
            var all = entries.Concat(orphans)
                .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .ToImmutableList();

            var record = new ModuleDiscovery
            {
                RepositoryUrl = source.RepoPath,
                SourceName = source.Name,
                GitRef = gitRef,
                AutoSync = source.AutoSync,
                LastScannedAt = now,
                Modules = all,
            };

            return WriteRecord(source, record)
                .SelectMany(recordPath => Announce(recordPath, all, previous).Select(_ => record));
        });
    }

    /// <summary>
    /// One module's verdict. The order of the tests IS the policy: something this instance already
    /// carries is never touched, absence is always REPORTED, and only an explicitly opted-in source
    /// with a genuinely free path may write anything.
    /// </summary>
    private IObservable<DiscoveredModule> Evaluate(
        ConfiguredPackageSource source,
        PackageManifest module,
        InstanceState state,
        ImmutableDictionary<string, DiscoveredModule> previous,
        DateTimeOffset now)
    {
        var spaceId = string.IsNullOrWhiteSpace(module.TargetPartition) ? module.Id : module.TargetPartition!;
        previous.TryGetValue(module.Id, out var prior);
        var seed = new DiscoveredModule
        {
            Id = module.Id,
            Name = module.Name,
            FirstSeenAt = prior?.FirstSeenAt ?? now,
            ProvisionedAt = prior?.ProvisionedAt,
        };

        // (1) Already synced — the production shape. Nothing to do, EXCEPT for a Space this scan
        //     itself provisioned whose first import never landed: re-running the import is the same
        //     reconcile reacting to an observed state, not a retry timer, and without it a module
        //     whose first import failed would sit permanently empty.
        if (state.SyncedPartitions.TryGetValue(spaceId, out var syncConfig))
        {
            var ours = string.Equals(
                syncConfig.RepositoryUrl?.TrimEnd('/'), source.RepoPath?.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
            if (ours && prior?.ProvisionedAt is not null
                && string.IsNullOrWhiteSpace(syncConfig.LastSyncCommitSha))
                return FirstImport(spaceId).Select(detail => seed with
                {
                    Status = ModuleDiscoveryStatus.Provisioned,
                    Detail = $"re-running the first import, which had not landed — {detail}",
                });
            return Observable.Return(seed with
            {
                Status = ModuleDiscoveryStatus.Synced,
                Detail = ours ? null : $"synced from {syncConfig.RepositoryUrl ?? "another source"}",
            });
        }

        // (2) The other way content arrives: a plugin-catalog install record.
        if (state.InstalledIds.Contains(module.Id))
            return Observable.Return(seed with
            {
                Status = ModuleDiscoveryStatus.Installed,
                Detail = "installed through the plugin catalog",
            });

        // (3) Not carried here. The occupancy probe runs in BOTH modes, not just before a write:
        //     "the module is absent but its name is taken" and "the module is simply absent" are
        //     different answers, and reporting accurately IS the product in discovery-only mode.
        return IsOccupied(spaceId).SelectMany(occupied =>
        {
            if (occupied)
                return Observable.Return(seed with
                {
                    Status = ModuleDiscoveryStatus.Occupied,
                    Detail = $"'{spaceId}' already exists here and carries no sync entry for this "
                             + "repo. It is left alone — wiring a _GitSync onto an existing Space "
                             + "makes the partition system-owned and retracts its owner's access.",
                });

            // Discovery ALWAYS reports absence; only AutoSync writes.
            if (!source.AutoSync)
                return Observable.Return(seed with
                {
                    Status = ModuleDiscoveryStatus.Discovered,
                    Detail = "not on this instance — turn on AutoSync for this source, or add it by hand",
                });

            if (string.IsNullOrWhiteSpace(source.RepoPath))
                return Observable.Return(seed with
                {
                    Status = ModuleDiscoveryStatus.Failed,
                    Detail = "AutoSync needs a repo URL to write into the sync entry, and this source "
                             + "has none (it is a DI-registered or remote-registry source).",
                });

            return Provision(source, module, spaceId, seed, now);
        });
    }

    /// <summary>
    /// Modules this instance SYNCS from this repo that the repo no longer ships. Reported, never
    /// deleted — auto-deleting would destroy content on the strength of a listing.
    /// </summary>
    private static IEnumerable<DiscoveredModule> Orphans(
        ConfiguredPackageSource source,
        InstanceState state,
        ImmutableDictionary<string, DiscoveredModule> previous,
        ImmutableHashSet<string> listed,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(source.RepoPath))
            yield break;
        var repo = source.RepoPath.TrimEnd('/');
        foreach (var (partition, config) in state.SyncedPartitions)
        {
            if (listed.Contains(partition))
                continue;
            if (!string.Equals(config.RepositoryUrl?.TrimEnd('/'), repo, StringComparison.OrdinalIgnoreCase))
                continue;
            previous.TryGetValue(partition, out var prior);
            yield return new DiscoveredModule
            {
                Id = partition,
                Name = prior?.Name,
                Status = ModuleDiscoveryStatus.Orphaned,
                Detail = "this instance syncs it from the repo, but the repo no longer ships it. "
                         + "Nothing was deleted — remove the Space by hand if that is what you want.",
                FirstSeenAt = prior?.FirstSeenAt ?? now,
                ProvisionedAt = prior?.ProvisionedAt,
            };
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Provisioning — Space + declared access + _GitSync, all as SYSTEM
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates everything a module needs to start syncing, under the System identity.
    ///
    /// <para><b>Order matters.</b> The Space root lands first (it provisions the partition), then the
    /// <c>_GitSync</c> entry — as early as possible, because its existence is what MARKS the
    /// partition system-owned, and every moment before it is a window in which an ordinary grant
    /// could be minted on repo-owned content. Then the module's declared access
    /// (<see cref="PackageInstaller.EnsureDeclaredAccess"/> — the same step the installer runs, and
    /// Viewer/Denied assignments are exactly what survives system-ownership), and finally the first
    /// import.</para>
    ///
    /// <para><b>Every write re-establishes System at its own subscribe.</b> An ambient impersonation
    /// does not survive a scheduler hop, and each of these primitives captures the AccessContext
    /// synchronously at its CALL — which happens inside a continuation. Same treatment
    /// <c>PackageInstaller</c> gives its writes.</para>
    /// </summary>
    private IObservable<DiscoveredModule> Provision(
        ConfiguredPackageSource source,
        PackageManifest module,
        string spaceId,
        DiscoveredModule seed,
        DateTimeOffset now)
    {
        var sync = hub.ServiceProvider.GetService<GitHubSyncService>();
        if (sync is null)
            return Observable.Return(seed with
            {
                Status = ModuleDiscoveryStatus.Failed,
                Detail = "AutoSync is on, but GitHub sync is not wired on this installation "
                         + "(AddGitHubSyncServices / AddGitHubSyncTypes) — the sync entry cannot be written.",
            });

        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var branch = BranchFor(source.GitRef);
        var subdirectory = Subdirectory(source.Subdir, module.SourceFolder ?? module.Id);

        var spaceNode = new MeshNode(spaceId)
        {
            NodeType = GitHubSyncService.SpaceNodeType,
            Name = module.Name ?? spaceId,
            State = MeshNodeState.Active,
            Content = new Space { Description = module.Description },
        };

        logger?.LogInformation(
            "[ModuleDiscovery] provisioning '{Space}' from {Repo}#{Subdir}@{Branch} as SYSTEM.",
            spaceId, source.RepoPath, subdirectory, branch);

        // 🚨 The free-vs-commercial gate (#830), on the ACTION. There is no authorizing principal on
        // a webhook or a boot, so a priced module is REFUSED here rather than quietly appearing.
        return PackageEntitlement.Authorize(hub, module, authorizingUserId: null, logger)
            // 🚨 The create is the ONE step that can fail because the path is already taken (it is
            // create-only). Losing that race — another replica, or an existing Space the probe could
            // not see — is NOT a failure to report as `Failed`: it is exactly the `Occupied` verdict,
            // and calling it anything else contradicts what the probe promises and hands the operator
            // a wrong, unactionable answer. Re-probe rather than sniff the message, so the mapping
            // holds whatever wording the create layer uses.
            .SelectMany(_ => AsSystem(() => meshService.CreateNode(spaceNode))
                .Catch((Exception createFailure) => IsOccupied(spaceId).SelectMany(occupied => occupied
                    ? Observable.Throw<MeshNode>(new ModulePathOccupiedException(spaceId, createFailure))
                    : Observable.Throw<MeshNode>(createFailure))))
            .SelectMany(_ => AsSystem(() => sync.SaveConfig(
                spaceId,
                source.RepoPath,
                branch,
                subdirectory,
                // 🚨 Never create a branch or a repository from an unattended scan. This instance is
                // a CONSUMER of the plugin repo; writing to it was authorized by nobody.
                createBranchIfMissing: false,
                createRepoIfMissing: false,
                // Import-only for the same reason: the repo is the source of truth for a module, and
                // an unattended export from every consuming instance back into a shared plugin repo
                // is not something a scan may decide. An admin can widen it in the sync settings.
                direction: SyncDirection.ImportOnly)))
            .SelectMany(_ => AsSystem(() => PackageInstaller.EnsureDeclaredAccess(
                hub, module, spaceId, logger)))
            .SelectMany(_ => FirstImport(spaceId))
            .Select(detail => seed with
            {
                Status = ModuleDiscoveryStatus.Provisioned,
                ProvisionedAt = now,
                Detail = detail,
            })
            .Catch((Exception exception) =>
            {
                if (exception is PackageAuthorizationException)
                {
                    logger?.LogWarning(
                        "[ModuleDiscovery] '{Space}' REFUSED — {Reason}", spaceId, exception.Message);
                    return Observable.Return(seed with
                    {
                        Status = ModuleDiscoveryStatus.Refused,
                        Detail = exception.Message,
                    });
                }
                if (exception is ModulePathOccupiedException)
                {
                    logger?.LogInformation(
                        "[ModuleDiscovery] '{Space}' is already taken — reporting it as occupied and "
                        + "leaving it alone.", spaceId);
                    return Observable.Return(seed with
                    {
                        Status = ModuleDiscoveryStatus.Occupied,
                        Detail = $"'{spaceId}' already exists here and carries no sync entry for this "
                                 + "repo. It is left alone — wiring a _GitSync onto an existing Space "
                                 + "makes the partition system-owned and retracts its owner's access.",
                    });
                }
                logger?.LogError(exception,
                    "[ModuleDiscovery] provisioning '{Space}' failed; the other modules continue.", spaceId);
                return Observable.Return(seed with
                {
                    Status = ModuleDiscoveryStatus.Failed,
                    Detail = exception.Message,
                });
            });
    }

    /// <summary>
    /// Runs the module's first import as the standard <b>Activity</b> — the identical operation the
    /// "Update to latest" button triggers, so its progress, its per-file problems and its terminal
    /// status land on the Space's activity log where a human already looks. Never faults: the
    /// activity IS the failure record, and a Space that exists with a sync entry but no content yet
    /// is recoverable (the next green build re-runs the import), whereas a faulted provisioning
    /// would leave nothing recorded at all.
    /// </summary>
    private IObservable<string> FirstImport(string spaceId) =>
        // System both as the trigger identity (TriggerAuthorizedAsSystem short-circuits an ambient
        // System caller) and as the GitHub identity (ResolveAuth falls through to the App
        // installation token — the machine identity server-side syncs already use).
        AsSystem(() => hub.UpdateToLatestFromGitHub(spaceId, WellKnownUsers.System))
            .Take(1)
            .Select(activityPath => $"first import ran ({activityPath})")
            .Catch((Exception exception) =>
            {
                logger?.LogWarning(exception,
                    "[ModuleDiscovery] the first import of '{Space}' could not run; the Space and its "
                    + "sync entry are in place and the next green build retries it.", spaceId);
                return Observable.Return($"first import could not run: {exception.Message}");
            });

    /// <summary>The branch a sync entry commits against. A ref of <c>HEAD</c> (the catalog default,
    /// which means "whatever the registry serves") is not a branch name — a sync entry needs a real
    /// one, and <c>main</c> is the repo default.</summary>
    private static string BranchFor(string? gitRef) =>
        string.IsNullOrWhiteSpace(gitRef) || string.Equals(gitRef, "HEAD", StringComparison.OrdinalIgnoreCase)
            ? "main"
            : gitRef.Trim();

    /// <summary>The module's folder inside the repo: the source's configured prefix plus the module's
    /// own folder.</summary>
    private static string Subdirectory(string? sourceSubdir, string moduleFolder)
    {
        var prefix = (sourceSubdir ?? "").Trim().Trim('/');
        var folder = (moduleFolder ?? "").Trim().Trim('/');
        return prefix.Length == 0 ? folder : $"{prefix}/{folder}";
    }

    /// <summary>Establishes the System identity on the write's OWN subscribe thread — the only place
    /// it is reliably in scope when the primitive captures the AccessContext at its call.
    ///
    /// <para><c>GetRequiredService</c>, never <c>GetService</c>: a missing <c>AccessService</c> would
    /// silently run every provisioning write under the AMBIENT identity — i.e. as whoever's session
    /// triggered the scan — which is precisely the "creator becomes Admin on a repo-owned partition"
    /// failure this whole service exists to prevent. Failing to resolve must be a startup error, not
    /// a silent downgrade.</para></summary>
    private IObservable<T> AsSystem<T>(Func<IObservable<T>> write)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(() => accessService.ImpersonateAsSystem(), _ => write());
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Making it visible
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Writes (or rewrites) the source's discovery record and returns its path.</summary>
    private IObservable<string> WriteRecord(ConfiguredPackageSource source, ModuleDiscovery record)
    {
        var path = ModuleDiscovery.PathFor(source.RepoPath);
        var slash = path.LastIndexOf('/');
        var node = new MeshNode(path[(slash + 1)..], path[..slash])
        {
            NodeType = ModuleDiscovery.NodeType,
            Name = $"Modules of {source.Name}",
            State = MeshNodeState.Active,
            Content = record,
        };
        return AsSystem(() => hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node)))
            .FirstAsync()
            .Select(delivery => delivery.Message)
            .SelectMany(response => response.Success
                ? Observable.Return(path)
                : Observable.Throw<string>(new InvalidOperationException(
                    $"Could not write the module discovery record at '{path}': {response.Error}")));
    }

    /// <summary>
    /// Raises a notification for every module whose verdict CHANGED since the last scan — a new
    /// module appearing un-provisioned, a refusal, a failure, an orphan, and a successful
    /// provisioning too. Only on change, which is what makes a re-scan a genuine no-op instead of a
    /// notification storm.
    /// </summary>
    private IObservable<Unit> Announce(
        string recordPath,
        ImmutableList<DiscoveredModule> modules,
        ImmutableDictionary<string, DiscoveredModule> previous)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        var announcements = modules
            .Where(m => m.Status is not (ModuleDiscoveryStatus.Synced or ModuleDiscoveryStatus.Installed))
            .Where(m => !previous.TryGetValue(m.Id, out var prior) || prior.Status != m.Status)
            .ToList();
        if (announcements.Count == 0)
            return Observable.Return(Unit.Default);

        return announcements
            // As SYSTEM, like every other write in a scan: there is no user on a boot or a webhook
            // emission, and the Admin-partition record is not writable by anyone else.
            .Select(module => AsSystem(() => NotificationService.CreateNotification(
                    meshService, recordPath,
                    accessService.Localize($"plugins.discovery.{Key(module.Status)}.title", module.Name ?? module.Id),
                    accessService.Localize($"plugins.discovery.{Key(module.Status)}.body",
                        module.Name ?? module.Id, module.Detail ?? ""),
                    NotificationType.System, targetNodePath: recordPath))
                .Take(1)
                .Select(_ => Unit.Default)
                .Catch((Exception exception) =>
                {
                    logger?.LogWarning(exception,
                        "[ModuleDiscovery] raising the notification for '{Id}' failed.", module.Id);
                    return Observable.Return(Unit.Default);
                }))
            .ToObservable()
            .Concat()
            .TakeLast(1);
    }

    private static string Key(ModuleDiscoveryStatus status) => status switch
    {
        ModuleDiscoveryStatus.Provisioned => "provisioned",
        ModuleDiscoveryStatus.Refused => "refused",
        ModuleDiscoveryStatus.Occupied => "occupied",
        ModuleDiscoveryStatus.Orphaned => "orphaned",
        ModuleDiscoveryStatus.Failed => "failed",
        _ => "discovered",
    };

    private static string Summarize(ModuleDiscovery record) => string.Join(", ", record.Modules
        .GroupBy(m => m.Status)
        .OrderBy(g => g.Key)
        .Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"));

    /// <summary>
    /// The module's Space path is already taken, discovered when the create-only
    /// <c>CreateNode</c> lost to whatever is there. Internal signal, never surfaced as an error: the
    /// catch turns it into the <see cref="ModuleDiscoveryStatus.Occupied"/> verdict, which is an
    /// accurate answer rather than a failure. Carries the create's own exception as
    /// <see cref="Exception.InnerException"/> so the underlying cause is never lost.
    /// </summary>
    private sealed class ModulePathOccupiedException(string spacePath, Exception cause)
        : InvalidOperationException($"'{spacePath}' already exists — it was not adopted.", cause);

    /// <summary>One queued scan.</summary>
    private sealed record ScanRequest(ConfiguredPackageSource Source, string GitRef);

    /// <summary>What this instance already carries, read once per scan.</summary>
    /// <param name="InstalledIds">Plugin-catalog install record ids.</param>
    /// <param name="SyncedPartitions">Partition → its GitHub sync config.</param>
    private sealed record InstanceState(
        ImmutableHashSet<string> InstalledIds,
        ImmutableDictionary<string, GitHubSyncConfig> SyncedPartitions);
}
