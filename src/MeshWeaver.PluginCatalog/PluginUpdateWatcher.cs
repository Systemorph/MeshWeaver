using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Turns "a plugin repo's CI went green" into "the instances that installed from it find out",
/// without anyone polling a registry or opening the catalog page.
///
/// <para><b>How the two halves meet.</b> <c>MeshWeaver.GitSync</c> writes a
/// <see cref="BuildCompletion"/> node at <c>Admin/_Build/{owner}.{repo}</c> when a
/// <c>workflow_run</c> completes successfully. This watcher SUBSCRIBES to that node's stream. There
/// is no call between the two projects and no interface either implements — the node is the entire
/// contract, so the producer knows nothing about plugins and this consumer knows nothing about
/// webhooks. (Its content type lives in <c>MeshWeaver.Graph</c>, which both already reference; a
/// handler interface in GitSync could not have worked, since PluginCatalog already depends on
/// GitSync and implementing it here would close a reference cycle.)</para>
///
/// <para><b>Which repositories are watched.</b> Every package source this installation installs
/// from: a <c>PluginCatalog</c> node naming a source repository, AND a source configured under
/// <c>PluginCatalog:Sources:N:RepoPath</c> (how a deployment mounts a GitHub repository as a package
/// source). Both are reconciled the same way, keyed by <see cref="InstallOrigin"/> so a repository
/// declared both ways reconciles once. A half-hourly safety net re-reads every watched build node, so
/// a lost reaction self-heals.</para>
///
/// <para><b>A green build is NOT a change.</b> The build node is rewritten on every green run —
/// doc-only commits, reverts, re-runs of an unchanged tree. Acting on the emission itself would
/// push an update to every instance on every green run. The decision is made per MODULE against
/// content identity, and it is deliberately NOT re-implemented here:
/// <see cref="CatalogLayoutAreas.InstallOrUpdate"/> already compares the catalog entry's
/// <c>ModuleVersion</c> (a hash over the module's sorted (path, file-hash) pairs, from its
/// CI-maintained <c>manifest.lock</c>) with the install record's, and no-ops when they are equal.
/// Keeping that comparison in ONE place is what stops the two paths — the Update button and this
/// watcher — from ever disagreeing about what "changed" means.</para>
///
/// <para><b>Reminder by default; unattended on opt-in — per record, seeded per deployment.</b> A
/// changed module raises a <c>Notification</c> satellite on the install record unless the record
/// opted in (<see cref="PackageManifest.AutoUpdate"/>), in which case the delta installs itself.
/// The opt-in is stamped at INSTALL time from the deployment's
/// <see cref="PluginCatalogOptions.AutoUpdateByDefault"/> — our deployments opt in via the Helm
/// chart, so every package they install auto-updates with no per-package step — and an opted-in
/// update is still fenced by the content-identity gate above, the additive install, and the
/// per-node <c>SyncBehavior</c> claim the installer honors. See
/// <see cref="PackageManifest.AutoUpdate"/> for the full policy.</para>
///
/// <para>Instance-scoped, not static: the subscriptions live and die with the mesh
/// (see <c>Doc/Architecture/NoStaticState</c>). Reactive throughout — no <c>async</c>/<c>await</c>,
/// no polling.</para>
///
/// <para>Full flow and configuration: <c>Doc/Architecture/PluginUpdateOnGreenBuild</c>.</para>
/// </summary>
public sealed class PluginUpdateWatcher : Microsoft.Extensions.Hosting.IHostedService, IDisposable
{
    private readonly IMessageHub hub;
    private readonly ILogger? logger;
    private readonly CompositeDisposable subscriptions = new();

    /// <summary>
    /// Every watched repository (and subdirectory) by its <see cref="InstallOrigin"/> — the ONE
    /// registry of what this watcher reconciles. It is at once the dedupe (a repository declared by
    /// a <c>PluginCatalog</c> node AND configured under <c>PluginCatalog:Sources</c> is watched,
    /// and reconciled, once per green build) and the census an install record that names no origin
    /// is decided against (MeshWeaver#6526). Instance state — one per mesh.
    /// </summary>
    private ImmutableDictionary<string, WatchedPackageSource> watchedOrigins =
        ImmutableDictionary<string, WatchedPackageSource>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>The head sha each build node last evicted the listing cache for, so two origins on
    /// one repository evict once per green build. Instance state — one per mesh.</summary>
    private readonly ConcurrentDictionary<string, string> lastEvicted = new(StringComparer.Ordinal);

    /// <summary>
    /// The ONE serialized lane every reconcile pass runs on — a green build of any watched origin
    /// and every safety-net pass. An unattended apply writes a partition's worth of nodes, and two
    /// passes over the same install record (a green build landing while the safety net reads the
    /// same build) must never interleave. <c>Subject.Synchronize</c> guards the hand-off from the
    /// build streams and the timer; <c>Concat</c> runs one pass at a time. The shape
    /// <see cref="RegistryUpdateReconciler"/>'s lane and <see cref="ModuleDiscoveryService"/>'s scan
    /// queue already use.
    /// </summary>
    private readonly ISubject<IObservable<Unit>> lane = Subject.Synchronize(new Subject<IObservable<Unit>>());

    /// <summary>1 once the safety net is armed, so a second <see cref="Start"/> arms no second timer.</summary>
    private int safetyNetArmed;

    /// <summary>Initializes a new instance of the <see cref="PluginUpdateWatcher"/> class.</summary>
    public PluginUpdateWatcher(IMessageHub hub, ILogger<PluginUpdateWatcher>? logger = null)
    {
        this.hub = hub;
        this.logger = logger;
        subscriptions.Add(lane.Concat().Subscribe(
            _ => { },
            ex => logger?.LogError(ex,
                "Plugin update watcher: the reconcile lane faulted — no further green build is reconciled "
                + "in this process until it restarts.")));
    }

    /// <summary>
    /// Hosted-service start — what actually activates the watcher on boot. Registered as an
    /// <c>IHostedService</c> in <c>AddPluginCatalog</c> (a bare singleton would sit inert: the host
    /// only starts services registered under the interface — the Copilot-caught gap that left the
    /// build-node subscription never opened). Delegates to <see cref="Start"/>; returns immediately
    /// (the subscription chain is reactive — nothing to await).
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <summary>Hosted-service stop: closes every subscription (idempotent with <see cref="Dispose"/>).</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens a build-node subscription for every package source this installation installs from:
    /// every <c>PluginCatalog</c> node that names a source repository, AND every source configured
    /// under <c>PluginCatalog:Sources</c>. Idempotent: calling it again subscribes only origins not
    /// yet watched. (A catalog CREATED after boot is picked up on the next start — the snapshot read
    /// is deliberate; the per-origin guard makes re-running free.)
    /// </summary>
    public void Start()
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();

        // Catalogs are read as SYSTEM: this runs on mesh startup with no ambient user, and a
        // catalog node is not anonymous-readable on an access-gated portal.
        // RunAsSystem, never `Observable.Using(() => ImpersonateAsSystem(), …)` — store and restore
        // of the identity must land on the same thread (AGENTS.md; #1790).
        var sub = accessService.RunAsSystem(() => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery(
                    // A catalog node is authored wherever its registry lives — mesh-wide by
                    // nature (#3202 — fan-out is opt-in).
                    MeshWideQuery.OfType(PluginCatalogConfigurationExtensions.CatalogNodeType)))
                .Take(1))
            .Subscribe(
                change =>
                {
                    foreach (var node in change.Items)
                        WatchCatalog(node);
                },
                ex => logger?.LogWarning(ex, "Plugin update watcher could not read the catalog set."));
        subscriptions.Add(sub);

        WatchConfiguredSources();
        ArmSafetyNet();
    }

    /// <summary>
    /// 🚨 The sources an installation actually INSTALLS from are configured, not only node-declared
    /// — <c>PluginCatalog:Sources:N:RepoPath</c>, which is also how a deployment mounts a GitHub
    /// repository as a package source (the fleet registry, and an instance's own
    /// <c>pluginRepos</c>). Until this read reconciled, it only evicted the listing cache (#4222):
    /// a package installed from a configured GitHub source had no update path at all — the build
    /// fact arrived, the cache forgot the listing, and the install record stayed on its old commit
    /// indefinitely, neither on the green build nor on any safety net (measured on an enterprise
    /// client's test instance, 2026-10-13/14: 14+ hours behind with the build fact present).
    ///
    /// <para>Now a configured URL source is watched exactly like a catalog node: on its green
    /// build the listing cache is evicted AND its installed packages are reconciled through the
    /// same <see cref="PackageUpdateReconciler"/> pass, bound to the source's origin. Read through
    /// <c>PackageSources.FromConfiguration</c> rather than re-parsing the section, so this can never
    /// disagree with what the endpoint serves and the default install installs.</para>
    /// </summary>
    private void WatchConfiguredSources()
    {
        var config = hub.ServiceProvider.GetService<IConfiguration>();
        if (config is null)
            return;

        // 🚨 This runs inside IHostedService.StartAsync, and reading the configured sources
        // CONSTRUCTS them — which resolves services and can throw on a mesh composed differently
        // from the registry's. A watch that can abort a boot is far worse than the update it
        // delivers: the fault is reported and the host carries on (the catalog page still offers a
        // manual Update). Same rule, and the same reason, as PlatformMetricsHostedService.
        IReadOnlyList<ConfiguredPackageSource> sources;
        try
        {
            sources = PackageSources.FromConfiguration(hub, config, logger);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception,
                "Plugin update watcher: the configured package sources could not be read, so a green "
                + "build of a configured source neither reconciles its installed packages nor "
                + "invalidates the listing cache early in this process.");
            return;
        }

        foreach (var configured in sources)
        {
            if (configured.RepoPath is not { Length: > 0 } src)
                continue;
            Watch(src, configured.Subdir, configured.Source, configured.GitRef,
                $"configured source {configured.Name}");
        }
    }

    private void WatchCatalog(MeshNode catalogNode)
    {
        var content = catalogNode.ContentAs<PluginCatalogContent>(hub.JsonSerializerOptions, logger);
        if (content?.SourceRepoPath is not { Length: > 0 } src)
            return;

        // The catalog node declares its own format; before #3384 this read `nodeRepo: true`
        // unconditionally while the browse view read the same record as package.json.
        var source = PackageSources.FromRepo(
            hub, src, content.SourceSubdir, logger,
            nodeRepo: PackageSources.IsNodeRepoFormatOrDetected(content.Format, src, content.SourceSubdir));
        if (source is null)
        {
            logger?.LogWarning(
                "Plugin update watcher: catalog {Catalog} has no usable package source; not watched.",
                catalogNode.Path);
            return;
        }
        Watch(src, content.SourceSubdir, source, content.SourceRef, $"catalog {catalogNode.Path}");
    }

    /// <summary>
    /// Watches one origin's build node: the shared tail of the catalog-node and configured-source
    /// paths, so the two cannot drift apart. A second declaration of an origin already watched —
    /// the same repository and subdirectory declared both ways — is a no-op: it reconciles once.
    /// </summary>
    private void Watch(string src, string? subdir, IPackageSource source, string? gitRef, string label)
    {
        // A source may be a local path rather than a repo URL; only a URL can ever have a build
        // node, because only a URL receives webhooks.
        (string Owner, string Repo) parsed;
        try { parsed = OctokitParse(src); }
        catch { return; }
        if (parsed.Owner.Length == 0 || parsed.Repo.Length == 0)
            return;
        if (InstallOrigin.ForRepo(src, subdir) is not { } origin)
            return;

        var buildPath = BuildCompletion.PathFor(parsed.Owner, parsed.Repo);
        var target = new WatchedPackageSource(
            origin, buildPath, src, source, string.IsNullOrWhiteSpace(gitRef) ? "HEAD" : gitRef, label);
        if (!ImmutableInterlocked.TryAdd(ref watchedOrigins, origin, target))
            return; // already watched (and reconciled) under another declaration of the same origin

        logger?.LogInformation(
            "Plugin update watcher: {Label} watches {BuildPath} for green builds of {Origin} — to reconcile "
            + "its installed packages and invalidate the listing cache.",
            label, buildPath, origin);

        var sub = Builds(buildPath)
            // A rebuild of the SAME commit tells us nothing new. This is a cheap guard against
            // re-emission; it is NOT the "did anything change" decision — that is per module,
            // against ModuleVersion, and lives in InstallOrUpdate.
            .DistinctUntilChanged(b => b.HeadSha)
            .Subscribe(
                build => OnGreenBuild(target, build),
                ex => logger?.LogWarning(ex,
                    "Plugin update watcher: build stream for {BuildPath} faulted; the safety net still "
                    + "reads it every interval.", buildPath));
        subscriptions.Add(sub);
    }

    /// <summary>
    /// The green builds recorded at <paramref name="buildPath"/>, read off the node's own stream —
    /// the canonical single-node read: one shared handle per path, live, authoritative (never
    /// QueryAsync — that index lags a write by design).
    ///
    /// <para>🚨 <b>A build node that does not exist YET is the normal first state, not a fault.</b>
    /// A repository whose content CI has never been recorded has no node, and the node stream then
    /// FAULTS ("No node found at 'Admin/_Build/…'"). Before, that fault ended the watch for the life
    /// of the process: measured on an enterprise client's test instance (2026-10-13), the
    /// repository's first green build was recorded hours after boot and the watcher never saw it.
    /// So a faulted stream waits for the node to APPEAR — on the path-scoped live query over the
    /// build records (<see cref="BuildCompletion.WatchQuery"/>, a wake-up only) — and then re-opens
    /// the node's own stream. A fault on a node that does exist re-opens after a pause rather than
    /// in a tight loop.</para>
    /// </summary>
    private IObservable<BuildCompletion> Builds(string buildPath)
        => hub.GetMeshNodeStream(buildPath)
            .Select(n => n?.ContentAs<BuildCompletion>(hub.JsonSerializerOptions, logger))
            .Where(b => b is not null && b.HeadSha.Length > 0)
            .Select(b => b!)
            .Catch((Exception ex) =>
            {
                logger?.LogInformation(
                    "Plugin update watcher: the build stream for {BuildPath} ended ({Reason}) — waiting for the "
                    + "build record to appear, then watching it again.", buildPath, ex.Message);
                return BuildRecordPresent(buildPath)
                    .Take(1)
                    .SelectMany(_ => Observable.Timer(RearmPause))
                    .SelectMany(_ => Builds(buildPath));
            });

    /// <summary>A pause before re-opening a build stream that faulted, so a node that exists but
    /// keeps faulting is retried at a bounded rate rather than in a tight loop.</summary>
    private static readonly TimeSpan RearmPause = TimeSpan.FromSeconds(2);

    /// <summary>Emits once the build record at <paramref name="buildPath"/> is present — read on
    /// the live, path-scoped query over every build record, as System (a hosted service has no
    /// caller, and the Admin partition is not anonymous-readable).</summary>
    private IObservable<Unit> BuildRecordPresent(string buildPath)
        => hub.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => hub.GetQuery($"{nameof(PluginUpdateWatcher)}.Builds", BuildCompletion.WatchQuery))
            .Where(nodes => (nodes ?? []).Any(n => string.Equals(n.Path, buildPath, StringComparison.Ordinal)))
            .Select(_ => Unit.Default);

    private void OnGreenBuild(WatchedPackageSource target, BuildCompletion build)
    {
        logger?.LogInformation(
            "Plugin update watcher: {Repo} built green at {Sha} ({Workflow} #{Run}) — checking the packages "
            + "installed from {Origin}.",
            build.RepositoryUrl, build.HeadSha, build.WorkflowName, build.RunNumber, target.Origin);

        // 🚨 The listing cache's PRIMARY invalidation (#4222). This is the moment the registry
        // learns the source repository changed — the webhook it already receives — so forget every
        // listing of it here rather than letting the freshness window run out. The window is the
        // safety net for a broadcast that never arrived, not the mechanism. Once per build node and
        // sha: two origins on one repository share it.
        if (!(lastEvicted.TryGetValue(target.BuildPath, out var evictedAt)
              && string.Equals(evictedAt, build.HeadSha, StringComparison.Ordinal)))
        {
            lastEvicted[target.BuildPath] = build.HeadSha;
            hub.ServiceProvider.GetService<PackageListingCache>()
                ?.EvictRepo(build.RepositoryUrl.Length > 0 ? build.RepositoryUrl : target.RepoPath);
        }

        Enqueue(() => Reconcile(target, build));
    }

    /// <summary>
    /// 🚨 The safety net (default every <see cref="PluginCatalogOptions.ReconcileSafetyNetInterval"/>,
    /// 30 min; ≤ 0 disables): a full pass over every watched origin against its build node AS
    /// STORED. The live subscription delivers a build once — a pass that failed (an unreachable
    /// repository, a transient install fault), a build stream that faulted because the node did not
    /// exist yet at boot, or a pod that was down when the node was written all leave an install
    /// record behind with nothing that would ever look again. The durable build fact is re-read
    /// here and the same reconcile runs at its sha; the content-identity gate makes a pass with
    /// nothing to do silent and fetch-free beyond the (cached) listing. The same role, and the same
    /// cadence, as <see cref="RegistryUpdateReconciler"/>'s safety net — and, like it, never a poll
    /// that drives updates: the green build does that.
    /// </summary>
    private void ArmSafetyNet()
    {
        if (Interlocked.Exchange(ref safetyNetArmed, 1) == 1)
            return;
        var interval = (hub.ServiceProvider.GetService<PluginCatalogOptions>() ?? new PluginCatalogOptions())
            .ReconcileSafetyNetInterval;
        if (interval <= TimeSpan.Zero)
        {
            logger?.LogInformation(
                "Plugin update watcher: the safety net is disabled ({Section}:{Key} ≤ 0) — a lost green-build "
                + "reaction is caught at the next boot only.",
                PluginCatalogOptions.SectionName, nameof(PluginCatalogOptions.ReconcileSafetyNetInterval));
            return;
        }
        // ReconcileNow only enqueues (each pass reports its own fault and never faults the lane), so
        // nothing reaches the error arm short of the timer itself failing.
        subscriptions.Add(Observable.Interval(interval)
            .Subscribe(
                _ => ReconcileNow(),
                ex => logger?.LogError(ex,
                    "Plugin update watcher: the safety-net timer faulted — no further safety-net pass runs "
                    + "in this process; green builds still reconcile.")));
    }

    /// <summary>
    /// ONE safety-net pass over every watched origin, NOW, on the serialized lane — what each
    /// safety-net tick does. Completes when the pass has ended; never faults (an origin whose build
    /// node or listing cannot be read is logged and skipped; the next pass asks again).
    /// </summary>
    internal IObservable<Unit> ReconcileNow() => Enqueue(SafetyNetPass);

    private IObservable<Unit> SafetyNetPass()
    {
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return Observable.Return(Unit.Default);
        return watchedOrigins.Values
            .Select(target => storage.Read(target.BuildPath, hub.JsonSerializerOptions)
                .Take(1)
                .DefaultIfEmpty(null)
                .Select(n => n?.ContentAs<BuildCompletion>(hub.JsonSerializerOptions, logger))
                .SelectMany(build => build is { HeadSha.Length: > 0 }
                    ? Reconcile(target, build)
                    : Observable.Return(Unit.Default))
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex,
                        "Plugin update watcher: the safety-net pass could not read {BuildPath} for {Origin}; "
                        + "the next pass asks again.", target.BuildPath, target.Origin);
                    return Observable.Return(Unit.Default);
                }))
            .ToObservable()
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .LastAsync();
    }

    /// <summary>
    /// Enqueues one pass on the serialized lane and hands back ITS completion. A pass that faults
    /// is reported here and never faults the lane; a pass enqueued during teardown completes at
    /// once, having run nothing.
    /// </summary>
    private IObservable<Unit> Enqueue(Func<IObservable<Unit>> pass)
    {
        var done = new AsyncSubject<Unit>();
        if (subscriptions.IsDisposed)
        {
            done.OnNext(Unit.Default);
            done.OnCompleted();
            return done;
        }
        lane.OnNext(Observable.Defer(pass)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Plugin update watcher: a reconcile pass failed.");
                return Observable.Return(Unit.Default);
            })
            .DefaultIfEmpty(Unit.Default)
            .LastAsync()
            .Do(_ => { }, () =>
            {
                done.OnNext(Unit.Default);
                done.OnCompleted();
            }));
        return done;
    }

    /// <summary>One origin's reconcile at one green build: list the source at the build's sha and
    /// hand the listing to <see cref="ReconcileInstalled"/>. Never faults.</summary>
    private IObservable<Unit> Reconcile(WatchedPackageSource target, BuildCompletion build)
        => target.Source.ListPackages(build.HeadSha)
            .Take(1)
            .SelectMany(packages => ReconcileInstalled(target.Source, packages, build))
            .DefaultIfEmpty(Unit.Default)
            .LastAsync()
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Plugin update watcher: reconciling the packages installed from {Origin} at {Sha} failed "
                    + "({Label}); the safety net retries.",
                    target.Origin, build.HeadSha, target.Label);
                return Observable.Return(Unit.Default);
            });

    /// <summary>
    /// Hands the per-module decision to <see cref="PackageUpdateReconciler"/> — the SAME code the
    /// consumer-side <see cref="RegistryUpdateReconciler"/> runs. The two paths differ only in how
    /// they learned that something might have changed (a GitHub webhook here, a read of the
    /// registry's feed there); what counts as a change, and who gets an unattended install rather
    /// than a reminder, is one decision in one place (#1318).
    /// </summary>
    private IObservable<Unit> ReconcileInstalled(
        IPackageSource source, IReadOnlyList<PackageManifest> packages, BuildCompletion build)
        => PackageUpdateReconciler.ReconcileInstalled(
                // 🚨 A manifest listed straight off a watched repository carries whatever `source`
                // its package.json AUTHORS — never a provenance anybody verified. It is stripped here,
                // so no authorization that binds on Source/Package (the deployment configuration's,
                // policy deployment-config-authorizes-named-commercial) can be claimed by a repository
                // writing the configured source's name. The install record keeps the source it was
                // stamped with (PackageInstaller.SeedSource carries it forward). An authored `origin`
                // is stripped for the same reason; the binding below names the real one.
                hub, source, build.HeadSha,
                packages.Select(p => p with { Source = null, Origin = null }).ToImmutableList(),
                $"Built from {build.HeadSha[..Math.Min(7, build.HeadSha.Length)]}", logger,
                // 🚨 BOUND to the repository each install came from (MeshWeaver#6526): a second
                // watched repository listing the same id never updates a package it did not install.
                BindingFor(source));

    /// <summary>
    /// The opt-in rule, kept here so it stays pinnable on its own: a changed module installs
    /// unattended only for a record that opted in — by its own flag, stamped at install time from
    /// the deployment default (<see cref="PackageInstaller.SeedAutoUpdate"/>). Pure, and the rule
    /// <see cref="PackageUpdateReconciler"/> applies for BOTH the webhook-driven and the
    /// registry-driven path.
    /// </summary>
    // Internal for the BuildCompletionSubscriptionTest pin (InternalsVisibleTo).
    internal static bool ShouldAutoApply(PackageManifest record) =>
        record.EffectiveUpdatePolicy == PackageUpdatePolicy.Auto;

    /// <summary>
    /// The origin binding for a pass over <paramref name="source"/>'s listing: its origin, and a
    /// census of which OTHER watched repositories list a package id — read only for an install
    /// record that names no origin yet. Null when the source carries no origin (one not built by
    /// <see cref="PackageSources.FromRepo"/>), which leaves the pass unbound as before.
    /// </summary>
    private InstallOriginBinding? BindingFor(IPackageSource source)
        => InstallOrigin.Of(source) is { } origin
            ? new InstallOriginBinding(origin, id => Census(origin, id))
            : null;

    /// <summary>Which watched repositories other than <paramref name="self"/> list
    /// <paramref name="packageId"/>, read through each watched source at its own ref (the
    /// listing cache makes a repeat free). A repository that cannot be listed is reported UNREAD,
    /// never as "does not list it" — an adoption over a census with a hole could adopt the
    /// impostor.</summary>
    internal IObservable<OriginCensus> Census(string self, string packageId)
        => Census(watchedOrigins, self, packageId, logger);

    /// <summary>The census over an explicit set of watched sources (pinnable without a webhook).</summary>
    internal static IObservable<OriginCensus> Census(
        IReadOnlyDictionary<string, WatchedPackageSource> watched, string self,
        string packageId, ILogger? logger)
        => watched
            .Where(kv => !string.Equals(kv.Key, self, StringComparison.Ordinal))
            .Select(kv => ListsPackage(kv.Value, packageId, logger)
                .Select(lists => (Origin: kv.Key, Lists: lists)))
            .ToObservable()
            .Concat()
            .Aggregate(
                new OriginCensus(ImmutableSortedSet<string>.Empty, ImmutableSortedSet<string>.Empty),
                (census, read) => read.Lists switch
                {
                    true => census with { Listing = census.Listing.Add(read.Origin) },
                    false => census,
                    null => census with { Unread = census.Unread.Add(read.Origin) },
                });

    /// <summary>Whether one watched source lists <paramref name="packageId"/>; null when it could
    /// not be read.</summary>
    private static IObservable<bool?> ListsPackage(
        WatchedPackageSource watched, string packageId, ILogger? logger)
        => watched.Source.ListPackages(watched.GitRef)
            .Take(1)
            .Select(listing => (bool?)listing.Any(p => string.Equals(p.Id, packageId, StringComparison.Ordinal)))
            .DefaultIfEmpty(null)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Plugin update watcher: listing {Repo} for the origin census of {Id} failed.",
                    watched.RepoPath, packageId);
                return Observable.Return<bool?>(null);
            });

    private static (string Owner, string Repo) OctokitParse(string url)
    {
        var trimmed = url.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        if (slash <= 0) return ("", "");
        var repo = trimmed[(slash + 1)..];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];
        var rest = trimmed[..slash];
        var prevSlash = rest.LastIndexOf('/');
        var owner = prevSlash >= 0 ? rest[(prevSlash + 1)..] : rest;
        return (owner, repo);
    }

    /// <inheritdoc />
    public void Dispose() => subscriptions.Dispose();
}

/// <summary>
/// One origin <see cref="PluginUpdateWatcher"/> reconciles on a green build — declared by a
/// <c>PluginCatalog</c> node or configured under <c>PluginCatalog:Sources</c>; the watcher does not
/// care which.
/// </summary>
/// <param name="Origin">The canonical origin (<see cref="InstallOrigin.ForRepo"/>).</param>
/// <param name="BuildPath">The repository's <see cref="BuildCompletion"/> node path.</param>
/// <param name="RepoPath">The repository URL as declared.</param>
/// <param name="Source">The package source, carrying <paramref name="Origin"/>.</param>
/// <param name="GitRef">The ref the source is declared at — what the origin census lists.</param>
/// <param name="Label">Who declared it, for logs.</param>
internal sealed record WatchedPackageSource(
    string Origin, string BuildPath, string RepoPath, IPackageSource Source, string GitRef, string Label);
