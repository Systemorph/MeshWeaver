using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
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
/// <para><b>Read the witness, do not wait to be told.</b> There is no cross-process change feed to
/// wait on — <c>PostgreSqlChangeListener</c> is registered and never started — and the registry is
/// a DIFFERENT deployment with a different database, so even a durable row is not shared. What IS
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
public sealed class RegistryUpdateReconciler(
    IMessageHub hub, ILogger<RegistryUpdateReconciler> logger)
    : IHostedService, IDisposable
{
    private readonly CompositeDisposable subscriptions = new();

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
        var tokenResolver = hub.ServiceProvider.GetService<RegistryTokenResolver>();
        if (tokenResolver is null)
            return Observable.Return(Unit.Default);

        return options.EffectiveRegistries
            .Select(registry => ReconcileOne(registry, tokenResolver))
            .ToObservable()
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .LastAsync();
    }

    private IObservable<Unit> ReconcileOne(
        PluginRegistryReference registry, RegistryTokenResolver tokenResolver)
    {
        var name = string.IsNullOrWhiteSpace(registry.Name) ? registry.Url : registry.Name;
        var gitRef = string.IsNullOrWhiteSpace(registry.Ref) ? "HEAD" : registry.Ref;

        return tokenResolver.ResolveToken(registry)
            .Take(1)
            .SelectMany(token =>
            {
                if (token.Length == 0)
                    logger.LogWarning(
                        "[RegistryUpdate] reading {Url} with NO instance key — only an open dev/e2e "
                        + "registry will answer, so installed packages may not be reconciled.",
                        registry.Url);

                var source = new RegistryPackageSource(hub, registry.Url, token);
                return source.ListPackages(gitRef)
                    .Take(1)
                    .SelectMany(packages =>
                    {
                        logger.LogInformation(
                            "[RegistryUpdate] {Name} serves {Count} package(s) at {Ref} — "
                            + "reconciling this installation's records against them.",
                            name, packages.Count, gitRef);
                        return PackageUpdateReconciler.ReconcileInstalled(
                            hub, source, gitRef, packages,
                            $"Served by registry '{name}'", logger);
                    });
            })
            .Catch((Exception ex) =>
            {
                // One unreachable registry must not withhold the others, and must never be silent:
                // "the store is empty and nobody said why" is the exact failure this issue began as.
                logger.LogError(ex,
                    "[RegistryUpdate] could not read the package feed of {Name} ({Url}) — installed "
                    + "packages from it are NOT reconciled this boot.", name, registry.Url);
                return Observable.Return(Unit.Default);
            });
    }
}
