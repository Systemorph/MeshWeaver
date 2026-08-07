using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// THE DEFAULT INSTALL. Every package whose manifest declares
/// <see cref="PackageManifest.PreInstalled"/> is installed on this instance at startup, and
/// re-reconciled on every boot thereafter — so the platform's own catalogs (the Agents and Skills
/// libraries, Essentials, …) are present on a fresh mesh with nobody clicking anything, and stay
/// present across a self-update.
///
/// <para><b>The gap this closes (#902).</b> <c>MeshWeaver.Plugins</c> has shipped
/// <c>"preInstalled": true</c> on its <c>Agent</c> and <c>Skill</c> roots since they were authored,
/// and NOTHING in the platform ever read that flag: it was a promise the machinery did not keep.
/// The consequences were both invisible and severe — the <c>Skill</c> catalog existed on the
/// production portal only because somebody hand-placed a <c>_GitSync</c> + <c>_Policy</c> pair, and
/// the <c>Agent</c> catalog, which never got that hand-treatment, was simply GONE for every user
/// with no mechanism anywhere that could bring it back.</para>
///
/// <para><b>Same install, no second path.</b> Each package goes through
/// <see cref="CatalogLayoutAreas.InstallOrUpdate"/> — the very method the Update button and the
/// green-build watcher use — so the manifest-diff fast path, the "nothing to sync" content-identity
/// gate and the per-node <c>SyncBehavior</c> claim all apply unchanged. An instance whose packages
/// are already current therefore does ONE catalog listing per source and writes nothing.</para>
///
/// <para><b>Publication is re-asserted every boot</b>, independent of whether anything installed:
/// <see cref="PackageInstaller.EnsurePreInstalledPublicRead"/> is create-only, so it costs one
/// storage read per package when the policy is already there — and it is what converges an instance
/// whose partition was left unreadable (the exact <c>Agent</c> failure). The install itself is the
/// ghost-root repair: it UPSERTS the partition root rather than creating it, so an existing-but-
/// unreadable root is rewritten into a healthy one instead of being refused "already exists" the
/// way a bare <c>create</c> is.</para>
///
/// <para><b>Where the packages come from.</b> Three source kinds, in precedence order, deduplicated
/// by package id (first source wins — the same precedence the registry's merged catalog uses):
/// sources registered in DI (the extension point), this instance's own configured git sources
/// (<c>PluginCatalog:Sources</c> — a REGISTRY instance is not a consumer of its own HTTP surface,
/// so it must read them directly), and the registries this instance consumes
/// (<see cref="PluginCatalogOptions.EffectiveRegistries"/>). An instance that configures none of
/// them does nothing at all.</para>
///
/// <para>Reactive throughout — no <c>async</c>, no polling, no watchdog. Sequential by construction
/// (<c>Concat</c>): concurrent installs deadlock on the shared effective-permissions rebuild, the
/// same reason <c>PackageInstaller</c> warms its roots one at a time. Instance-scoped, so the
/// subscription lives and dies with the mesh (<c>Doc/Architecture/NoStaticState</c>).</para>
/// </summary>
/// <param name="hub">Hub supplying the services, configuration and install pipeline.</param>
/// <param name="logger">Logger for the reconcile summary.</param>
public sealed class PreInstalledPackageService(
    IMessageHub hub, ILogger<PreInstalledPackageService>? logger = null)
    : IHostedService, IDisposable
{
    private IDisposable? subscription;

    /// <summary>The boot pass's outcome, replayed to whoever asks after the fact.</summary>
    private readonly System.Reactive.Subjects.AsyncSubject<PreInstallSummary> completed = new();

    /// <summary>
    /// The boot pass's outcome — emits once, when the startup default install has finished, and
    /// replays that emission to late subscribers (<c>AsyncSubject</c>). This is the signal to wait
    /// on when something must happen AFTER the platform's defaults are in place; nothing polls.
    /// Never emits on an instance whose hosted service was not started.
    /// </summary>
    public IObservable<PreInstallSummary> Completed => completed;

    /// <summary>
    /// Hosted-service start — what actually runs the default install on boot. Registered as an
    /// <c>IHostedService</c> in <see cref="PluginCatalogConfigurationExtensions.AddPluginCatalog{TBuilder}"/>;
    /// returns immediately (the reconcile is a reactive chain, there is nothing to await), and a
    /// failure is logged rather than propagated — the portal must come up whether or not a plugin
    /// source is reachable.
    ///
    /// <para>🚨 <c>SubscribeOn</c> the thread pool, NOT the host-startup thread. The chain is
    /// synchronous right up to its first genuinely-async leaf (an in-memory or already-cached source
    /// lists its packages inline), so subscribing here would run the whole install ON the startup
    /// thread — re-entering the hub schedulers mid-init, which deadlocks. Same fix, same reason, as
    /// <c>StaticRepoImportHostedService</c>.</para>
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        subscription = EnsureDefaults()
            .SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)
            .Do(summary => logger?.LogInformation(
                "[PreInstall] default install reconciled: {Summary}", summary))
            // A failed pass must not leave the completion signal hanging forever — report the
            // failure as a pass that installed nothing, having already logged the cause.
            .Catch((Exception exception) =>
            {
                logger?.LogWarning(exception, "[PreInstall] default install failed");
                return Observable.Return(PreInstallSummary.Empty);
            })
            .Subscribe(completed);
        return Task.CompletedTask;
    }

    /// <summary>Hosted-service stop: closes the reconcile subscription (idempotent with <see cref="Dispose"/>).</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        subscription?.Dispose();
        subscription = null;
        completed.Dispose();
    }

    /// <summary>
    /// Installs (or re-reconciles) every <see cref="PackageManifest.PreInstalled"/> package this
    /// instance can see, and re-asserts each one's public read. COLD — the work runs on Subscribe —
    /// and emits exactly one <see cref="PreInstallSummary"/> when the whole pass is done, which is
    /// what makes the default install assertable in a test rather than a race against startup.
    /// </summary>
    public IObservable<PreInstallSummary> EnsureDefaults() =>
        Sources()
            .SelectMany(sources => sources.Count == 0
                ? Observable.Return(PreInstallSummary.Empty)
                : Candidates(sources)
                    .SelectMany(candidates => candidates.Count == 0
                        ? Observable.Return(PreInstallSummary.Empty)
                        : candidates
                            .Select(Reconcile)
                            .ToObservable()
                            .Concat()
                            .Aggregate(PreInstallSummary.Empty, (acc, one) => acc.Add(one))));

    /// <summary>
    /// The sources to read the default install out of, in precedence order. Cold; emits once.
    /// Registry token resolution is reactive (it reads the stored auto-registration credential), so
    /// this is an observable rather than a plain list.
    /// </summary>
    private IObservable<IReadOnlyList<ConfiguredPackageSource>> Sources()
    {
        var services = hub.ServiceProvider;
        var options = services.GetService<PluginCatalogOptions>();

        // 1. DI-registered sources — the extension point (and the seam a test hands a repo in on).
        var registered = services.GetServices<IPackageSource>()
            .Select((s, i) => new ConfiguredPackageSource(s, "HEAD", $"registered-{i}"))
            .ToList();

        // 2. This instance's OWN configured git sources. A registry instance serves these over
        //    /api/plugins and is not a consumer of its own HTTP surface, so it must read the same
        //    config directly — through the shared reader, so serving and pre-installing agree.
        var configured = services.GetService<IConfiguration>() is { } config
            ? PackageSources.FromConfiguration(hub, config, logger)
            : [];

        // 3. The registries this instance consumes, each with its resolved instance token.
        var registries = options?.EffectiveRegistries ?? [];
        var tokenResolver = services.GetService<RegistryTokenResolver>();
        var remote = registries.Count == 0 || tokenResolver is null
            ? Observable.Return<IReadOnlyList<ConfiguredPackageSource>>([])
            : registries
                .Select(registry => tokenResolver.ResolveToken(registry)
                    .Take(1)
                    .Select(token => new ConfiguredPackageSource(
                        new RegistryPackageSource(hub, registry.Url, token),
                        string.IsNullOrWhiteSpace(registry.Ref) ? "HEAD" : registry.Ref,
                        string.IsNullOrWhiteSpace(registry.Name) ? registry.Url : registry.Name)))
                .ToObservable()
                .Concat()
                .ToList()
                .Select(list => (IReadOnlyList<ConfiguredPackageSource>)list);

        return remote.Select(remoteSources => (IReadOnlyList<ConfiguredPackageSource>)registered
            .Concat(configured)
            .Concat(remoteSources)
            .ToList());
    }

    /// <summary>
    /// Every pre-installed package the sources offer, deduplicated by id (first source wins) and
    /// ordered by id so a boot's install order is deterministic. A source that cannot be listed is
    /// logged and skipped — one unreachable repo must not withhold the packages the others carry.
    /// </summary>
    private IObservable<IReadOnlyList<PreInstallCandidate>> Candidates(
        IReadOnlyList<ConfiguredPackageSource> sources) =>
        sources
            .Select(source => source.Source.ListPackages(source.GitRef)
                .Take(1)
                .Select(packages => packages
                    .Where(p => p.PreInstalled)
                    .Select(p => new PreInstallCandidate(source, p))
                    .ToList())
                .Catch((Exception exception) =>
                {
                    logger?.LogWarning(exception,
                        "[PreInstall] listing {Name} @ {Ref} failed — its default packages are skipped "
                        + "this boot", source.Name, source.GitRef);
                    return Observable.Return(new List<PreInstallCandidate>());
                }))
            .ToObservable()
            .Concat()
            .ToList()
            .Select(perSource => (IReadOnlyList<PreInstallCandidate>)perSource
                .SelectMany(list => list)
                .GroupBy(c => c.Package.Id, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(c => c.Package.Id, StringComparer.Ordinal)
                .ToList());

    /// <summary>
    /// Installs (or re-reconciles) ONE default package and re-asserts its public read.
    ///
    /// <para>SYSTEM for the whole lifetime, for the same reason the catalog click is: an install is
    /// PROVISIONING — every partition it creates lands under the System identity with no user
    /// grants, so any step that authorises against an ambient identity fails closed. There is no
    /// user here at all (this runs on boot), so the impersonation is not a widening of anyone's
    /// rights; the manifest's own <c>preInstalled</c> declaration is the authorization.</para>
    ///
    /// <para>A failure is reported in the summary and stepped over: the packages are independent,
    /// and one unreachable or malformed package must not withhold the rest of the platform's
    /// defaults.</para>
    /// </summary>
    private IObservable<PreInstallSummary> Reconcile(PreInstallCandidate candidate)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var package = candidate.Package;
        var partition = string.IsNullOrWhiteSpace(package.TargetPartition)
            ? package.Id
            : package.TargetPartition!;

        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => CatalogLayoutAreas
                    .InstallOrUpdate(hub, candidate.Source.Source, candidate.Source.GitRef, package, logger)
                    .Take(1)
                    .Do(result => logger?.LogInformation(
                        "[PreInstall] {Id} → {Partition}: {Written} written, {Unchanged} unchanged",
                        package.Id, partition, result.Written, result.Unchanged))
                    // Publication is re-asserted even when nothing installed — that is what heals an
                    // instance whose partition was left unreadable, and it is free once in place.
                    .SelectMany(result => PackageInstaller
                        .EnsurePreInstalledPublicRead(hub, package, partition, logger)
                        .Select(_ => result)))
            .Select(result => new PreInstallSummary(
                Installed: result.Written > 0 ? 1 : 0,
                UpToDate: result.Written > 0 ? 0 : 1,
                Failed: 0,
                Packages: [package.Id]))
            .Catch((Exception exception) =>
            {
                logger?.LogWarning(exception,
                    "[PreInstall] installing default package {Id} failed; the instance is missing it "
                    + "until the next boot or a manual install", package.Id);
                return Observable.Return(new PreInstallSummary(0, 0, 1, [package.Id]));
            });
    }

    /// <summary>One package the default install should carry, and the source it came from.</summary>
    private sealed record PreInstallCandidate(ConfiguredPackageSource Source, PackageManifest Package);
}

/// <summary>
/// What one default-install pass did: how many packages were written, how many were already
/// current, how many failed, and which packages the pass covered. Emitted once per
/// <see cref="PreInstalledPackageService.EnsureDefaults"/> so both the boot log line and a test can
/// read the same outcome.
/// </summary>
/// <param name="Installed">Packages that actually wrote content this pass.</param>
/// <param name="UpToDate">Packages already at the catalog's content version.</param>
/// <param name="Failed">Packages whose install threw (logged, stepped over).</param>
/// <param name="Packages">The package ids the pass covered, in install order.</param>
public readonly record struct PreInstallSummary(
    int Installed, int UpToDate, int Failed, ImmutableList<string> Packages)
{
    /// <summary>A pass that covered nothing.</summary>
    public static PreInstallSummary Empty { get; } = new(0, 0, 0, ImmutableList<string>.Empty);

    /// <summary>Folds one package's outcome into the running total.</summary>
    public PreInstallSummary Add(PreInstallSummary other) => new(
        Installed + other.Installed,
        UpToDate + other.UpToDate,
        Failed + other.Failed,
        Packages.AddRange(other.Packages));

    /// <inheritdoc />
    public override string ToString() =>
        $"{Installed} installed, {UpToDate} up to date, {Failed} failed "
        + $"[{string.Join(", ", Packages)}]";
}
