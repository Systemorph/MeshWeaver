using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// THE decision "this installed module changed — remind, or install it" — in one place, for every
/// path that can learn a module changed.
///
/// <para>There are two such paths and they must never disagree:</para>
/// <list type="bullet">
///   <item><see cref="PluginUpdateWatcher"/> — a plugin repo's CI went green, learned from a
///     <c>BuildCompletion</c> node a GitHub <c>workflow_run</c> webhook wrote. This is the
///     REGISTRY's input: it is the installation that holds the GitHub credential and receives the
///     hooks.</item>
///   <item><see cref="RegistryUpdateReconciler"/> — the packages a registry is serving no longer
///     match what is installed here, learned by READING the registry's own feed. This is the
///     CONSUMER's input, and before it existed a registry-only installation had none at all
///     (Systemorph/MeshWeaver#1318).</item>
/// </list>
///
/// <para><b>A new build is NOT a change, and neither is a new feed read.</b> The gate is content
/// identity per MODULE — <see cref="PackageManifest.ModuleVersion"/>, a hash over the module's
/// sorted (path, file-hash) pairs — compared against the install record's. Equal ⇒ silence: no
/// notification, no fetch. That comparison, and the install that follows it, are deliberately
/// delegated to <see cref="CatalogLayoutAreas.InstallOrUpdate"/>, the very same call the Update
/// button makes.</para>
///
/// <para><b>Reminder by default; unattended on opt-in.</b> A changed module raises a
/// <c>Notification</c> satellite on the install record unless that record opted in
/// (<see cref="PackageManifest.AutoUpdate"/>, stamped at install time from the deployment's
/// <see cref="PluginCatalogOptions.AutoUpdateByDefault"/>). A commercial package is re-authorized
/// against the principal recorded at install time on EVERY unattended apply (#830) — there is no
/// user on a background reaction, so revoking that principal's admin must stop the syncing.</para>
///
/// <para>Reactive throughout, errors are logged rather than thrown: these are background
/// reactions, and one unreachable package must not tear down the pass that serves the rest.</para>
/// </summary>
internal static class PackageUpdateReconciler
{
    /// <summary>
    /// Reconciles every <paramref name="candidates"/> entry that this installation actually has an
    /// install record for. Packages that are not installed here are skipped in silence — a catalog
    /// lists far more than any one instance installs, so that is the common case.
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="source">The source the candidates came from, used to fetch a delta.</param>
    /// <param name="sourceRef">The ref/sha the candidates were listed at.</param>
    /// <param name="candidates">The packages the source is currently serving.</param>
    /// <param name="provenance">One human phrase naming where this reconcile learned its facts,
    /// e.g. <c>"Built from a1b2c3d"</c> or <c>"Served by registry 'memex'"</c>. It ends up in the
    /// reminder a user reads, so it must say something a user can act on.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>Cold observable completing when every candidate has been considered.</returns>
    public static IObservable<Unit> ReconcileInstalled(
        IMessageHub hub,
        IPackageSource source,
        string sourceRef,
        IReadOnlyList<PackageManifest> candidates,
        string provenance,
        ILogger? logger)
    {
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null || candidates.Count == 0)
            return Observable.Return(Unit.Default);

        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();

        // SEQUENTIAL (Concat), not a fan-out: each apply writes a partition's worth of nodes and
        // may compile node types. The boot caller runs this on a cold pod, where a parallel
        // fan-out is how you saturate it — the same call InstanceAutoRegistrationService.InstallAll
        // already makes for the identical reason.
        return candidates
            .Select(pkg => ReconcileOne(
                hub, storage, meshService, accessService, source, sourceRef, pkg, provenance, logger))
            .ToObservable()
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .LastAsync();
    }

    private static IObservable<Unit> ReconcileOne(
        IMessageHub hub,
        IStorageAdapter storage,
        IMeshService meshService,
        AccessService accessService,
        IPackageSource source,
        string sourceRef,
        PackageManifest pkg,
        string provenance,
        ILogger? logger)
    {
        var recordPath = $"{PackageInstaller.InstalledPartition}/{pkg.Id}";
        return storage.Read(recordPath, hub.JsonSerializerOptions)
            .Take(1)
            .Select(n => n?.ContentAs<PackageManifest>(hub.JsonSerializerOptions))
            .Catch<PackageManifest?, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "Package update: reading install record {Path} failed; skipping {Id}.",
                    recordPath, pkg.Id);
                return Observable.Return<PackageManifest?>(null);
            })
            .SelectMany(record => record is null
                // Not installed here → nothing to remind anyone about.
                ? Observable.Return(Unit.Default)
                : Decide(hub, meshService, accessService, source, sourceRef, pkg, record, recordPath,
                    provenance, logger));
    }

    private static IObservable<Unit> Decide(
        IMessageHub hub,
        IMeshService meshService,
        AccessService accessService,
        IPackageSource source,
        string sourceRef,
        PackageManifest pkg,
        PackageManifest record,
        string recordPath,
        string provenance,
        ILogger? logger)
    {
        // 🚨 A MISSING hash on either side is not evidence of a change — it is the ABSENCE of
        // evidence, and the two must not be confused. Without a `manifest.lock` there is no content
        // identity to compare, so "has it changed" is unanswerable; answering "yes" would act on the
        // EVENT instead of the content, which is the one property this whole path is built on. Both
        // callers would be wrong in the same way: the webhook would re-install the module on every
        // green build of the repo (a doc-only commit elsewhere included), and the boot reconcile
        // would re-install it on every single pod start.
        //
        // So: refuse, and say so. A module with no content identity can never auto-update, and that
        // is an authoring defect in the module's CI (Doc/Architecture/PluginAuthoring) rather than
        // something to paper over here — logged at Warning precisely so it does not become a quiet
        // "my plugin never updates".
        //
        // 🚨 This is checked BEFORE the equality gate below, not after. `null == null` is *equal*,
        // so an equality-first order would silently absorb the commonest shape of this defect — both
        // sides missing a manifest.lock — into "nothing changed" and never say a word (Copilot catch).
        if (string.IsNullOrEmpty(pkg.ModuleVersion) || string.IsNullOrEmpty(record.ModuleVersion))
        {
            logger?.LogWarning(
                "Package update: {Id} has no module content identity ({Side} moduleVersion is empty), "
                + "so it cannot be reconciled and will NOT auto-update. Its CI must emit a "
                + "manifest.lock sidecar; until then the catalog card's manual Update is the only "
                + "path. {Provenance}",
                pkg.Id,
                string.IsNullOrEmpty(pkg.ModuleVersion) ? "the source's" : "the install record's",
                provenance);
            return Observable.Return(Unit.Default);
        }

        // 🚨 THE gate: content identity, not the event that woke us. Equal module hashes ⇒ nothing in
        // this module changed ⇒ stay completely silent — no notification, and not one file fetched.
        if (string.Equals(record.ModuleVersion, pkg.ModuleVersion, StringComparison.Ordinal))
            return Observable.Return(Unit.Default);

        var detail = Describe(pkg, record);

        logger?.LogInformation(
            "Package update: {Id} has an update ({Old} → {New}; {Detail}); autoUpdate={Auto}. {Provenance}",
            pkg.Id, record.ModuleVersion, pkg.ModuleVersion, detail, record.AutoUpdate, provenance);

        return record.AutoUpdate
            ? Apply(hub, meshService, accessService, source, sourceRef, pkg, record, recordPath,
                provenance, detail, logger)
            : Notify(
                meshService, accessService, recordPath, pkg,
                $"Update available: {pkg.Name ?? pkg.Id}",
                $"A new build of {pkg.Name ?? pkg.Id} is available ({detail}). {provenance}.",
                logger);
    }

    /// <summary>
    /// What actually changed, for the reminder's message — computed without re-fetching anything:
    /// the installed side is on the record (<c>InstalledFiles</c>), the candidate side rode in on
    /// the catalog entry (<c>ManifestFiles</c>).
    /// </summary>
    private static string Describe(PackageManifest pkg, PackageManifest record)
    {
        var changed = 0;
        var removed = 0;
        if (record.InstalledFiles is { Count: > 0 } installed && pkg.ManifestFiles is { Count: > 0 } candidate)
        {
            foreach (var (path, hash) in candidate)
                if (!installed.TryGetValue(path, out var old) || !string.Equals(old, hash, StringComparison.Ordinal))
                    changed++;
            foreach (var path in installed.Keys)
                if (!candidate.ContainsKey(path))
                    removed++;
        }

        return changed + removed > 0
            ? $"{changed} file(s) changed, {removed} removed"
            : "content changed";
    }

    /// <summary>
    /// Unattended install of a changed module — reached only for a record that opted in.
    ///
    /// <para>Runs as SYSTEM: there is no user behind a background reaction, and the install writes
    /// nodes. The record's <see cref="PackageManifest.AuthorizedBy"/> principal is what the
    /// commercial-entitlement gate re-checks (#830), so a revoked admin stops the syncing and a
    /// refusal surfaces as a notification rather than a silent skip.</para>
    /// </summary>
    private static IObservable<Unit> Apply(
        IMessageHub hub,
        IMeshService meshService,
        AccessService accessService,
        IPackageSource source,
        string sourceRef,
        PackageManifest pkg,
        PackageManifest record,
        string recordPath,
        string provenance,
        string detail,
        ILogger? logger)
    {
        logger?.LogInformation(
            "Package update: auto-updating {Id} to {Version} ({Detail}); authorized by {Principal}.",
            pkg.Id, pkg.ModuleVersion, detail, record.AuthorizedBy ?? "(nobody)");

        return Observable.Using(
                accessService.ImpersonateAsSystem,
                _ => CatalogLayoutAreas.InstallOrUpdate(
                    hub, source, sourceRef, pkg, logger, record.AuthorizedBy))
            .Do(result => logger?.LogInformation(
                "Package update: {Id} auto-updated ({Written} written, {Unchanged} unchanged).",
                pkg.Id, result.Written, result.Unchanged))
            .Select(_ => Unit.Default)
            .Catch((Exception ex) =>
            {
                if (ex is PackageAuthorizationException)
                {
                    logger?.LogWarning(
                        "Package update: auto-update of {Id} REFUSED — {Reason}", pkg.Id, ex.Message);
                    return Notify(
                        meshService, accessService, recordPath, pkg,
                        $"Update needs a Global Admin: {pkg.Name ?? pkg.Id}",
                        $"A new build of {pkg.Name ?? pkg.Id} is available ({detail}), but it is a "
                        + "commercial package and was not applied automatically. " + ex.Message,
                        logger);
                }

                logger?.LogWarning(ex,
                    "Package update: auto-update of {Id} failed; the card still offers a manual Update. {Provenance}",
                    pkg.Id, provenance);
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>Raises a system notification on the install record — the user-visible surface of an
    /// update reminder, and of a refusal, which must never be a silent skip.</summary>
    private static IObservable<Unit> Notify(
        IMeshService meshService,
        AccessService accessService,
        string recordPath,
        PackageManifest pkg,
        string title,
        string body,
        ILogger? logger)
        => Observable.Using(
                accessService.ImpersonateAsSystem,
                _ => NotificationService.CreateNotification(
                    meshService, recordPath, title, body,
                    NotificationType.System, targetNodePath: recordPath))
            .Select(_ => Unit.Default)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Package update: raising the notification for {Id} failed.", pkg.Id);
                return Observable.Return(Unit.Default);
            });
}
