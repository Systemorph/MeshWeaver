using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
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
    /// <param name="binding">🚨 What binds this pass to the install records' origins
    /// (MeshWeaver#6526): a candidate is taken only for a record installed from
    /// <see cref="InstallOriginBinding.Origin"/>, or for a record with no origin that this origin
    /// ADOPTS because no other watched origin lists the package. Null leaves the pass unbound — the
    /// registry lane, whose single configured registry is the consumer's trust root.</param>
    /// <returns>Cold observable completing when every candidate has been considered.</returns>
    public static IObservable<Unit> ReconcileInstalled(
        IMessageHub hub,
        IPackageSource source,
        string sourceRef,
        IReadOnlyList<PackageManifest> candidates,
        string provenance,
        ILogger? logger,
        InstallOriginBinding? binding = null)
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
                hub, storage, meshService, accessService, source, sourceRef, pkg, provenance, logger, binding))
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
        ILogger? logger,
        InstallOriginBinding? binding)
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
                    provenance, logger, binding));
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
        ILogger? logger,
        InstallOriginBinding? binding)
    {
        // 🚨 A LISTING without a hash is not evidence of a change — it is the ABSENCE of evidence,
        // and the two must not be confused. Without a `manifest.lock` the source states no content
        // identity, so "has it changed" is unanswerable; answering "yes" would act on the EVENT
        // instead of the content, which is the one property this whole path is built on: the webhook
        // would re-install the module on every green build of the repo (a doc-only commit elsewhere
        // included), and the boot reconcile on every single pod start.
        //
        // So: refuse, and say so. A module with no content identity can never auto-update, and that
        // is an authoring defect in the module's CI (Doc/Architecture/PluginAuthoring) rather than
        // something to paper over here — logged at Warning precisely so it does not become a quiet
        // "my plugin never updates".
        //
        // 🚨 This is checked BEFORE the equality gate below, not after. `null == null` is *equal*,
        // so an equality-first order would silently absorb the commonest shape of this defect — both
        // sides missing a manifest.lock — into "nothing changed" and never say a word (Copilot catch).
        if (string.IsNullOrEmpty(pkg.ModuleVersion))
        {
            logger?.LogWarning(
                "Package update: {Id} has no module content identity (the source's listing carries no "
                + "moduleVersion{RecordSide}), so it cannot be reconciled and will NOT auto-update. Its CI "
                + "must emit a manifest.lock sidecar; until then the catalog card's manual Update is the "
                + "only path. {Provenance}",
                pkg.Id,
                string.IsNullOrEmpty(record.ModuleVersion) ? ", and neither does the install record" : "",
                provenance);
            return Observable.Return(Unit.Default);
        }

        // 🚨 The OTHER side missing is a different case and is NOT refused (2026-10-15). A record
        // installed before its repository's CI emitted a manifest.lock carries no moduleVersion,
        // while the listing now states one. The record therefore cannot claim to EQUAL the listed
        // content — what it holds is unknown — so the listed identity is a change: an Auto record
        // installs unattended (which stamps the moduleVersion, so the next pass compares normally
        // and stays silent), a Notify record is reminded once (NotifiedModuleVersion), a pinned one
        // is left alone. Refusing here left every package installed before its repo emitted locks
        // stuck forever behind a manual click — measured on an enterprise client's test instance,
        // where three packages logged "the install record's moduleVersion is empty" on every
        // green build after their repository started committing locks.
        if (string.IsNullOrEmpty(record.ModuleVersion))
            logger?.LogInformation(
                "Package update: {Id}'s install record carries no moduleVersion (installed before its "
                + "source emitted a manifest.lock); the listed {Candidate} is taken as a change. {Provenance}",
                pkg.Id, pkg.ModuleVersion, provenance);

        // 🚨 THE gate: content identity, not the event that woke us. Equal module hashes ⇒ nothing in
        // this module changed ⇒ stay completely silent — no notification, and not one file fetched.
        if (string.Equals(record.ModuleVersion, pkg.ModuleVersion, StringComparison.Ordinal))
            return Observable.Return(Unit.Default);

        // 🚨 THE ORIGIN gate (MeshWeaver#6526), before anything is written or told: a candidate from
        // a repository the package was not installed from is not an update of THIS install. After
        // the equality gate, so an unchanged module never costs a census of the watched origins;
        // before the floor gate, so a foreign candidate cannot even stamp a hold on the record.
        if (binding is null)
            return DecideBound(hub, meshService, accessService, source, sourceRef, pkg, record, recordPath,
                provenance, logger);
        // A pinned package is skipped on a bound pass, BEFORE the floor gate: nothing moves for it, so
        // nothing is bound, refused, held or dispatched. Handing it to DecideBound would let a candidate
        // from ANY watched repository — a foreign one included — stamp or clear HeldUpdate and dispatch
        // a blocking ticket for a package the administrator pinned (review on MeshWeaver#6541).
        if (record.EffectiveUpdatePolicy == PackageUpdatePolicy.None)
        {
            logger?.LogInformation(
                "Package update: {Id} is pinned; the candidate {Candidate} listed by {Origin} is not decided "
                + "(nothing moves for a pinned package). {Provenance}",
                pkg.Id, pkg.ModuleVersion, binding.Origin, provenance);
            return Observable.Return(Unit.Default);
        }
        return BindOrigin(hub, meshService, accessService, pkg, record, recordPath, binding, logger)
            .SelectMany(bound => bound is null
                ? Observable.Return(Unit.Default)
                : DecideBound(hub, meshService, accessService, source, sourceRef, pkg, bound, recordPath,
                    provenance, logger));
    }

    /// <summary>
    /// 🚨 Binds one candidate to the install record's origin (MeshWeaver#6526,
    /// <see cref="InstallOrigin.Decide"/>). Emits the record to decide on — with its origin ADOPTED
    /// when this is the only watched origin listing the package — or null when the candidate must
    /// not be taken: a foreign origin (ignored), two or more listing origins (refused, by name, on
    /// the platform bell), or an incomplete census (neither, this pass).
    /// </summary>
    private static IObservable<PackageManifest?> BindOrigin(
        IMessageHub hub,
        IMeshService meshService,
        AccessService accessService,
        PackageManifest pkg,
        PackageManifest record,
        string recordPath,
        InstallOriginBinding binding,
        ILogger? logger)
    {
        var census = string.IsNullOrEmpty(record.Origin)
            ? binding.Census(pkg.Id)
                .Take(1)
                .Select(c => (OriginCensus?)c)
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex,
                        "Package update: which watched repositories list {Id} could not be read.", pkg.Id);
                    return Observable.Return<OriginCensus?>(null);
                })
                .DefaultIfEmpty(null)
            : Observable.Return<OriginCensus?>(null);

        return census.SelectMany(c =>
        {
            switch (InstallOrigin.Decide(record.Origin, binding.Origin, c))
            {
                case OriginVerdict.Bound:
                    return Observable.Return<PackageManifest?>(record);

                case OriginVerdict.Foreign:
                    logger?.LogInformation(
                        "Package update: {Id} was installed from {Recorded}; the candidate {Candidate} listed by "
                        + "{Origin} is ignored (MeshWeaver#6526).",
                        pkg.Id, record.Origin, pkg.ModuleVersion, binding.Origin);
                    return Observable.Return<PackageManifest?>(null);

                case OriginVerdict.Adopt:
                    logger?.LogInformation(
                        "Package update: {Id} records no origin; {Origin} is the only watched repository "
                        + "listing it, so the record adopts it (MeshWeaver#6526).", pkg.Id, binding.Origin);
                    return AdoptOrigin(hub, accessService, recordPath, record, binding.Origin, logger);

                case OriginVerdict.Ambiguous:
                    var candidates = c!.Listing.Add(binding.Origin);
                    var listed = string.Join(", ", candidates);
                    var name = pkg.Name ?? pkg.Id;
                    logger?.LogWarning(
                        "Package update: {Id} records no origin and {Count} watched repositories list it "
                        + "({Origins}); the update is REFUSED until an administrator installs it from one "
                        + "of them (MeshWeaver#6526).", pkg.Id, candidates.Count, listed);
                    return Notify(
                            hub, meshService, accessService, recordPath, pkg, record, OriginAmbiguousKind,
                            LocalizableText.Keyed(
                                $"Update refused: {name}",
                                "notification.packageUpdate.originAmbiguous.title", ("name", name)),
                            LocalizableText.Keyed(
                                $"A new build of {name} is available, but this install does not record "
                                + $"which repository it came from, and {candidates.Count} watched "
                                + $"repositories list it: {listed}. It was NOT applied. Install it again "
                                + "from the repository it belongs to; later updates then come from there only.",
                                "notification.packageUpdate.originAmbiguous.body",
                                ("name", name), ("count", candidates.Count), ("origins", listed)),
                            logger)
                        .Select(_ => (PackageManifest?)null);

                default:
                    logger?.LogWarning(
                        "Package update: {Id} records no origin, and not every watched repository could be "
                        + "read ({Unread}); it is neither adopted nor updated from {Origin} this pass "
                        + "(MeshWeaver#6526).",
                        pkg.Id, c is null ? "the census failed" : string.Join(", ", c.Unread), binding.Origin);
                    return Observable.Return<PackageManifest?>(null);
            }
        });
    }

    /// <summary>Records <paramref name="origin"/> on an install record that named none, and hands
    /// the decision the record AS WRITTEN. Only an EMPTY origin is filled: a concurrent install that
    /// stamped one first wins — and then the record names a different origin, so this candidate is
    /// not bound to it and the pass takes nothing (the next pass decides against the live record).</summary>
    private static IObservable<PackageManifest?> AdoptOrigin(
        IMessageHub hub, AccessService accessService, string recordPath, PackageManifest record,
        string origin, ILogger? logger)
    {
        // RunAsSystem, never Observable.Using (#1790): the install-records partition is System-owned.
        return accessService.RunAsSystem(() => hub.GetMeshNodeStream(recordPath)
                .Update<PackageManifest>(current => string.IsNullOrEmpty(current.Origin)
                    ? current with { Origin = origin }
                    : current))
            .Take(1)
            // 🚨 The record the update EMITTED, never the pre-write snapshot: a concurrent install or
            // policy edit may have won, and deciding on the snapshot would act on stale policy and
            // authorization fields — or bind a candidate to an origin the record no longer names
            // (review on MeshWeaver#6541).
            .Select(node => node.ContentAs<PackageManifest>(hub.JsonSerializerOptions))
            .Select(current =>
            {
                if (current is not null && string.Equals(current.Origin, origin, StringComparison.Ordinal))
                    return current;
                logger?.LogInformation(
                    "Package update: {Path} was not adopted for {Origin} — the record now names {Recorded}; "
                    + "nothing is taken this pass (MeshWeaver#6526).",
                    recordPath, origin, current?.Origin ?? "(unreadable)");
                return null;
            })
            // An update that answered nothing proves no adoption: take nothing this pass.
            .DefaultIfEmpty(null)
            .Catch((Exception ex) =>
            {
                // Not adopted ⇒ not bound ⇒ not updated this pass; the next pass adopts again.
                logger?.LogWarning(ex, "Package update: recording the origin of {Path} failed.", recordPath);
                return Observable.Return<PackageManifest?>(null);
            });
    }

    /// <summary>The floor and policy half of <see cref="Decide"/> — reached once the candidate is
    /// bound to the record's origin, or the pass is unbound.</summary>
    private static IObservable<Unit> DecideBound(
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
        // 🚨 policy package-min-mesh-version — the candidate is USABLE here only if the running
        // platform satisfies its declared floor. A held candidate is neither applied nor offered:
        // the installed version keeps running (R1), the record says why, and the next reconcile
        // after the platform rolls applies it. Before the policy switch, so no lane reminds an
        // administrator of an update this platform cannot run (2026-09-27: Store 1.16 and Hosting
        // synced onto 3.0.0-ci.9412/9414 and left 14 NodeTypes with no usable assembly).
        var floor = PackagePlatformFloorGate.Evaluate(hub, pkg);
        if (floor.IsHeld)
            return PackagePlatformFloorGate.RecordHold(hub, pkg, record, floor, logger);
        if (floor.Kind == PlatformFloorKind.Advisory)
            logger?.LogInformation("Package update: {Id} {Advisory}", pkg.Id, floor.Reason);

        // 🚨 The floor is MET now. A record still saying "held: … updates when the platform rolls"
        // is a lie from here on — measured on the control instance 2026-10-04: Plugins/Hosting and
        // 22 others kept "running 3.0.0-ci.9885" after the roll to 9887, and the fleet's only
        // record of a hold read as current. The decision below may still hold the package for
        // ANOTHER reason (sync-owned content), and then it says so in its own words.
        // Only a FLOOR hold ends because the floor is met; a hold for another reason is re-decided by
        // the lane that stamped it and keeps its since-when (review on MeshWeaver#6065).
        var proceed = PackagePlatformFloorGate.IsFloorHold(record.HeldUpdate)
            ? ClearFloorHold(hub, accessService, recordPath, record, logger)
            : Observable.Return(record);
        return proceed.SelectMany(current => DecidePolicy(
            hub, meshService, accessService, source, sourceRef, pkg, current, recordPath, provenance, logger));
    }

    /// <summary>The policy half of <see cref="Decide"/> — reached only once the floor is met.</summary>
    private static IObservable<Unit> DecidePolicy(
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
        var delta = DescribeDelta(pkg, record);
        var detail = Describe(delta);
        var name = pkg.Name ?? pkg.Id;

        var policy = record.EffectiveUpdatePolicy;
        logger?.LogInformation(
            "Package update: {Id} has an update ({Old} → {New}; {Detail}); updatePolicy={Policy}. {Provenance}",
            pkg.Id, record.ModuleVersion, pkg.ModuleVersion, detail, policy, provenance);

        // 🚨 MeshWeaver#6192 — a candidate whose declared requirement the LOADED dependency does not
        // meet is neither applied nor offered on the lanes where THIS lane would write or remind: the
        // installed version keeps running and the record says which dependency has to be loaded. A
        // sync-owned partition is not gated here — its sync source applies the same rule before it
        // writes (ModuleSyncDecision.DeclineUnmetRequirements), and a record must adopt what that
        // owner has already landed.
        IObservable<Unit> UnlessADependencyHolds(Func<PackageManifest, IObservable<Unit>> proceed) =>
            PackageDependencyFloorGate.Evaluate(hub, pkg, logger).SelectMany(unmet => unmet is null
                ? PackageDependencyFloorGate.ClearMetHold(hub, record, logger).SelectMany(proceed)
                : PackageDependencyFloorGate.RecordHold(hub, pkg, record, unmet, logger));

        // The PACKAGE's own policy decides — never the platform's image policy (Admin/UpdatePolicy),
        // which governs the image roll alone since 2026-09-14.
        return policy switch
        {
            // 🚨 …and the policy decides only whether this lane WANTS to write. Whether it MAY is a
            // second question, and #4355 is what it cost to leave unasked: a partition whose content
            // a sync source keeps at the sealed commit has an owner already, and this lane writing
            // into it is a second writer with its own books. The apply is therefore gated on
            // ownership and DEGRADES TO THE REMINDER — never a silent skip, and never a write that
            // lands sources this instance has no proven bundle for. Only the unattended apply is
            // gated: a human clicking Update is the documented escape (Doc/Architecture/SyncRefContract
            // draws the same line), and it goes through the full install, never a delta.
            //
            // The CANDIDATE's target partition, because that is where this apply's writes would
            // land — not the record's, which is the partition an existing baseline describes and is
            // the question CatalogLayoutAreas asks. They are the same for every package that does
            // not move partitions, and where they differ each side asks about the one it means.
            PackageUpdatePolicy.Auto => PartitionContentOwnership
                .Observe(hub, PackageInstaller.TargetPartitionOf(pkg.Id, pkg), pkg.Id, logger)
                .SelectMany(ownership => ownership.InstallerOwnsTheContent
                    ? UnlessADependencyHolds(current => Apply(
                        hub, meshService, accessService, source, sourceRef, pkg, current,
                        recordPath, provenance, delta, logger))
                    // 🚨 Sync-owned — but has the sync ALREADY landed this very content? Then there
                    // is nothing to write and no second writer: the record adopts the candidate and
                    // the module lane (RegistryUpdateReconciler.AdoptOne) lands its code in the same
                    // pass. This is the convergence the 10-03/10-04 holds lacked (maintainer,
                    // 2026-10-04: "a module held because _GitSync owns the partition must also
                    // converge, without a manual step").
                    : ownership.Owner == PartitionContentOwner.SyncSource
                        ? PartitionContentOwnership
                            .SyncedModules(hub, PackageInstaller.TargetPartitionOf(pkg.Id, pkg))
                            .SelectMany(synced => PartitionContentOwnership.SyncDelivered(ownership, synced, pkg.Id, pkg.ModuleVersion)
                                ? AdoptSyncedContent(hub, accessService, recordPath, pkg, logger)
                                : HoldForSync(hub, meshService, accessService, recordPath, pkg, record,
                                    ownership, synced, name, detail, delta, provenance, logger))
                        : Notify(
                        hub, meshService, accessService, recordPath, pkg, record, SyncOwnedPartitionKind,
                        LocalizableText.Keyed(
                            $"Update held: {name}",
                            "notification.packageUpdate.held.title", ("name", name)),
                        LocalizableText.Keyed(
                            $"A new build of {name} is available ({detail}), and this package "
                            + "is set to update automatically — but it was NOT applied. "
                            + ownership.Because + ". Applying it here would leave two writers with "
                            + "separate records of one partition (MeshWeaver#4355). " + provenance + ".",
                            BodyKey("held", delta),
                            ("name", name), ("changed", delta?.Changed), ("removed", delta?.Removed),
                            ("because", ownership.Because), ("provenance", provenance)),
                        logger)),
            PackageUpdatePolicy.Notify => UnlessADependencyHolds(current => Notify(
                hub, meshService, accessService, recordPath, pkg, current, UpdateAvailableKind,
                LocalizableText.Keyed(
                    $"Update available: {name}",
                    "notification.packageUpdate.available.title", ("name", name)),
                LocalizableText.Keyed(
                    $"A new build of {name} is available ({detail}). {provenance}.",
                    BodyKey("available", delta),
                    ("name", name), ("changed", delta?.Changed), ("removed", delta?.Removed),
                    ("provenance", provenance)),
                logger)),
            // Pinned: the update exists, the log says so, and nothing else moves — no notification
            // either, because the administrator chose not to be reminded.
            _ => Observable.Return(Unit.Default),
        };
    }

    /// <summary>
    /// The floor that held an update is met: clear the hold on the record (sentence, dispatch,
    /// stamps) and hand the decision the CURRENT record. Never faults — a stamp that could not be
    /// written costs one stale line, never the update itself.
    /// </summary>
    private static IObservable<PackageManifest> ClearFloorHold(
        IMessageHub hub, AccessService accessService, string recordPath, PackageManifest record, ILogger? logger)
    {
        logger?.LogInformation(
            "Package update: {Id} — the platform now satisfies the floor that held it ({Held}); the hold "
            + "is cleared and the update is decided again.", record.Id, record.HeldUpdate);
        var cleared = record with
        {
            HeldUpdate = null,
            HeldUpdateDispatch = null,
            HeldUpdateDispatchedAt = null,
            HeldSince = null,
        };
        // RunAsSystem, never Observable.Using (#1790): the install-records partition is System-owned.
        return accessService.RunAsSystem(() => hub.GetMeshNodeStream(recordPath)
                .Update<PackageManifest>(current => current with
                {
                    HeldUpdate = null,
                    HeldUpdateDispatch = null,
                    HeldUpdateDispatchedAt = null,
                    HeldSince = null,
                }))
            .Take(1)
            .Select(_ => cleared)
            .DefaultIfEmpty(cleared)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Package update: clearing the met floor hold on {Path} failed.", recordPath);
                return Observable.Return(cleared);
            });
    }

    /// <summary>
    /// 🚨 <b>The convergence of a SYNC-OWNED package</b> (maintainer, 2026-10-04). The partition's
    /// sync source has landed exactly the candidate's content, so nothing is written to the
    /// partition — the record ADOPTS the candidate (version, content hash, file map, floor) and
    /// any hold clears. One writer stays one writer: the installer only brings its own books in
    /// line with what the owner landed, which is the repair One Partition, One Bookkeeping asks of
    /// every lane ("whichever writer lands content must leave the other writer's record
    /// consistent"). The module lane that follows in the same pass then lands the module whose
    /// content this is (<c>RegistryUpdateReconciler.AdoptOne</c>).
    /// </summary>
    internal static IObservable<Unit> AdoptSyncedContent(
        IMessageHub hub, AccessService accessService, string recordPath, PackageManifest pkg, ILogger? logger)
    {
        logger?.LogInformation(
            "Package update: {Id} — its partition's sync source has already landed {Version} ({Hash}); "
            + "the install record adopts it (no content written) and its module lands in this pass.",
            pkg.Id, pkg.ReleasedVersion ?? pkg.Version, pkg.ModuleVersion);
        return accessService.RunAsSystem(() => hub.GetMeshNodeStream(recordPath)
                .Update<PackageManifest>(current => AdoptedFromSync(current, pkg, DateTimeOffset.UtcNow)))
            .Take(1)
            .Select(_ => Unit.Default)
            .DefaultIfEmpty(Unit.Default)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Package update: adopting the synced content of {Id} on {Path} failed.",
                    pkg.Id, recordPath);
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>The record after adopting a candidate the sync has landed (pure). Policy, the
    /// authorizing principal and every other install-time field are kept.</summary>
    /// <param name="current">The record as it stands.</param>
    /// <param name="pkg">The candidate.</param>
    /// <param name="now">Now.</param>
    internal static PackageManifest AdoptedFromSync(PackageManifest current, PackageManifest pkg, DateTimeOffset now) =>
        current with
        {
            Version = pkg.Version ?? current.Version,
            ReleasedVersion = pkg.ReleasedVersion ?? current.ReleasedVersion,
            ModuleVersion = pkg.ModuleVersion,
            MinMeshVersion = pkg.MinMeshVersion,
            InstalledFiles = pkg.ManifestFiles ?? current.InstalledFiles,
            InstalledAtUtc = now,
            NotifiedModuleVersion = pkg.ModuleVersion,
            HeldUpdate = null,
            HeldUpdateDispatch = null,
            HeldUpdateDispatchedAt = null,
            HeldSince = null,
        };

    /// <summary>The hold sentence for a sync-owned package whose sync has NOT landed the
    /// candidate yet (pure) — what the record and the fleet view say instead of a silent reminder.</summary>
    /// <param name="pkg">The candidate.</param>
    /// <param name="synced">What the partition's sync sources have landed.</param>
    /// <param name="partition">The target partition.</param>
    internal static string SyncHoldSentence(PackageManifest pkg, SyncedModuleVersions synced, string partition)
    {
        var version = pkg.ReleasedVersion ?? pkg.Version ?? pkg.ModuleVersion ?? "the served version";
        var landed = !synced.Known
            ? "an unknown version (its sync source could not be read)"
            : synced.Versions.TryGetValue(pkg.Id, out var hash) ? hash : "nothing for this module";
        return $"held: {version} — '{partition}' is kept by its sync source, which has landed {landed}, "
            + $"not {pkg.ModuleVersion}; converges when the sync lands it (the seal for this platform)";
    }

    /// <summary>
    /// A sync-owned package whose sync has NOT landed the candidate: stamp the reason on the record
    /// (so the fleet view lists it, with since-when) and raise the once-per-candidate reminder.
    /// </summary>
    private static IObservable<Unit> HoldForSync(
        IMessageHub hub,
        IMeshService meshService,
        AccessService accessService,
        string recordPath,
        PackageManifest pkg,
        PackageManifest record,
        PartitionContentOwnershipVerdict ownership,
        SyncedModuleVersions synced,
        string name,
        string detail,
        (int Changed, int Removed)? delta,
        string provenance,
        ILogger? logger)
    {
        var sentence = SyncHoldSentence(pkg, synced, ownership.Partition);
        var stamp = string.Equals(record.HeldUpdate, sentence, StringComparison.Ordinal)
            ? Observable.Return(Unit.Default)
            : accessService.RunAsSystem(() => hub.GetMeshNodeStream(recordPath)
                    .Update<PackageManifest>(current => current with
                    {
                        HeldUpdate = sentence,
                        HeldSince = current.HeldSince ?? DateTimeOffset.UtcNow,
                    }))
                .Take(1)
                .Select(_ => Unit.Default)
                .DefaultIfEmpty(Unit.Default)
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex, "Package update: stamping the sync hold on {Path} failed.", recordPath);
                    return Observable.Return(Unit.Default);
                });
        return stamp.SelectMany(_ => Notify(
            hub, meshService, accessService, recordPath, pkg, record with { HeldUpdate = sentence }, SyncOwnedPartitionKind,
            LocalizableText.Keyed(
                $"Update held: {name}",
                "notification.packageUpdate.held.title", ("name", name)),
            LocalizableText.Keyed(
                $"A new build of {name} is available ({detail}), and this package "
                + "is set to update automatically — but it was NOT applied. "
                + ownership.Because + ". Applying it here would leave two writers with "
                + "separate records of one partition (MeshWeaver#4355). " + provenance + ".",
                BodyKey("held", delta),
                ("name", name), ("changed", delta?.Changed), ("removed", delta?.Removed),
                ("because", ownership.Because), ("provenance", provenance)),
            logger));
    }

    /// <summary>
    /// What actually changed, for the reminder's message — computed without re-fetching anything:
    /// the installed side is on the record (<c>InstalledFiles</c>), the candidate side rode in on
    /// the catalog entry (<c>ManifestFiles</c>). <c>null</c> when the file-level delta cannot be
    /// told (one side carries no file list), which is a DIFFERENT sentence, not a zero.
    /// </summary>
    private static (int Changed, int Removed)? DescribeDelta(PackageManifest pkg, PackageManifest record)
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

        return changed + removed > 0 ? (changed, removed) : null;
    }

    /// <summary>The English rendering of <see cref="DescribeDelta"/> — the log line, and the
    /// fallback half of the reminder's localizable body.</summary>
    private static string Describe((int Changed, int Removed)? delta)
        => delta is { } d ? $"{d.Changed} file(s) changed, {d.Removed} removed" : "content changed";

    /// <summary>
    /// The catalog key for a reminder body. 🚨 TWO keys per kind, selected by whether the
    /// file-level delta is known — never one key taking a composed <c>{detail}</c> argument: an
    /// English clause spliced into a translated sentence lands as English inside German word order
    /// (Doc/Architecture/Localization, "a conditional clause gets its own key").
    /// </summary>
    private static string BodyKey(string kind, (int Changed, int Removed)? delta)
        => $"notification.packageUpdate.{kind}.body{(delta is null ? "Content" : "Files")}";

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
        (int Changed, int Removed)? delta,
        ILogger? logger)
    {
        var detail = Describe(delta);
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
                    var name = pkg.Name ?? pkg.Id;
                    return Notify(
                        hub, meshService, accessService, recordPath, pkg, record,
                        AuthorizationRequiredKind,
                        LocalizableText.Keyed(
                            $"Update needs a Global Admin: {name}",
                            "notification.packageUpdate.authorizationRequired.title", ("name", name)),
                        LocalizableText.Keyed(
                            $"A new build of {name} is available ({detail}), but it is a "
                            + "commercial package and was not applied automatically. " + ex.Message,
                            BodyKey("authorizationRequired", delta),
                            ("name", name), ("changed", delta?.Changed), ("removed", delta?.Removed),
                            ("reason", ex.Message)),
                        logger);
                }

                logger?.LogWarning(ex,
                    "Package update: auto-update of {Id} failed; the card still offers a manual Update. {Provenance}",
                    pkg.Id, provenance);
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>The reminder that a changed module is waiting for the user to act.</summary>
    private const string UpdateAvailableKind = "update-available";

    /// <summary>The refusal that an unattended apply needs a global admin to re-authorize it.</summary>
    private const string AuthorizationRequiredKind = "authorization-required";

    /// <summary>
    /// The hold that an unattended apply declined because the target partition's content is kept by
    /// a sync source, not by the installer (<see cref="PartitionContentOwnership"/>, #4355). A
    /// distinct kind so it is idempotent per candidate on its OWN marker and is never folded into
    /// the plain "update available" reminder — the two say different things to an administrator:
    /// one is "press Update", the other is "this package will not auto-update here, and why".
    /// </summary>
    private const string SyncOwnedPartitionKind = "sync-owned-partition";

    /// <summary>
    /// The refusal that an install record naming no origin cannot be bound, because two or more
    /// watched repositories list the package (MeshWeaver#6526). Its own kind: it says "install it
    /// again from the right repository", never "press Update".
    /// </summary>
    private const string OriginAmbiguousKind = "origin-ambiguous";

    /// <summary>
    /// Raises a system notification on the install record — the user-visible surface of an update
    /// reminder, and of a refusal, which must never be a silent skip.
    ///
    /// <para>🚨 <b>A reminder is a STATE, not an event, and this method is what makes it behave
    /// like one</b> (Systemorph/MeshWeaver#3213). The reconciler re-decides on every poll, so
    /// without the two guards below one package with a pending update mints a new bell row per
    /// poll, forever — measured on memex-cloud as 124 of the newest 200 notifications, two
    /// packages accounting for over half of everything the mesh had written in four days. Both
    /// guards are needed, and each one alone still duplicates:</para>
    ///
    /// <para>🚨 <b>Addressed to the PLATFORM</b> (<see cref="NotificationService.PlatformAddressee"/>),
    /// not to the install record's partition. Only a platform admin can apply a plugin update — the
    /// refusal this same method raises says so in as many words ("Update needs a Global Admin") — so
    /// an ordinary reader of the catalog can do nothing with it. Before the addressed model it landed
    /// at <c>Plugins/{package}/_Notification/{id}</c>, visible to everyone who could read the plugin
    /// record (Systemorph/MeshWeaver#3156 §2). The install record stays the CLICK TARGET, which is
    /// what <c>targetNodePath</c> carries.</para>
    /// <list type="number">
    ///   <item><b>A satisfiable silence gate.</b> The content-identity gate in
    ///     <see cref="Decide"/> asks "is the candidate installed?" — self-silencing on the
    ///     <c>AutoUpdate</c> path (the apply advances the record) and UNSATISFIABLE here, because
    ///     the reminder installs nothing by design. So the notify path carries its own marker,
    ///     <see cref="PackageManifest.NotifiedModuleVersion"/>, which answers the question it
    ///     actually needs: "have I already told them about THIS candidate?". Writing it is what
    ///     makes the gate become true — which is precisely what the version comparison could
    ///     never do here.</item>
    ///   <item><b>A deterministic identity to be idempotent on.</b> Marker or no marker, the
    ///     reconcile runs from several entry points (boot, a feed read draining a deferral, a
    ///     build webhook) that can overlap, and the marker write can fail. Keying the notification
    ///     on (record, kind, candidate version) makes any such repeat land on the SAME node as an
    ///     idempotent upsert instead of a new row, so the worst case is a refreshed bell rather
    ///     than an unbounded one.</item>
    /// </list>
    ///
    /// <para>The gate sits HERE and not before <see cref="Apply"/>: an unattended apply must keep
    /// retrying every poll so a restored admin grant heals itself. It is only the TELLING that is
    /// once per candidate.</para>
    /// </summary>
    private static IObservable<Unit> Notify(
        IMessageHub hub,
        IMeshService meshService,
        AccessService accessService,
        string recordPath,
        PackageManifest pkg,
        PackageManifest record,
        string kind,
        LocalizableText title,
        LocalizableText body,
        ILogger? logger)
    {
        // Decide() has already refused an empty candidate hash, so this is a real content identity.
        var candidate = pkg.ModuleVersion!;

        // ── Guard 1: already told them about this exact candidate ⇒ stay silent, write nothing ──
        if (string.Equals(record.NotifiedModuleVersion, candidate, StringComparison.Ordinal))
            return Observable.Return(Unit.Default);

        // RunAsSystem, never Observable.Using (#1790): the install-records partition is
        // System-owned, and Rx would otherwise leave the subscribing thread latched as System.
        return accessService
            .RunAsSystem(() => NotificationService
                // ── Guard 2: one node per (record, kind, candidate) ──────────────────────────
                .CreateLocalizableNotification(
                    meshService, recordPath, title, body,
                    NotificationType.System, targetNodePath: recordPath,
                    // Addressed to the PLATFORM: only a platform admin can apply a plugin
                    // update, so an ordinary catalog reader can do nothing with this. The
                    // install record stays the click target (targetNodePath above).
                    recipient: NotificationService.PlatformAddressee,
                    identity: $"package-update|{kind}|{candidate}")
                // Mark AFTER the bell, never before: a marker written first and a notification
                // that then failed would silence the reminder permanently, whereas a bell written
                // first and a marker that then failed costs one repeat — which guard 2 absorbs
                // into the same node.
                //
                // The TYPED overload, and the lambda reads the CURRENT record rather than the
                // snapshot this pass started from: the write is an RFC 7396 merge patch the owning
                // hub applies to its own state, so only `notifiedModuleVersion` travels and a
                // concurrent install's fields are not clobbered.
                .SelectMany(_ => hub.GetMeshNodeStream(recordPath)
                    .Update<PackageManifest>(current => current with { NotifiedModuleVersion = candidate })))
            .Select(_ => Unit.Default)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Package update: raising the notification for {Id} failed.", pkg.Id);
                return Observable.Return(Unit.Default);
            });
    }
}
