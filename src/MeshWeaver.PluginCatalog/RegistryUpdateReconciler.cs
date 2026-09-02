using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// How a REGISTRY-ONLY installation learns that an installed module changed — by READING the
/// registry's own package feed, which is a fact it can obtain, instead of waiting for a
/// notification that can never reach it (Systemorph/MeshWeaver#1318).
///
/// <para><b>What was broken.</b> The only mechanism that turned "a module changed" into "the
/// instances that installed it find out" was <see cref="PluginUpdateWatcher"/>, which subscribes to
/// a <c>BuildCompletion</c> node at <c>Admin/_Build/{owner}.{repo}</c>. Across the whole tree that
/// node is constructed in exactly ONE place — <c>GitHubWebhookProcessor</c>, handling a GitHub
/// <c>workflow_run</c> webhook. A consumer that installs over HTTP with an instance token holds no
/// GitHub credential, receives no webhooks, and has no catalog node naming a source repo, so the
/// watcher opened zero subscriptions: registered, live, and completely inert. Auto-update was
/// therefore dead end to end on such an installation — <see cref="PackageManifest.AutoUpdate"/> was
/// stamped at install time and nothing ever fired it, which is why a package left at an old version
/// needed a human to click Provision.</para>
///
/// <para><b>Read the witness, do not wait to be told.</b> There is no change feed that could carry
/// the registry's commits here: the <c>PostgreSqlChangeListener</c> feed is live (since #1816) but
/// is scoped to THIS deployment's database, and the registry is a DIFFERENT deployment with a
/// different database, so neither a durable row nor a <c>NOTIFY</c> is shared. What IS
/// shared is the authenticated feed this installation already installs from: <c>GET /api/plugins</c>
/// returns every package's <see cref="PackageManifest.ModuleVersion"/>, the same content identity
/// the install records carry. Comparing the two answers "has anything changed" outright, with no
/// signal, no HMAC, no subscription registry to maintain, and no second wire protocol to keep in
/// step with the first. That is the shape <c>BuildProtocolDriver.FollowGo</c> arrived at for the
/// cross-cluster case (#1440/#1450): end on a fact you can read.</para>
///
/// <para>🚨 <b>No poll timer.</b> The reconcile runs on an event the deployment already has — this
/// process starting — and not on a clock. #1366 removed a staleness clock for the same reason: a
/// timer answers "how stale am I willing to be", which is a question nobody asked, and it turns one
/// misconfiguration into a permanent background load. On these deployments the restart IS the
/// fan-out: plugin content and the framework image are published by the same CI, and the portals
/// self-update onto each new image, so a green plugin build and a pod roll already arrive together.
/// The honest bound is therefore <b>a consumer learns on its next boot</b> — plus immediately, on
/// demand, whenever a human opens the catalog page, which reads the same feed and offers the same
/// Update. An installation that never restarts also never picks up framework fixes, which is a
/// louder problem with its own alarm.</para>
///
/// <para>🚨 <b>A boot read that fails past its budget is DEFERRED, not dropped
/// (Systemorph/MeshWeaver#2888).</b> The 2026-08-31 fix taught the boot read to re-ask a transient
/// answer (503/429/gateway) within a ~26 s budget. That narrowed the failure, and the residual bit
/// the same day: a registry down for longer than the budget left a pod unreconciled for the rest
/// of its life, with one Error line on one pod as the only witness — and the line's own "next
/// chance is a human opening the catalog page" was false, because the catalog page only RENDERS
/// the feed; it never ran the reconcile. Two things change, and neither is a clock:
/// <list type="bullet">
///   <item><b>The skipped reconcile is recorded as PENDING</b> on the node this service owns
///   (<see cref="LedgerPath"/>, a <see cref="RegistryReconcileLedger"/>), and platform admins get
///   ONE <c>Admin</c>-anchored bell notification naming the registry, the attempts and the
///   registry's own last answer — the same surface <see cref="StartupErrorNotifier"/> uses for a
///   degraded boot.</item>
///   <item><b>The next successful feed read drains it.</b> Every registry contact this
///   installation makes goes through <see cref="RegistryPackageSource.ListPackages"/> — a catalog
///   open, an install, the Store's count — and that method hands each successful read back here
///   (<see cref="OnFeedRead"/>). A pending registry then gets the reconcile the boot skipped, from
///   the packages that read already returned: no second read, no timer, no new caller to remember
///   anything.</item>
/// </list></para>
///
/// <para><b>An installation with no registry keeps working.</b> With no registry configured this
/// service resolves no registry source and does nothing at all — the git path stays correct for the
/// registry instance itself, which legitimately reads GitHub and is served by
/// <see cref="PluginUpdateWatcher"/>. The two are complements, not alternatives: the watcher is how
/// the REGISTRY learns from GitHub, this is how a CONSUMER learns from the registry, and both hand
/// the decision to the one <see cref="PackageUpdateReconciler"/> so they can never disagree about
/// what "changed" means or about who opted into an unattended install.</para>
///
/// <para>Sequenced AFTER <see cref="InstanceAutoRegistrationService.Completed"/> — the same
/// ordering idiom <see cref="ModuleDiscoveryService"/> uses, and for the same reason: both write
/// through the same package partitions, and phase 2 of that service is what mints the instance key
/// this one authenticates with. Instance-scoped, so its subscriptions live and die with the mesh.
/// Reactive throughout.</para>
/// </summary>
public sealed class RegistryUpdateReconciler : IHostedService, IDisposable
{
    private readonly IMessageHub hub;
    private readonly ILogger<RegistryUpdateReconciler> logger;
    private readonly CompositeDisposable subscriptions = new();

    /// <summary>Id of the ledger node — an underscore-prefixed sibling in the install-records
    /// partition, exactly like <c>_DefaultInstallLedger</c>.</summary>
    public const string LedgerId = "_RegistryReconcileLedger";

    /// <summary>The node this service owns: the per-registry reconcile state, see
    /// <see cref="RegistryReconcileLedger"/>.</summary>
    public const string LedgerPath = PackageInstaller.InstalledPartition + "/" + LedgerId;

    /// <summary>The ledger's own node type — deliberately distinct from <c>Package</c> so it never
    /// appears in installed-package enumerations (the <c>_DefaultInstallLedger</c> lesson).</summary>
    public const string LedgerNodeType = "RegistryReconcileLedger";

    /// <summary>
    /// How many times a transport-level failure of the feed read is re-attempted within the boot
    /// reconcile (see the RetryWhen in <see cref="ReconcileRegistry"/>). Small on purpose: this
    /// bounds a cold pod's startup work, and the retries exist to survive a hiccup, not to wait out
    /// an outage. An outage longer than the budget is DEFERRED (see the type remarks), never waited out.
    /// </summary>
    internal const int FeedReadRetries = 3;

    /// <summary>Exponential-ish backoff between feed-read attempts: 2 s, 6 s, 18 s.</summary>
    private static TimeSpan FeedReadBackoff(int attempt) =>
        TimeSpan.FromSeconds(2 * Math.Pow(3, attempt));

    /// <summary>
    /// What this process knows about each configured registry, keyed by URL — THE state for the
    /// running process; the ledger node is its durable projection. Instance-scoped (never static)
    /// and immutable-swapped, so a claim (<see cref="OnFeedRead"/>) is one compare-and-swap.
    /// </summary>
    private ImmutableDictionary<string, TrackedRegistry> tracked =
        ImmutableDictionary<string, TrackedRegistry>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The ledger write channel: every state change signals it, and the writes run ONE AT A TIME
    /// (<c>Concat</c>), each projecting the state as it stands when the write starts — so a drain
    /// completing while a boot pass is still recording another registry can never land an older
    /// snapshot over a newer one. Serialization through Rx, never a lock
    /// (Doc/Architecture/RemovingHandWovenGates).
    /// </summary>
    private readonly Subject<Unit> ledgerDirty = new();

    private sealed record TrackedRegistry(PluginRegistryReference Registry, RegistryReconcileEntry Entry);

    /// <summary>Creates the service; the ledger write channel is live from construction so a state
    /// change is recorded whether it arrives from the boot pass or from a later feed read.</summary>
    public RegistryUpdateReconciler(IMessageHub hub, ILogger<RegistryUpdateReconciler> logger)
    {
        this.hub = hub;
        this.logger = logger;
        subscriptions.Add(ledgerDirty
            .Select(_ => Observable.Defer(WriteLedger)
                .Catch((Exception ex) =>
                {
                    // A ledger write failure must not stop the channel — the NEXT change re-projects
                    // the whole state, so nothing is lost but this one snapshot.
                    logger.LogWarning(ex, "[RegistryUpdate] could not write the reconcile ledger at {Path}", LedgerPath);
                    return Observable.Return(Unit.Default);
                }))
            .Concat()
            .Subscribe(_ => { }, ex => logger.LogWarning(ex, "[RegistryUpdate] the reconcile ledger channel faulted")));
    }

    /// <summary>
    /// Whether a failed feed read is worth re-asking within the boot window.
    ///
    /// <para>🚨 <b>The distinction is the whole point, and it used to be lost.</b> This predicate
    /// was once <c>fault is not InvalidOperationException</c> — reading that type as "a definite
    /// HTTP answer (401/403/404), which will answer the same in two seconds". That was true of the
    /// statuses it named and false of the type: <see cref="RegistryPackageSource"/> threw the SAME
    /// bare <see cref="InvalidOperationException"/> for every non-2xx, so a
    /// <c>503 Instance-key resolution is temporarily unavailable — retry shortly</c> was excluded
    /// by a rule written for permission errors. The one condition a boot retry exists for was the
    /// one condition it skipped, and a transient registry blip left a pod's installed set
    /// unreconciled for the rest of its life — silently, because the portal itself stays up
    /// (Systemorph/MeshWeaver#2836).</para>
    ///
    /// <para>The fix is not a longer budget nor a poll behind it: it is that the status now
    /// survives as data (<see cref="RegistryResponseException.StatusCode"/>), so the policy can ask
    /// what the server actually said instead of inferring it from a CLR type.</para>
    /// </summary>
    internal static bool ShouldRetryFeedRead(Exception fault) => fault switch
    {
        // A definite answer naming a transient server-side condition (503/429/5xx) — re-ask.
        RegistryResponseException http => http.IsTransientFailure,
        // Any other definite answer (401/403/404, a malformed payload) — re-asking cannot help.
        InvalidOperationException => false,
        // A transport fault (TCP hiccup, DNS blip, request timeout): the original #1500 case,
        // where the read never reached an HTTP answer at all.
        _ => true,
    };

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
    public void Dispose() => subscriptions.Dispose();

    private void Start()
    {
        var options = hub.ServiceProvider.GetService<PluginCatalogOptions>() ?? new PluginCatalogOptions();
        if (options.EffectiveRegistries.Count == 0)
        {
            logger.LogDebug(
                "[RegistryUpdate] no registry configured — nothing to reconcile against. "
                + "A registry instance learns from its own repos through the build watcher instead.");
            return;
        }

        var autoRegistration = hub.ServiceProvider.GetService<InstanceAutoRegistrationService>();
        // Ordering on the actual precondition, not a delay: the default install writes the same
        // partitions and mints the instance key this reconcile authenticates with. A host that did
        // not register that service has nothing to be behind.
        var defaultsDone = autoRegistration?.Completed.Take(1).Select(_ => Unit.Default)
            ?? Observable.Return(Unit.Default);

        subscriptions.Add(defaultsDone
            // Hop off whatever thread completed the default install before the reconcile chain runs.
            .ObserveOn(TaskPoolScheduler.Default)
            .SelectMany(_ => Reconcile(options))
            // 🚨 SubscribeOn the thread pool, NOT the host-startup thread — the chain is synchronous
            // up to its first genuinely-async leaf, so subscribing inline would run it ON the
            // startup thread and re-enter the hub schedulers mid-init. Same fix, same reason, as
            // InstanceAutoRegistrationService.
            .SubscribeOn(TaskPoolScheduler.Default)
            .Subscribe(
                _ => { },
                ex => logger.LogError(ex,
                    "[RegistryUpdate] the boot reconcile failed; installed packages keep their "
                    + "current version and the catalog page still offers a manual Update.")));
    }

    /// <summary>
    /// Reads each configured registry's feed and reconciles this installation's install records
    /// against it. Registries are read SEQUENTIALLY: this runs on a cold starting pod, and an
    /// apply writes a partition's worth of nodes.
    /// </summary>
    private IObservable<Unit> Reconcile(PluginCatalogOptions options)
    {
        if (hub.ServiceProvider.GetService<RegistryTokenResolver>() is null)
            return Observable.Return(Unit.Default);

        return RegistryTokenResolver.WithLegacyTokens(options, options.EffectiveRegistries)
            .Select(registry => ReconcileRegistry(registry, FeedReadBackoff))
            .ToObservable()
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .LastAsync();
    }

    /// <summary>
    /// The boot reconcile against ONE registry: read its feed within the retry budget, then
    /// reconcile the install records against what it serves. Exhausting the budget records the
    /// registry as pending and notifies admins (see the type remarks) — it never throws, so one
    /// unreachable registry cannot withhold the others. Emits exactly once.
    /// </summary>
    /// <param name="registry">The registry to reconcile against.</param>
    /// <param name="backoff">The pause before re-asking attempt <c>n</c> (0-based) — production
    /// passes <see cref="FeedReadBackoff"/>; a test hands in zero so the exhausted budget is
    /// reached in milliseconds instead of ~26 s.</param>
    internal IObservable<Unit> ReconcileRegistry(PluginRegistryReference registry, Func<int, TimeSpan> backoff)
    {
        var tokenResolver = hub.ServiceProvider.GetService<RegistryTokenResolver>();
        if (tokenResolver is null)
            return Observable.Return(Unit.Default);

        var name = DisplayName(registry);
        var gitRef = EffectiveRef(registry.Ref);
        // Every configured registry appears on the ledger from the moment it is attempted, so an
        // admin can tell "not configured" from "configured, never answered".
        Mark(registry, entry => entry);

        return tokenResolver.ResolveToken(registry)
            .Take(1)
            .SelectMany(token =>
            {
                if (token.Length == 0)
                    logger.LogWarning(
                        "[RegistryUpdate] reading {Url} with NO instance key — only an open dev/e2e "
                        + "registry will answer, so installed packages may not be reconciled.",
                        registry.Url);

                // ONE bundle client for the pass, shared by the source (a content apply lands its
                // module inside InstallOrUpdate) and the module pass below — so the promise-cached
                // bundle index is read once per registry per boot.
                var bundles = new PluginBundleClient(hub, registry.Url, token);
                var source = new RegistryPackageSource(hub, registry.Url, token) { Bundles = bundles };
                return source.ListPackages(gitRef)
                    // 🚨 The ONE attempt the design allots must actually get a fair chance (#1500).
                    //
                    // This service deliberately has NO poll timer (see the type remarks): the bound
                    // it promises is "a consumer learns on its NEXT BOOT". A single TCP hiccup on a
                    // cold pod silently consumed that one chance — the feed read failed, the Error
                    // was logged, and the installation then stayed unreconciled for the whole life
                    // of the pod, because nothing here ever asks again. That is the defect: not the
                    // timeout's length, but that the boot attempt is unrepeated.
                    //
                    // So this is a bounded retry INSIDE the boot attempt, not a clock. It does not
                    // widen any bound and it adds no background load. What it retries is decided by
                    // ShouldRetryFeedRead — a DEFINITE refusal is not re-asked, because a registry
                    // that refuses this instance's key will refuse it again in two seconds, and
                    // burning the boot window on that would only delay the log line naming it.
                    // Past the budget the read is NOT abandoned either: the exhaustion is typed so
                    // the Catch below can record how many attempts were spent and DEFER (#2888).
                    .RetryWhen(faults => faults
                        .Select((fault, attempt) => (fault, attempt))
                        .SelectMany(f => ShouldRetryFeedRead(f.fault) && f.attempt < FeedReadRetries
                            ? Observable.Timer(backoff(f.attempt)).Select(_ => Unit.Default)
                                .Do(_ => logger.LogWarning(f.fault,
                                    "[RegistryUpdate] reading {Url} failed (attempt {Attempt}/{Total}) — retrying "
                                    + "within the boot budget.",
                                    registry.Url, f.attempt + 1, FeedReadRetries + 1))
                            : Observable.Throw<Unit>(new FeedReadExhaustedException(f.fault, f.attempt + 1))))
                    .Take(1)
                    .SelectMany(packages => ReconcileFromFeed(
                        registry, name, gitRef, source, bundles, packages, RegistryReconcileEntry.ViaBoot));
            })
            .Catch((Exception ex) => Defer(registry, name, ex));
    }

    /// <summary>
    /// A successful feed read happened somewhere in this process — the event a deferred reconcile
    /// waits for. Called by <see cref="RegistryPackageSource.ListPackages"/> for EVERY successful
    /// read, whoever subscribed it; a no-op unless <paramref name="registryUrl"/> is a configured
    /// registry whose boot reconcile is pending at the ref the caller read. The pending marker is
    /// CLAIMED with one compare-and-swap, so two catalog opens in the same second drain it once.
    /// The reconcile then runs from <paramref name="packages"/> — the read that just happened —
    /// with no second round-trip, off the caller's thread, and re-marks the registry pending if it
    /// faults, so nothing is ever silently dropped.
    /// </summary>
    internal void OnFeedRead(string registryUrl, string gitRef, IReadOnlyList<PackageManifest> packages)
    {
        var key = Key(registryUrl);
        if (!tracked.TryGetValue(key, out var candidate) || !candidate.Entry.Pending)
            return;
        var readRef = EffectiveRef(gitRef);
        if (!string.Equals(readRef, EffectiveRef(candidate.Registry.Ref), StringComparison.Ordinal))
        {
            logger.LogDebug(
                "[RegistryUpdate] {Name} answered a feed read at {ReadRef}, but its reconcile is pending at "
                + "{Ref} — leaving it pending.", candidate.Entry.Name, readRef, candidate.Registry.Ref);
            return;
        }
        var tokenResolver = hub.ServiceProvider.GetService<RegistryTokenResolver>();
        if (tokenResolver is null)
            return;

        // The claim: only the writer that swaps the marker out runs the drain.
        var claimed = candidate with { Entry = candidate.Entry with { Pending = false } };
        if (!ImmutableInterlocked.TryUpdate(ref tracked, key, claimed, candidate))
            return;

        var registry = candidate.Registry;
        var name = candidate.Entry.Name;
        logger.LogInformation(
            "[RegistryUpdate] {Name} ({Url}) answered a feed read — running the reconcile this boot "
            + "could not ({Count} package(s) served at {Ref}).",
            name, registry.Url, packages.Count, readRef);

        subscriptions.Add(tokenResolver.ResolveToken(registry)
            .Take(1)
            .SelectMany(token =>
            {
                var bundles = new PluginBundleClient(hub, registry.Url, token);
                var source = new RegistryPackageSource(hub, registry.Url, token) { Bundles = bundles };
                return ReconcileFromFeed(
                    registry, name, readRef, source, bundles, packages, RegistryReconcileEntry.ViaFeedRead);
            })
            // Off the reading caller's thread: that is an IO-pool continuation (a catalog render, an
            // install) and the reconcile is a partition's worth of writes.
            .SubscribeOn(TaskPoolScheduler.Default)
            .Subscribe(
                _ => { },
                ex =>
                {
                    logger.LogError(ex,
                        "[RegistryUpdate] the deferred reconcile against {Name} ({Url}) failed — it stays "
                        + "pending and the next successful feed read re-runs it.", name, registry.Url);
                    Mark(registry, entry => entry with
                    {
                        Pending = true,
                        PendingSince = DateTimeOffset.UtcNow,
                        LastFault = ex.Message,
                    });
                }));
    }

    /// <summary>The reconcile proper — the content lane, then the module lane — followed by the
    /// ledger entry recording that it completed and how it was reached.</summary>
    private IObservable<Unit> ReconcileFromFeed(
        PluginRegistryReference registry,
        string name,
        string gitRef,
        RegistryPackageSource source,
        PluginBundleClient bundles,
        IReadOnlyList<PackageManifest> packages,
        string via)
    {
        logger.LogInformation(
            "[RegistryUpdate] {Name} serves {Count} package(s) at {Ref} — reconciling this installation's "
            + "records against them ({Via}).",
            name, packages.Count, gitRef, via);
        return PackageUpdateReconciler.ReconcileInstalled(
                hub, source, gitRef, packages, $"Served by registry '{name}'", logger)
            // The MODULE lane of the same reconcile (#1664): for installed packages that declare a
            // compiled module, consult the registry's bundle index and land what is newer FOR THE
            // RUNNING framework. AFTER the content reconcile, so a content update and its module
            // land in one pass; keyed on the bundle index, NOT the content hash — a bundle rebuilt
            // for a new framework MVID has unchanged content, and this lane is what heals it after
            // an image roll.
            .SelectMany(_ => ReconcileModules(bundles, name, packages))
            .Do(_ => Mark(registry, entry => entry with
            {
                Pending = false,
                PendingSince = null,
                LastFault = null,
                LastReconciledAt = DateTimeOffset.UtcNow,
                LastReconciledVia = via,
            }));
    }

    /// <summary>
    /// The boot read is over and the reconcile did not run: record it as pending on the ledger and
    /// tell platform admins — ONE bell notification anchored under <c>Admin</c> (RLS scopes it to
    /// them), the same surface a degraded boot uses. Emits once; never throws.
    /// </summary>
    private IObservable<Unit> Defer(PluginRegistryReference registry, string name, Exception fault)
    {
        var attempts = fault is FeedReadExhaustedException exhausted ? exhausted.Attempts : 1;
        var cause = fault is FeedReadExhaustedException { InnerException: { } inner } ? inner : fault;
        logger.LogError(cause,
            "[RegistryUpdate] could not read the package feed of {Name} ({Url}) after {Attempts} attempt(s) "
            + "— installed packages from it are NOT reconciled this boot. Recorded as PENDING on {Ledger}; "
            + "the next successful feed read against this registry (a catalog open, an install) runs "
            + "the reconcile. Platform admins have been notified.",
            name, registry.Url, attempts, LedgerPath);

        Mark(registry, entry => entry with
        {
            Pending = true,
            PendingSince = DateTimeOffset.UtcNow,
            Attempts = attempts,
            LastFault = cause.Message,
        });

        var access = hub.ServiceProvider.GetService<AccessService>();
        return NotificationService.Dispatch(
                hub,
                recipient: null,
                mainNodePath: StartupErrorNotifier.AdminPartition,
                title: access.Localize("plugins.reconcile.deferred.title", name),
                message: access.Localize("plugins.reconcile.deferred.body", name, registry.Url, attempts, cause.Message),
                type: NotificationType.System,
                targetNodePath: LedgerPath,
                createdBy: "system")
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex,
                    "[RegistryUpdate] raising the admin notification for the deferred reconcile of {Name} failed.",
                    name);
                return Observable.Return(Unit.Default);
            })
            .DefaultIfEmpty(Unit.Default)
            .LastAsync();
    }

    /// <summary>Applies <paramref name="mutate"/> to the registry's entry (creating it on first sight)
    /// and signals the ledger channel.</summary>
    private void Mark(PluginRegistryReference registry, Func<RegistryReconcileEntry, RegistryReconcileEntry> mutate)
    {
        var key = Key(registry.Url);
        ImmutableInterlocked.AddOrUpdate(
            ref tracked,
            key,
            _ => new TrackedRegistry(registry, mutate(new RegistryReconcileEntry
            {
                Url = key,
                Name = DisplayName(registry),
                Ref = EffectiveRef(registry.Ref),
            })),
            (_, current) => current with { Entry = mutate(current.Entry) });
        if (!subscriptions.IsDisposed)
            ledgerDirty.OnNext(Unit.Default);
    }

    /// <summary>Projects the in-memory state onto the ledger node — a full snapshot, written as
    /// System (the install-records partition is System-owned), after making sure that partition is
    /// provisioned, because a consumer can be configured with a registry before anything from it
    /// is installed.</summary>
    private IObservable<Unit> WriteLedger()
    {
        var snapshot = tracked;
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return(Unit.Default);
        var access = hub.ServiceProvider.GetService<AccessService>();

        // Satellite(): MainNode = the Plugins partition root, not the ledger's own path (#2383) —
        // bookkeeping that points at itself IS a main node by the catalog's definition and would be
        // listed as partition CONTENT and put in mesh-wide search.
        var node = MeshNode.Satellite(LedgerId, PackageInstaller.InstalledPartition) with
        {
            Name = "Registry reconcile ledger",
            NodeType = LedgerNodeType,
            State = MeshNodeState.Active,
            Content = new RegistryReconcileLedger
            {
                Registries = snapshot.Values
                    .Select(t => t.Entry)
                    .OrderBy(e => e.Url, StringComparer.OrdinalIgnoreCase)
                    .ToImmutableList(),
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };
        return access.RunAsSystem(() => PackageInstaller
            .EnsurePartitionsProvisioned(hub, PackageInstaller.InstalledPartition)
            .SelectMany(_ => meshService.CreateOrUpdateNode(node))
            .Select(_ => Unit.Default));
    }

    private static string Key(string? url) => (url ?? "").Trim().TrimEnd('/');

    private static string EffectiveRef(string? gitRef) => string.IsNullOrWhiteSpace(gitRef) ? "HEAD" : gitRef;

    private static string DisplayName(PluginRegistryReference registry) =>
        string.IsNullOrWhiteSpace(registry.Name) ? Key(registry.Url) : registry.Name;

    /// <summary>The feed read spent its whole budget (or met a definite refusal) — carries the last
    /// fault and how many attempts were made, so the deferral can record both.</summary>
    private sealed class FeedReadExhaustedException(Exception cause, int attempts)
        : Exception(cause.Message, cause)
    {
        public int Attempts { get; } = attempts;
    }

    /// <summary>The most one package's module adopt may take before the reconcile moves on —
    /// generous for a large bundle download, small against a boot; the point is only that it is
    /// FINITE (see the Timeout note below, Plugins#959).</summary>
    internal static readonly TimeSpan PerPackageAdoptBudget = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The module half of the boot reconcile (#1664 Slice C): for each of the registry's packages
    /// that declares a compiled module AND has an install record here, run the one module-update
    /// decision (<see cref="ModuleUpdateDecision"/>) via <see cref="PluginBundleClient.AdoptModule"/>
    /// — which lands a newer bundle for the RUNNING framework MVID through
    /// <see cref="ModuleLandingService"/> and flags <c>PendingRestart</c>, skips an up-to-date one
    /// without a download, and skips a foreign-framework registry silently-with-log (it becomes
    /// relevant after the next image roll).
    ///
    /// <para><b>The policy gate is the deployment's EXISTING update policy</b>
    /// (<see cref="IModuleUpdatePolicy"/> — the memex portals wire it to <c>Admin/UpdatePolicy</c>):
    /// this lane passes <c>unattended: true</c>, so Continuous (the platform default, and the
    /// default when no policy is registered) lands unattended while Stable/None decline. There is
    /// deliberately NO module-specific knob.</para>
    ///
    /// <para>Sequential, and failure-tolerant per package — one unreachable bundle must not
    /// withhold the rest, and <see cref="PluginBundleClient.AdoptModule"/> already absorbs its own
    /// failures into a logged zero.</para>
    /// </summary>
    private IObservable<Unit> ReconcileModules(
        PluginBundleClient bundles,
        string registryName,
        IReadOnlyList<PackageManifest> packages)
    {
        var declaring = packages
            .Where(p => !string.IsNullOrWhiteSpace(p.Module))
            .ToArray();
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (declaring.Length == 0 || storage is null)
            return Observable.Return(Unit.Default);

        return declaring
            .Select(pkg =>
            {
                var recordPath = $"{PackageInstaller.InstalledPartition}/{pkg.Id}";
                return storage.Read(recordPath, hub.JsonSerializerOptions)
                    .Take(1)
                    .SelectMany(record => record is null
                        // Not installed here → somebody else's module; nothing to reconcile.
                        ? Observable.Return(0)
                        : bundles.AdoptModule(pkg.Id, pkg.Module!, recordPath, unattended: true))
                    // 🚨 A HANG is worse than a failure here: the packages run as one sequential
                    // Concat, so a single adopt that never answers (a wedged record read, a
                    // download that stalls) silently starves EVERY package after it — on
                    // memex.systemorph.com the Northwind adopt was never even attempted while
                    // earlier packages logged failures (Plugins#959). A bounded wait turns the
                    // hang into the loud, caught failure below and the chain proceeds.
                    .Timeout(PerPackageAdoptBudget)
                    .Catch((Exception ex) =>
                    {
                        // The CAUSE goes into the message itself, not only the attached exception:
                        // single-line log pipelines (Loki greps) see the template line alone, and
                        // "failed" with no reason cost a night of archaeology (Plugins#959).
                        logger.LogWarning(ex,
                            "[RegistryUpdate] module reconcile of {Id} against {Name} failed — "
                            + "its landed module is unchanged. Cause: {Cause}",
                            pkg.Id, registryName, ex.Message);
                        return Observable.Return(0);
                    })
                    .Select(_ => Unit.Default);
            })
            .ToObservable()
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .LastAsync();
    }
}
