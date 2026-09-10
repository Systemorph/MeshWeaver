using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Reconciles every ALREADY-installed package, once, at startup — the REPAIR pass: re-runs the
/// <see cref="IPartitionInstallHook"/>s AND re-asserts the access shape each package's recorded
/// manifest declares (<see cref="PackageInstaller.EnsureDeclaredAccess"/>).
///
/// <para><b>Why a repair pass is needed at all.</b> Hooks now run on install, but every package
/// installed before they existed never ran them, and every account created since inherited the gap.
/// On a live instance that is the entire population: agents shipped by a package are present in the
/// mesh and invisible in every picker. Waiting for the next package update to fix it would leave
/// users broken for however long that takes, for a defect they cannot see or work around.</para>
///
/// <para><b>Why the access re-assert lives here too.</b> The boot install pass
/// (<see cref="InstanceAutoRegistrationService"/>) only revisits the pre-installed baseline and — on
/// a FRESH instance — the operator's default seed; a free package installed any other way (the seed
/// on an earlier boot, the catalog button, an update) is never a candidate again, so a lost policy
/// or grant would stay lost forever. The install records are the one complete inventory, and each
/// record carries the manifest whose declarations drive the shape — so "re-asserted on boot, a lost
/// policy self-heals" holds for EVERY installed package, not just the baseline (#920).</para>
///
/// <para><b>Why the RECORDS partition is published first.</b> Step 0 is
/// <see cref="PackageInstaller.EnsureRecordsPartitionReadable"/> — the durable
/// <c>Plugins/_Policy</c> that the SQL permission fold projects into
/// <c>public.partition_access</c>. It runs BEFORE the record listing below, and unconditionally,
/// for two reasons. First, an instance that has never installed anything still needs its records
/// partition readable — a registry serving bundles is exactly such an instance. Second, and this is
/// the trap: the listing is itself a partition-scoped query, so while the partition is invisible it
/// returns ZERO records and this whole pass exits early. A heal placed after it could never fire on
/// the instance that needed it (#1950).</para>
///
/// <para><b>Safe to run every boot.</b> Hooks are required to be idempotent, and the access shape is
/// create-only — so on an already-consistent instance this reads and writes nothing. It is
/// deliberately fire-and-forget and failure-tolerant: repair must never delay or fail startup (the
/// hard failure-propagation contract lives on the install paths themselves).</para>
/// </summary>
/// <param name="hub">Hub supplying the workspace and the registered hooks.</param>
public sealed class InstalledPackageRepairService(IMessageHub hub) : IHostedService
{
    private IDisposable? subscription;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger<InstalledPackageRepairService>();

        subscription = PackageInstaller.EnsureRecordsPartitionReadable(hub, logger)
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[PackageRepair] publishing the install-records partition failed — catalog "
                    + "surfaces and the bundle index may read empty until the next boot");
                return Observable.Return(Unit.Default);
            })
            .SelectMany(_ => InstalledRecords(logger))
            .SelectMany(records => (records.Count == 0
                    ? Observable.Return(Unit.Default)
                    : records
                        .Select(record => Reconcile(record, logger))
                        .ToObservable()
                        .Concat()
                        .DefaultIfEmpty(Unit.Default)
                        .LastAsync()
                        .Do(_ => logger?.LogInformation(
                            "[PackageRepair] reconciled declared access + install hooks for {Count} "
                            + "installed partition(s)", records.Count)))
                .Do(_ => ReportDeclaredModulesWithNoBinary(records, logger))
                .SelectMany(_ => VerifyCompleteness(records, logger)))
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "[PackageRepair] repair pass failed"));

        return Task.CompletedTask;
    }

    /// <summary>
    /// 🚨 <b>Say when an installed package's DECLARED MODULE has no binary here — the half of a
    /// mixed package that goes missing in total silence (Systemorph/MeshWeaver.Plugins#1597).</b>
    ///
    /// <para>The sibling of the <c>[InstallCompleteness]</c> lines below, asking the same question
    /// about the OTHER half of the package. Those verdicts count the NODES an install owed the
    /// mesh; a package that also declares <c>content.module</c> owes it a compiled assembly too,
    /// and nothing counted that. So the record read up to date, the guide page rendered, and every
    /// layout area the module serves answered <i>Area not found</i> — measured on a
    /// <c>memex-local</c> self-registry install where <c>Export</c> was installed and
    /// <c>MeshWeaver.Markdown.Export.dll</c> was in neither <c>/app</c> nor <c>/app/modules</c>,
    /// because a mounted checkout can never land a module BINARY (Systemorph/MeshWeaver#2417).</para>
    ///
    /// <para><b>The probe is the boot loader's own resolution</b>, not a re-derivation:
    /// <see cref="MeshBuilder.ResolveModulePath(string,string?)"/> (landed root → image
    /// <c>modules/</c> → app closure) and then the activation sidecar, so a bundle that HAS landed
    /// as a generation and is merely waiting for a restart is not reported as absent. A report that
    /// fired on the normal minutes after an install would be one nobody reads by the second week.</para>
    ///
    /// <para>🚨 <b>One line, warning, never a failure.</b> It cannot fail the boot: a portal that
    /// will not start cannot be given the module it is missing — the same deadlock
    /// <see cref="ModuleLoadReport"/> refuses to create, and the reason <c>Modules:Required</c> is
    /// the wrong home for this (a required module that cannot land fails readiness and the portal
    /// never serves at all).</para>
    /// </summary>
    private void ReportDeclaredModulesWithNoBinary(
        IReadOnlyList<InstalledRecord> records, ILogger? logger)
    {
        // 🚨 No configuration ⇒ no module root ⇒ nothing is KNOWN, and the report says nothing
        // rather than clearing every package. Same rule as the null probe on BinaryAbsent.
        if (hub.ServiceProvider.GetService<IConfiguration>() is not { } configuration)
            return;

        var moduleRoot = ModuleRoot.Resolve(configuration);
        var activation = ModuleActivationSidecar.Read(moduleRoot);
        var landed = activation.Entries
            .ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);

        var absent = ModuleDelivery.BinaryAbsent(
            records.Select(record => record.Manifest),
            module => File.Exists(MeshBuilder.ResolveModulePath($"{module}.dll", moduleRoot))
                || (landed.TryGetValue(module, out var entry)
                    && ModuleActivationBoot.LandedModuleDllExists(moduleRoot, entry)));

        if (absent.IsEmpty)
            return;

        logger?.LogWarning(
            "[PackageRepair] {Count} installed package(s) declare a compiled module whose assembly "
            + "is not on this installation: [{Packages}]. Their NODES are installed and their pages "
            + "render; every layout area and node type the module serves is ABSENT, and the install "
            + "record says the package is up to date. Nothing here can produce the bytes — a mounted "
            + "checkout never lands a module binary (MeshWeaver#2417). Install the package from a "
            + "registry that serves its bundle, or ship the module in the image (a MeshModuleClosure "
            + "row AND a Modules:Assemblies entry — the two move together or neither works).",
            absent.Count, string.Join(", ", absent.Select(u => u.Describe())));
    }

    /// <summary>
    /// Reconciles ONE recorded install: re-assert its declared access and re-run its hooks — unless
    /// the partition it names is GONE, in which case the record is a dangling reference and the
    /// write is skipped and reported (#3451).
    ///
    /// <para>🚨 <b>Why a stale record must not be written to.</b> The record lives in the
    /// <c>Plugins</c> partition and outlives the partition it points at, so deleting an installed
    /// space used to leave this pass aiming <c>{partition}/_Policy</c> at nothing on every boot,
    /// forever — a permanent per-boot error, and the writer that re-armed the resurrection race on
    /// every restart. Going FORWARD there is nothing to skip:
    /// <see cref="InstallRecordPartitionTeardownHandler"/> removes the record with the partition. This
    /// arm is for the records that already dangle (the #3436 census counted 21 partition definitions
    /// with no live root on one portal) and for a partition deleted by another replica or an older
    /// build.</para>
    ///
    /// <para>🚨 <b>It reports; it does not delete.</b> The delete path knows exactly which partition
    /// went, in-process, right now — so removing the record there is deterministic. Here the same
    /// conclusion rests on three probes, and a boot pass that silently deletes install records on a
    /// heuristic is a worse failure than the one it fixes. The remedy is the admin orphan list
    /// (<c>CatalogLayoutAreas</c> → <see cref="PackageInstaller.RemoveInstalledRecord"/>), named in
    /// the log line.</para>
    /// </summary>
    private IObservable<Unit> Reconcile(InstalledRecord record, ILogger? logger) =>
        TargetPartitionIsGone(record.Partition, logger).SelectMany(gone =>
        {
            if (gone)
            {
                logger?.LogWarning(
                    "[PackageRepair] SKIPPING install record '{Record}': its target partition "
                    + "'{Partition}' no longer exists — no root node, no children, and no storage "
                    + "provider reports a backing store. The record is a dangling reference; remove "
                    + "it from the admin orphan list (Catalog → orphaned install records). Nothing "
                    + "is written into a partition that is gone (#3451).",
                    $"{PackageInstaller.InstalledPartition}/{record.PackageId}", record.Partition);
                return Observable.Return(Unit.Default);
            }

            return PackageInstaller
                .EnsureDeclaredAccess(hub, record.Manifest, record.Partition, logger)
                .Catch<Unit, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[PackageRepair] re-asserting declared access for {Partition} failed",
                        record.Partition);
                    return Observable.Return(Unit.Default);
                })
                .SelectMany(_ => PackageInstaller.RunInstallHooks(hub, record.Partition, logger));
        });

    /// <summary>
    /// Is <paramref name="partition"/> definitively gone? A CONJUNCTION of three signals, each of
    /// which always answers, so the verdict has no arming condition and no silent skip:
    /// <list type="number">
    ///   <item>no storage provider reports the backing store present — the same global-OR fold
    ///     <c>PartitionWriteGuardValidator</c> applies, where only a <c>true</c> is evidence of
    ///     presence;</item>
    ///   <item>the partition ROOT node does not read back;</item>
    ///   <item>the partition has no child paths.</item>
    /// </list>
    ///
    /// <para>The conjunction is what makes it safe. Any ONE of these alone has a legitimate negative:
    /// a provider that cannot tell answers <c>null</c>, a root row can go missing while the space is
    /// alive (#638 — the very state the bootstrap repairs), and a read can fault. Requiring all three
    /// means a false "gone" needs a partition with no store, no root and no content — which is not a
    /// partition. A fault anywhere is read as the SAFE answer (present), so an unreachable store
    /// keeps its record — and so does a host that registers no partition providers at all, where
    /// signal 1 cannot be evaluated and must therefore not be counted as evidence of absence.</para>
    /// </summary>
    private IObservable<bool> TargetPartitionIsGone(string partition, ILogger? logger)
    {
        // 🚨 Every leg ends in DefaultIfEmpty(present) as well as Catch(present). A probe that
        // COMPLETES WITHOUT EMITTING is not caught by Timeout — Timeout faults on silence, not on a
        // clean finish (#2901) — and a Zip leg that never emits would park this record's
        // reconciliation forever inside a boot pass. Empty means "no answer", which reads as the
        // SAFE answer here: the partition is present, so nothing is declared stale.
        var providers = hub.ServiceProvider.GetServices<IPartitionStorageProvider>().ToList();
        // 🚨 NO providers ⇒ PRESENT, not absent (Copilot catch). A host on non-partitioned
        // persistence registers none, so a "false" here would leave the verdict resting on
        // root-absent + no-children alone — and an empty, rootless partition is precisely the #638
        // state the bootstrap exists to REPAIR, not a partition that is gone. Same fail-open rule
        // PartitionWriteGuardValidator applies (`providers.Count == 0 ⇒ allow`): a host that cannot
        // tell must never declare a record stale.
        var storePresent = providers.Count == 0
            ? Observable.Return(true)
            : Observable.CombineLatest(providers.Select(p => p.PartitionExists(partition)
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(5))
                    .Catch<bool?, Exception>(_ => Observable.Return<bool?>(true))
                    .DefaultIfEmpty(true)))
                .Take(1)
                .Select(results => results.Any(r => r == true))
                .DefaultIfEmpty(true);

        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        var rootPresent = storage is null
            ? Observable.Return(true)
            : storage.Read(partition, hub.JsonSerializerOptions)
                .Take(1)
                .DefaultIfEmpty(null!)
                .Select(node => node is not null)
                .Catch<bool, Exception>(_ => Observable.Return(true))
                .DefaultIfEmpty(true);

        var childrenPresent = storage is null
            ? Observable.Return(true)
            : storage.ListChildPaths(partition)
                .Take(1)
                .Select(children => children.NodePaths.Any() || children.DirectoryPaths.Any())
                .Catch<bool, Exception>(_ => Observable.Return(true))
                .DefaultIfEmpty(true);

        return Observable.Zip(storePresent, rootPresent, childrenPresent,
                (store, root, children) => (store, root, children))
            .Select(t =>
            {
                var gone = !t.store && !t.root && !t.children;
                if (gone)
                    logger?.LogDebug(
                        "[PackageRepair] partition '{Partition}' is definitively gone "
                        + "(store={Store}, root={Root}, children={Children})",
                        partition, t.store, t.root, t.children);
                return gone;
            });
    }

    /// <summary>
    /// 🚨 The check that did not exist (MeshWeaver#3485): what each install record DECLARES landed,
    /// compared against what is actually in the mesh — plus the partition roots no record accounts
    /// for at all.
    ///
    /// <para><b>Why here.</b> This pass is already the one complete inventory of what is installed,
    /// it already runs once per boot, and it is already fire-and-forget and failure-tolerant. Every
    /// other instrument the platform had counts what the installer DECIDED to write
    /// (<c>InstallResult.Written</c>, <c>InstalledNodeCount</c> — which had no reader at all) or
    /// hashes what the SOURCE serves (<c>ModuleVersion</c>). None of them reads the mesh back, which
    /// is why a source node lost on 2026-08-26 was still missing, unnamed, eleven days later — and
    /// was the proximate cause of the #3472 outage.</para>
    ///
    /// <para><b>It reports; it does not repair.</b> Healing belongs to the install lane, which now
    /// refuses to skip an incomplete package (<see cref="CatalogLayoutAreas"/>). A boot pass that
    /// silently reinstalled on a heuristic would be a worse failure than the one it names — the same
    /// discipline this service already applies to a dangling record.</para>
    ///
    /// <para><b>The denominator is printed</b>, per AGENTS.md: a sweep reporting zero problems must
    /// say how many things it looked at, so "nothing is wrong" and "nothing was checked" cannot
    /// read the same. Cost is one batched read per record over a bounded, KNOWN path set, one root
    /// listing, and one child listing per unaccounted root — never a query (eventually consistent:
    /// a stale negative would manufacture a shortfall) and never a point read of a path that may
    /// not exist.</para>
    /// </summary>
    private IObservable<Unit> VerifyCompleteness(
        IReadOnlyList<InstalledRecord> records, ILogger? logger)
    {
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        var accounted = records
            .Select(r => r.Partition)
            .Concat([PackageInstaller.InstalledPartition])
            .ToImmutableHashSet(StringComparer.Ordinal);

        // 🚨 The INSTALL's own parser registry (#3659). The declared population is "the files the
        // installer would have written as nodes", and which extensions those are is DI-dependent —
        // a module contributes parsers. Deriving it from half the rule is what made every
        // carry-along asset (a `.tsx` view, a `.png`, an extension-less LICENSE) count as a node
        // the install owed the mesh and be reported ABSENT at Error on every boot, forever.
        var parsers = new FileFormatParserRegistry(
            hub.JsonSerializerOptions, hub.ServiceProvider.GetServices<IFileFormatParser>());
        var perRecord = records.Count == 0
            ? Observable.Empty<InstallCompletenessVerdict>()
            : records
                .Select(record => InstallCompleteness.Observe(
                    persistence, hub.JsonSerializerOptions,
                    record.PackageId, record.Partition, record.Manifest, parsers))
                .ToObservable()
                .Concat();

        return perRecord
            .Concat(InstallCompleteness.ObserveUnaccountedRoots(
                persistence, hub.JsonSerializerOptions, accounted))
            .ToList()
            .Select(list => (IReadOnlyCollection<InstallCompletenessVerdict>)list.ToImmutableList())
            .Do(verdicts =>
            {
                foreach (var verdict in verdicts.Where(v => v.Kind is InstallCompletenessKind.Incomplete))
                    logger?.LogError(
                        "[InstallCompleteness] {Package} → '{Partition}': {Missing} of {Declared} "
                        + "declared node(s) are ABSENT. Missing: [{Paths}]. Counted over: "
                        + "{Population}. The install record says this package is up to date; the "
                        + "mesh disagrees. Reinstalling it now repairs it — the up-to-date gate no "
                        + "longer skips an incomplete install (MeshWeaver#3485).",
                        verdict.PackageId, verdict.Partition, verdict.Missing.Count,
                        verdict.Declared, string.Join(", ", verdict.Missing.Take(20)),
                        verdict.Population);

                foreach (var verdict in verdicts.Where(v => v.Kind is InstallCompletenessKind.RootWithoutRecord))
                    logger?.LogError(
                        "[InstallCompleteness] '{Partition}' is a partition ROOT that no install "
                        + "record accounts for, with no content and nothing but satellites. That is "
                        + "what an install leaves when it writes its root placeholder and then "
                        + "stops — and the portal serves it as an ordinary empty space. Install the "
                        + "package again, or delete the partition (MeshWeaver#3485).",
                        verdict.Partition);

                foreach (var verdict in verdicts.Where(v =>
                             v.Kind is InstallCompletenessKind.NotObserved
                                 or InstallCompletenessKind.Undeclared))
                    logger?.LogWarning(
                        "[InstallCompleteness] {Package} → '{Partition}': NOT VERIFIED ({Kind}) — "
                        + "{Because}. Counted over: {Population}. This is not a clean bill of "
                        + "health; it is an absence of one.",
                        verdict.PackageId, verdict.Partition, verdict.Kind, verdict.Because,
                        verdict.Population);

                var summary = InstallCompleteness.Summarize(verdicts);
                logger?.LogInformation(
                    "[InstallCompleteness] {Summary} (records scanned: {Records}; storage adapter: "
                    + "{Adapter})",
                    summary, records.Count, persistence is null ? "NONE" : "present");
            })
            .Select(_ => Unit.Default)
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[InstallCompleteness] the completeness sweep failed — NOTHING was verified "
                    + "this boot. Absence of a report here is not evidence that the installs are "
                    + "whole.");
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>One recorded install: its id, its target partition and the manifest recorded for it.</summary>
    private sealed record InstalledRecord(string PackageId, string Partition, PackageManifest Manifest);

    /// <summary>
    /// Every recorded install, one entry per partition. Reads the <c>Package</c> records the
    /// installer writes, so the repair covers exactly what is installed — no hard-coded package
    /// list. The record's content IS the manifest as installed (id, partition, price, declared
    /// public segments), which is what drives the access re-assert.
    /// </summary>
    private IObservable<IReadOnlyList<InstalledRecord>> InstalledRecords(ILogger? logger) =>
        hub.GetWorkspace()
            .GetQuery("installed-packages-repair",
                $"namespace:{PackageInstaller.InstalledPartition} "
                + $"nodeType:{PackageInstaller.PackageNodeType} select:path,id,name,nodeType,content")
            .Take(1)
            .Timeout(TimeSpan.FromMinutes(2))
            .Select(nodes => (IReadOnlyList<InstalledRecord>)nodes
                .Select(node => (Node: node,
                    Manifest: node.ContentAs<PackageManifest>(hub.JsonSerializerOptions)))
                .Where(x => x.Manifest is not null)
                .Select(x => new InstalledRecord(
                    x.Node.Id,
                    PackageInstaller.TargetPartitionOf(x.Node.Id, x.Manifest!),
                    x.Manifest!))
                .Where(r => !string.IsNullOrWhiteSpace(r.Partition))
                .GroupBy(r => r.Partition, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList())
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "[PackageRepair] listing installed packages failed");
                return Observable.Return((IReadOnlyList<InstalledRecord>)[]);
            });

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        subscription?.Dispose();
        subscription = null;
        return Task.CompletedTask;
    }
}
