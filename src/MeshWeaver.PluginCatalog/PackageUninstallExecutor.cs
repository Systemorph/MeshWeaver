using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 The executor of a <see cref="PackageUninstallRequest"/> (<c>Doc/Architecture/PackageUninstall</c>)
/// — on the request node's OWN hub, one brain per request, every step a <c>stream.Update</c> and
/// idempotent so a re-activated executor simply runs its step again.
///
/// <para><b>Phase 1 — no confirmation.</b> Refuses, before anything is touched and by name: a
/// package with no install record here; a target partition another installed package also uses; a
/// partition the platform never tears down (<see cref="PartitionTeardown.Refusal"/>); a partition
/// holding nodes created by anyone but the installer (user data) — that census guards phase 2's drop,
/// so a FLAG-issued request skips it and records <see cref="FlagRetireNotMeasured"/> instead, and its
/// confirmation is refused by name (policy <c>package-uninstall-request</c>). Then: retires the module (live
/// through <see cref="IModuleLiveActivation.Retire"/>, else by the ONE restart the instance's
/// <see cref="PackageRestartCoordinator"/> dispatches), closes the partition's hubs, removes the install record
/// — which, with this request on record, keeps every unattended pass from installing it again
/// (<see cref="UninstalledHere"/>) — and records exactly what phase 2 would destroy.</para>
///
/// <para><b>Phase 2 — after the requester confirms.</b> The confirmation must be
/// <see cref="PackageUninstallRequest.ConfirmationRequired"/> exactly, from the requester; anything
/// else is refused by name and the data stays. Then the partition's hubs are closed again and each
/// partition goes through the platform's governed whole-partition teardown
/// (<see cref="PartitionTeardown.TearDownPartition"/> — store drop on every provider, Postgres
/// <c>DROP SCHEMA … CASCADE</c> with its satellite tables, cached queries evicted, the
/// <c>Admin/Partition</c> registry record deleted) as System. Never raw SQL.</para>
/// </summary>
public static class PackageUninstallExecutor
{
    /// <summary>The most nodes phase 1 inspects for user data; a larger partition is refused by name
    /// (it cannot be verified here) rather than dropped unverified.</summary>
    public const int MaxInspectedNodes = 20_000;

    /// <summary>
    /// What a FLAG-issued request records as its partition's preview instead of a measurement
    /// (policy <c>package-uninstall-request</c>, MeshWeaver#8020). The census — the
    /// <see cref="MaxInspectedNodes"/> cap and the <c>CreatedBy</c> check — exists to guard phase 2's
    /// drop, and a flag retire never reaches phase 2: it names no requester, and its confirmation is
    /// refused by name. Running the census anyway refused the retire of a partition above the cap on
    /// every boot, forever, while touching nothing.
    /// </summary>
    public const string FlagRetireNotMeasured = "not measured: a flag retire never drops";

    /// <summary>What phase 2 drops that phase 1 cannot count — said, never implied.</summary>
    public static readonly ImmutableList<string> Unmeasured =
    [
        "compiled assemblies cached on disk for the partition's NodeTypes (left for the module GC / next restart)",
        "search and vector index entries kept outside the partition's own store",
        "blob / content-collection storage kept outside the partition's own store",
    ];

    /// <summary>Arms the executor on a request node's own hub.</summary>
    /// <param name="hub">The request node's hub.</param>
    public static IObservable<Unit> Arm(IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(PackageUninstallExecutor));
        var run = new Run(hub, logger);
        hub.RegisterForDisposal(hub.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Started)
            .Take(1)
            .Where(level => level == MessageHubRunLevel.Started)
            .SelectMany(_ => hub.GetWorkspace().GetMeshNodeStream())
            .Select(run.Step)
            .Concat()
            .Subscribe(_ => { },
                ex => logger?.LogError(ex, "[PackageUninstall] the executor on {Path} stopped — the request keeps its status", hub.Address)));
        return Observable.Return(Unit.Default);
    }

    /// <summary>
    /// The packages this instance has UNINSTALLED and not installed again — what no unattended
    /// install pass may bring back. A request a feature flag issued
    /// (<see cref="PackageUninstallRequest.RetiredByFlag"/>) is not counted. Read from a LISTING of the request namespace (stale-tolerant:
    /// a request missing from it costs one more boot) minus the install records that exist now, so
    /// a person installing the package again lifts the block. Never faults: an unreadable listing
    /// blocks nothing.
    /// </summary>
    /// <param name="hub">The mesh hub.</param>
    public static IObservable<ImmutableHashSet<string>> UninstalledHere(IMessageHub hub)
    {
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        if (mesh is null)
            return Observable.Return(ImmutableHashSet<string>.Empty);
        return access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{PackageUninstallRequest.Namespace} scope:children nodeType:{PackageUninstallRequest.NodeType}"))
                .Take(1)
                .Timeout(ActivationRecycle.ReadBudget))
            // CQRS: the listing names which requests exist; each one's STATUS is read from its own
            // node — a listing's content trails the write that moved the request on.
            .SelectMany(change => change.Items.Select(n => n.Path).ToObservable()
                .Select(p => access.RunAsSystem(() => hub.GetMeshNodeStream(p).Take(1).Timeout(ActivationRecycle.ReadBudget))
                    .Select(n => n.ContentAs<PackageUninstallRequest>(hub.JsonSerializerOptions))
                    .Catch((Exception _) => Observable.Empty<PackageUninstallRequest?>()))
                .MergeBounded(8)
                .ToList())
            .Select(requests => requests
                .OfType<PackageUninstallRequest>()
                // A FLAG-issued retire blocks nothing (#5055): while the flag stays off its exclusion
                // keeps the package out, and once the record stops declaring it off the package's
                // own lanes must be free to bring it back — the record decides, not this ledger.
                .Where(r => r.RetiredByFlag is null)
                .Where(r => r.Status is PackageUninstallStatus.AwaitingConfirmation or PackageUninstallStatus.TearingDown or PackageUninstallStatus.Done)
                .Select(r => r.Package)
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase))
            .SelectMany(uninstalled => uninstalled.IsEmpty
                ? Observable.Return(uninstalled)
                : ModuleDependencyFloor.ReadInstalled(hub)
                    .Select(installed => uninstalled.Except(installed.Select(m => m.Id))))
            .Catch((Exception _) => Observable.Return(ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>The table a stored path belongs to: its satellite segment (<c>_Access</c>, …) or
    /// <c>mesh_nodes</c>. Pure.</summary>
    /// <param name="partition">The partition.</param>
    /// <param name="path">A path at or under it.</param>
    public static string TableOf(string partition, string path)
    {
        var rest = path.Length > partition.Length ? path[(partition.Length + 1)..] : "";
        return rest.Split('/').FirstOrDefault(s => s.StartsWith('_')) is { Length: > 0 } satellite
            ? satellite
            : "mesh_nodes";
    }

    /// <summary>Whether a node's creator is a PERSON (anything but the installer's system identity). Pure.</summary>
    /// <param name="createdBy">The node's <c>CreatedBy</c>.</param>
    public static bool IsUserCreated(string? createdBy) =>
        !string.IsNullOrWhiteSpace(createdBy)
        && !string.Equals(createdBy, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase);

    /// <summary>The module outcome's words for a landed generation that was disabled.</summary>
    private const string LandedGenerationDisabled = "landed generation disabled";

    private sealed class Run(IMessageHub hub, ILogger? logger)
    {
        private int phase1;
        private int phase2;
        private int finished;

        private AccessService? Access => hub.ServiceProvider.GetService<AccessService>();

        public IObservable<Unit> Step(MeshNode node)
        {
            var request = node.ContentAs<PackageUninstallRequest>(hub.JsonSerializerOptions);
            if (request is null || PackageUninstallStatus.IsTerminal(request.Status))
                return Observable.Empty<Unit>();
            if (!string.Equals(node.CreatedBy, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase))
                return Once(ref finished, () => Finish(PackageUninstallStatus.Failed,
                    $"written by '{node.CreatedBy}', not by System — only PackageUninstall.Request may issue an uninstall; nothing was touched"));
            if (PackageUninstall.Validate(request) is { } invalid)
                return Once(ref finished, () => Finish(PackageUninstallStatus.Failed, invalid));
            return request.Status switch
            {
                PackageUninstallStatus.Requested or PackageUninstallStatus.Uninstalling =>
                    Once(ref phase1, () => Phase1(request)),
                PackageUninstallStatus.AwaitingConfirmation when request.Confirmation is not null => Confirmed(request),
                PackageUninstallStatus.AwaitingConfirmation => Observable.Empty<Unit>(),
                PackageUninstallStatus.TearingDown => Once(ref phase2, () => Phase2(request)),
                _ => Once(ref finished, () => Finish(PackageUninstallStatus.Failed,
                    $"the request carries the status '{request.Status}', which this executor does not know")),
            };
        }

        private static IObservable<Unit> Once(ref int flag, Func<IObservable<Unit>> step) =>
            Interlocked.Exchange(ref flag, 1) == 0 ? step() : Observable.Empty<Unit>();

        // ── Phase 1 ──────────────────────────────────────────────────────────────────────────

        private IObservable<Unit> Phase1(PackageUninstallRequest request) =>
            Write(r => r with { Status = PackageUninstallStatus.Uninstalling },
                    Line($"uninstalling '{request.Package}' — reason: {request.Reason}"))
                .SelectMany(_ => ModuleDependencyFloor.ReadInstalled(hub))
                .SelectMany(installed =>
                {
                    var record = installed.FirstOrDefault(m => string.Equals(m.Id, request.Package, StringComparison.OrdinalIgnoreCase));
                    if (record is null)
                        return Finish(PackageUninstallStatus.Failed,
                            $"'{request.Package}' is not an installed package here — no {PackageInstaller.InstalledPartition}/{request.Package} install record; nothing was touched");
                    var partition = PackageInstaller.TargetPartitionOf(record.Id, record);
                    var sharers = installed
                        .Where(m => !string.Equals(m.Id, record.Id, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(PackageInstaller.TargetPartitionOf(m.Id, m), partition, StringComparison.OrdinalIgnoreCase))
                        .Select(m => m.Id)
                        .ToList();
                    if (sharers.Count > 0)
                        return Finish(PackageUninstallStatus.Failed,
                            $"refused: partition '{partition}' is shared with the installed package(s) {string.Join(", ", sharers)} — uninstalling '{record.Id}' would destroy their content; nothing was touched");
                    if (PartitionTeardown.Refusal(hub, partition, requireSystem: false) is { } never)
                        return Finish(PackageUninstallStatus.Failed, $"refused: {never}; nothing was touched");
                    // 🚨 A flag retire never drops (policy package-uninstall-request, #8020): the
                    // census guards phase 2 only, so a flag-issued phase 1 records "not measured"
                    // instead of being refused by it. A person's uninstall is measured unchanged.
                    var measure = request.RetiredByFlag is not null
                        ? Observable.Return(new Measured(NotMeasured(partition), null))
                        : Measure(partition);
                    return measure.SelectMany(measured => measured is not { Preview: { } preview }
                        ? Finish(PackageUninstallStatus.Failed, $"refused: {measured.Refusal ?? "the partition could not be measured"}; nothing was touched")
                        // 🚨 The install record's OWN hub grants the retirement first
                        // (MeshWeaver.Plugins#5055, InstallRecordGate): from here it refuses every
                        // unattended stamp, so no repair or update re-writes the record while this
                        // runs. Refused (another uninstall holds it) ⇒ nothing was touched.
                        : InstallRecordGate.BeginRetirement(hub, record.Id, RequestPath, request.Reason, logger)
                            .Select(refusal => (Refusal: refusal, Fault: (Exception?)null))
                            .Catch((Exception ex) => Observable.Return((Refusal: (string?)null, Fault: (Exception?)ex)))
                            .SelectMany(begun => begun.Fault is { } fault
                                // 🚨 The owner may have GRANTED the retirement although its answer
                                // never arrived (#7964 review): release it before going red, and the
                                // owner's ended-request memory refuses a late Begin of this request.
                                ? InstallRecordGate.EndRetirement(hub, record.Id, RequestPath, logger)
                                    .Select(_ => "released")
                                    .Catch((Exception endFault) => Observable.Return($"NOT released ({endFault.Message})"))
                                    .SelectMany(released => Finish(PackageUninstallStatus.Failed,
                                        $"the retirement's answer did not arrive ({fault.Message}); nothing was touched and the retirement was {released}"))
                                : begun.Refusal is { } refusal
                                    ? Finish(PackageUninstallStatus.Failed, $"refused: {refusal}; nothing was touched")
                                    : Retire(request, record, partition, preview)));
                })
                .Catch((Exception ex) => Finish(PackageUninstallStatus.Failed, $"phase 1 faulted: {ex.Message}"));

        private string RequestPath => ModuleReloadExecutor.PathOf(hub);

        /// <summary>Phase 1 once the retirement is held: retire the module, close the hubs, announce the
        /// removal to the record's owner, remove the record. A fault RELEASES the retirement before the
        /// request goes red, so the package is repairable again.</summary>
        private IObservable<Unit> Retire(PackageUninstallRequest request, PackageManifest record, string partition, PackageUninstallPartition preview) =>
            EnrollPending(request, record)
                .SelectMany(_ => RetireModule(request, record))
                            .SelectMany(retired => retired.Failure is { } moduleFailure
                                // 🚨 A module that could not be disabled durably FAILS an every-replica
                                // retire before the install record is touched (#7959 review) — and
                                // releases the retirement, so the package stays repairable.
                                ? WithdrawPending().SelectMany(_ => ReleaseThenFail(record, $"refused: {moduleFailure}; the install record is kept"))
                                : CloseHubs(partition).Select(closed => (moduleOutcome: retired.Outcome, retired.Disabled, closed))
                                // Announce the removal: from the owner's answer on, a person's install is
                                // refused too. A refusal here means a PERSON installed it meanwhile — the
                                // record is theirs now and stays.
                                .SelectMany(x => InstallRecordGate.MarkRemoving(hub, record.Id, RequestPath, logger)
                                    .Select(displaced => (x.moduleOutcome, x.Disabled, x.closed, displaced)))
                                .SelectMany(x => x.displaced is { } displaced
                                    ? Finish(PackageUninstallStatus.Failed,
                                        $"stopped: {displaced}; the install record is kept (module: {x.moduleOutcome})")
                                    : RemoveAndPreview(request.RetiredByFlag, record, partition, preview, x.moduleOutcome, x.Disabled, x.closed)))
                .Catch((Exception ex) => WithdrawPending()
                    .SelectMany(_ => InstallRecordGate.EndRetirement(hub, record.Id, RequestPath, logger))
                    .Select(_ => "released")
                    .Catch((Exception endFault) => Observable.Return($"NOT released ({endFault.Message})"))
                    .SelectMany(released => Finish(PackageUninstallStatus.Failed,
                        $"phase 1 faulted after the retirement was granted: {ex.Message}; the retirement was {released}")));

        /// <summary>Releases the held retirement, then fails the request naming whether the release landed.</summary>
        private IObservable<Unit> ReleaseThenFail(PackageManifest record, string message) =>
            InstallRecordGate.EndRetirement(hub, record.Id, RequestPath, logger)
                .Select(_ => "released")
                .Catch((Exception endFault) => Observable.Return($"NOT released ({endFault.Message})"))
                .SelectMany(released => Finish(PackageUninstallStatus.Failed, $"{message}; the retirement was {released}"));

        private IObservable<Unit> RemoveAndPreview(string? retiredByFlag, PackageManifest record, string partition, PackageUninstallPartition preview, string moduleOutcome, bool moduleDisabled, int closed) =>
            PackageInstaller.RemoveInstalledRecord(hub, record.Id, logger).Take(1)
                                .Select(removed => (moduleOutcome, Disabled: moduleDisabled, closed, removed))
                            .SelectMany(x => Write(r => r with
                                {
                                    Module = string.IsNullOrWhiteSpace(record.Module) ? null : record.Module,
                                    ModuleOutcome = x.moduleOutcome,
                                    ModuleDisabled = moduleDisabled,
                                    Partitions = [preview],
                                    // A flag retire offers no confirmation: nothing may drop an
                                    // unmeasured partition.
                                    ConfirmationRequired = retiredByFlag is null ? partition : null,
                                    Status = PackageUninstallStatus.AwaitingConfirmation,
                                    // 🚨 The preview is what a confirmation answers (#6124 review): a
                                    // confirmation recorded BEFORE it existed is discarded here, and the
                                    // instant is stamped so phase 2 can re-check the order.
                                    AwaitingConfirmationAt = DateTimeOffset.UtcNow,
                                    Confirmation = null,
                                    ConfirmedBy = null,
                                    ConfirmedAt = null,
                                },
                                Line($"module: {x.moduleOutcome}"),
                                Line($"closed {x.closed} hub(s) under '{partition}'"),
                                Line(x.removed ? $"install record {PackageInstaller.InstalledPartition}/{record.Id} removed — unattended passes will not re-install it"
                                               : $"install record {PackageInstaller.InstalledPartition}/{record.Id} was already gone"),
                                Line(retiredByFlag is { } flag
                                    ? $"UNINSTALLED, data RETAINED. Partition '{partition}' {FlagRetireNotMeasured} (retired by the flag '{flag}'); "
                                      + "no confirmation can drop it"
                                    : $"UNINSTALLED, data RETAINED. Phase 2 drops partition '{partition}' ({Describe(preview)}) — "
                                      + $"send the confirmation '{partition}' to run it")));

        /// <summary>A flag retire's preview: the partition named, nothing counted, and why.</summary>
        private static PackageUninstallPartition NotMeasured(string partition) => new()
        {
            Partition = partition,
            StoreExists = null,
            Unmeasured = [FlagRetireNotMeasured],
        };

        private sealed record Measured(PackageUninstallPartition? Preview, string? Refusal);

        private IObservable<Measured> Measure(string partition)
        {
            var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
            if (storage is null)
                return Observable.Return(new Measured(null, "this host has no storage adapter, so the partition cannot be measured"));
            var stores = hub.ServiceProvider.GetServices<IPartitionStorageProvider>()
                .Select(p => p.PartitionExists(partition).Take(1).DefaultIfEmpty(null)
                    .Catch<bool?, Exception>(_ => Observable.Return<bool?>(null)))
                .ToList();
            var storeExists = stores.Count == 0
                ? Observable.Return<bool?>(null)
                : stores.Merge().ToList().Select(a => a.Any(x => x == true) ? true : a.Any(x => x == false) ? false : (bool?)null);
            return storage.ListDescendantPaths(partition).Take(1)
                .Select(paths => paths.Append(partition).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableList())
                .SelectMany(paths => paths.Count > MaxInspectedNodes
                    ? Observable.Return(new Measured(null,
                        $"partition '{partition}' holds {paths.Count} nodes, more than the {MaxInspectedNodes} this uninstall verifies for user data"))
                    : paths.ToObservable()
                        .Select(p => storage.Read(p, hub.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
                            .Select(n => (Path: p, n?.CreatedBy, Fault: (string?)null))
                            // 🚨 A FAULT is not an absence (#6124 review): it is recorded, and any one of
                            // them refuses the uninstall below — the user-data check fails CLOSED, the
                            // same direction as the size cap.
                            .Catch((Exception ex) => Observable.Return((Path: p, CreatedBy: (string?)null, Fault: (string?)ex.Message))))
                        .MergeBounded(8)
                        .ToList()
                        .SelectMany(read => storeExists.Select(exists =>
                        {
                            var faults = read.Where(x => x.Fault is not null).OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
                            if (faults.Count > 0)
                                return new Measured(null,
                                    $"{faults.Count} node(s) under '{partition}' could not be read (e.g. "
                                    + string.Join(", ", faults.Take(5).Select(f => $"{f.Path}: {f.Fault}"))
                                    + ") — the partition is not verified free of user data");
                            var user = read.Where(x => IsUserCreated(x.CreatedBy)).OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
                            if (user.Count > 0)
                                return new Measured(null,
                                    $"partition '{partition}' holds {user.Count} node(s) created by people, not by the installer "
                                    + $"(e.g. {string.Join(", ", user.Take(5).Select(u => $"{u.Path} by {u.CreatedBy}"))}) — that is user data the package does not own");
                            var rows = paths
                                .GroupBy(p => TableOf(partition, p), StringComparer.Ordinal)
                                .ToImmutableDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                            return new Measured(new PackageUninstallPartition
                            {
                                Partition = partition,
                                StoreExists = exists,
                                RowsByTable = rows,
                                SyncConfig = paths.Any(p => string.Equals(p, $"{partition}/_GitSync", StringComparison.OrdinalIgnoreCase)),
                                Unmeasured = Unmeasured,
                            }, null);
                        })));
        }

        private static string Describe(PackageUninstallPartition p) =>
            $"store {(p.StoreExists switch { true => "present", false => "absent", null => "not reported" })}; rows "
            + string.Join(", ", p.RowsByTable.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))
            + (p.SyncConfig ? "; a sync configuration" : "");

        /// <summary>What retiring the module did: the outcome sentence, whether the landed generation is
        /// disabled (now, or by an earlier interrupted run), and — for an every-replica retire only — why
        /// it must FAIL instead.</summary>
        private sealed record ModuleRetired(string Outcome, bool Disabled, string? Failure = null);

        /// <summary>
        /// Retires the package's module: disables its landed generation, then unloads it live where the
        /// process can, else states it on the instance's ONE restart coordinator, which dispatches the restart.
        ///
        /// <para>🚨 The activation is read WHOLE or not at all (<see cref="ModuleLandingService.GetCheckedActivation"/>,
        /// #7959 review): a read that dropped an unreadable record would make a landed module look
        /// absent, and phase 1 would then remove the install record with the generation still enabled
        /// — so an incomplete read FAULTS, and phase 1 goes red with the install record kept. A removal
        /// that fails faults the same way, never becomes a sentence. An entry that is ALREADY disabled
        /// is "disable done" (a phase 1 resumed after its removal), never "never landed" — and its
        /// removal still runs again, because the tombstone may have landed without the projection.
        /// Once the generation is disabled, <see cref="PackageUninstallRequest.ModuleDisabled"/> is
        /// written at once, before any later step can fail. An every-replica retire (a flag-issued
        /// request) takes <see cref="RetireForEveryReplica"/> under the same rules and states its restart
        /// through <see cref="ReadyForEveryReplica"/>.</para>
        /// </summary>
        private IObservable<ModuleRetired> RetireModule(PackageUninstallRequest request, PackageManifest record)
        {
            var module = record.Module?.Trim();
            if (string.IsNullOrWhiteSpace(module))
                return Observable.Return(new ModuleRetired("the package declares no compiled module", false));
            if (request.RetireOnEveryReplica)
                return RetireForEveryReplica(request, record, module)
                    // What is on disk is recorded the moment it is true (#7972 review), on this lane too.
                    .SelectMany(retired => (retired.Disabled
                            ? Write(r => r with { ModuleDisabled = true }, Line($"module: {retired.Outcome}"))
                            : Observable.Return(Unit.Default))
                        .Select(_ => retired))
                    // Only a module durably disabled needs every replica's restart; any other exit
                    // withdraws, so it never holds the batch's restart back.
                    .SelectMany(retired => retired.Disabled
                        ? ReadyForEveryReplica(module, request.Reason).Select(restart => retired with { Outcome = $"{retired.Outcome} — {restart}" })
                        : WithdrawPending().Select(_ => retired));
            var landing = hub.ServiceProvider.GetService<ModuleLandingService>();
            var removed = landing is null
                ? Observable.Return(new ModuleRetired("no landing service on this host", false))
                : landing.GetCheckedActivation().Take(1).SelectMany(activation =>
                    activation.Entries.FirstOrDefault(e => string.Equals(e.Name, module, StringComparison.OrdinalIgnoreCase)) switch
                    {
                        // 🚨 No entry HERE is not "never landed" (#7974 review, round 4): the owner may be
                        // executing the module's FIRST landing right now. The removal is queued behind it,
                        // and the owner decides whether a generation exists once that landing finished.
                        null => ModuleLandingOwner.Remove(hub, record.Id, module, RequestPath, logger)
                            .Select(outcome => outcome == ModuleLandingOwner.NothingToRemove
                                ? new ModuleRetired(outcome, false)
                                : new ModuleRetired("landed generation disabled", true)),
                        // 🚨 Disabled by the TOMBSTONE does not mean the removal finished (#7972
                        // review): a removal stopped between the tombstone and the projection leaves
                        // the per-module file — what an older image reads — still enabled. The
                        // removal is idempotent, so it runs again and rewrites the projection.
                        // 🚨 Every removal goes through the install record's OWNER (MeshWeaver.Plugins#5055,
                        // ModuleLandingOwner): it queues behind any landing in flight there, so the
                        // tombstone is written last; a removal the owner did not carry out faults.
                        { Enabled: false } => ModuleLandingOwner.Remove(hub, record.Id, module, RequestPath, logger).Select(_ => new ModuleRetired(
                            "landed generation already disabled (an earlier run of this phase removed it)", true)),
                        _ => ModuleLandingOwner.Remove(hub, record.Id, module, RequestPath, logger)
                            .Select(_ => new ModuleRetired("landed generation disabled", true)),
                    });
            var loaded = LoadedHere(module);
            return removed
                // 🚨 What is on disk is recorded the moment it is true (#7972 review): a later step
                // (the live retire, the restart, the hub close, the record removal) that fails ends
                // the request Failed, and it must still say the module IS disabled.
                .SelectMany(retired => (retired.Disabled
                        ? Write(r => r with { ModuleDisabled = true }, Line($"module: {retired.Outcome}"))
                        : Observable.Return(Unit.Default))
                    .Select(_ => retired))
                .SelectMany(retired => RetireLoaded(request, module, loaded, retired.Outcome)
                    .Select(outcome => retired with { Outcome = outcome }));
        }

        private bool LoadedHere(string module)
        {
            var agent = hub.ServiceProvider.GetService<ModuleReloadAgent>();
            return (agent?.LoadedNames() ?? ModuleActivationStatus.LoadedAssemblyNames()).Contains(module);
        }

        /// <summary>Set once this run stated <see cref="PackageRestartFactKind.Pending"/>: its exits
        /// then withdraw it, so a stopped phase 1 never holds another request's restart back.</summary>
        private int pendingStated;

        /// <summary>
        /// 🚨 Tells the instance's ONE restart coordinator (<see cref="PackageRestartCoordinator"/>,
        /// MeshWeaver.Plugins#5055) that this request MAY need a restart — before its module is
        /// touched, so a restart another request asks for meanwhile waits for this one and covers both.
        /// Only when a restart can follow: the package declares a module, the host has a restart path,
        /// and this process holds the module — or, for an every-replica retire (a flag-issued request),
        /// ANY replica may hold it, so it enrols whether or not this process does. A flag-issued request
        /// was already enrolled by its boot pass before it was written (<see cref="DeclaredDisabledRetirement"/>);
        /// stating it again is a no-op on the coordinator. Not reaching the coordinator weakens only
        /// the batching, never phase 1.
        /// </summary>
        private IObservable<Unit> EnrollPending(PackageUninstallRequest request, PackageManifest record)
        {
            var module = record.Module?.Trim();
            if (string.IsNullOrWhiteSpace(module)
                || hub.ServiceProvider.GetService<IModuleActivationRestart>() is null
                || (!request.RetireOnEveryReplica && !LoadedHere(module)))
                return Observable.Return(Unit.Default);
            return PackageRestartCoordinator.Pending(hub, RequestPath, module, request.Reason, logger)
                .Do(_ => Interlocked.Exchange(ref pendingStated, 1))
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex, "[PackageUninstall] {Path}: the restart coordinator could not be told this request is pending — "
                                           + "a restart another request asks for meanwhile may not cover it", RequestPath);
                    return Observable.Return(Unit.Default);
                });
        }

        /// <summary>Withdraws this run's pending enrolment (a no-op when it stated none, or when it
        /// is already ready or covered). Never faults.</summary>
        private IObservable<Unit> WithdrawPending() =>
            Volatile.Read(ref pendingStated) == 0
                ? Observable.Return(Unit.Default)
                : PackageRestartCoordinator.Withdraw(hub, RequestPath, logger)
                    .Catch((Exception ex) =>
                    {
                        logger?.LogWarning(ex, "[PackageUninstall] {Path}: withdrawing from the restart coordinator failed — "
                                               + "it stops holding other restarts back after its pending budget", RequestPath);
                        return Observable.Return(Unit.Default);
                    });

        /// <summary>
        /// 🚨 The every-replica retire (a flag-issued request, MeshWeaver.Plugins#5055). The module must
        /// be DURABLY disabled on the volume every replica shares, or the request fails — a restart over
        /// a still-enabled generation would bring the module back on every replica while the install
        /// record needed for another attempt was gone. The removal goes through the install record's
        /// owner (<see cref="ModuleLandingOwner.Remove"/>) exactly as a person's uninstall does; a
        /// module loaded here that the store lane never landed is image-carried — the deployment's, which
        /// no flag can retire. The restart is never issued here: <see cref="ReadyForEveryReplica"/>
        /// states it on the instance's ONE restart coordinator.
        /// </summary>
        private IObservable<ModuleRetired> RetireForEveryReplica(PackageUninstallRequest request, PackageManifest record, string module)
        {
            var loaded = LoadedHere(module);
            var landing = hub.ServiceProvider.GetService<ModuleLandingService>();
            // 🚨 Fails CLOSED (#7959 review): a host with no landing service cannot read the shared
            // activation record, so "not loaded here" says nothing about the generation other replicas
            // load — proceeding would remove the install record over a generation still enabled.
            if (landing is null)
                return Observable.Return(new ModuleRetired("", false,
                    $"module {module}: this host has no landing service, so it cannot disable the module's landed generation "
                    + $"durably on the shared volume{(loaded ? " (and the module is loaded here)" : "")}"));
            var reason = $"package uninstall {ModuleReloadExecutor.PathOf(hub)}: {request.Reason}";
            // 🚨 Read WHOLE (GetCheckedActivation, #7972): an unreadable record faults and the retire
            // fails with the install record kept. No entry here may be a FIRST landing in flight at the
            // owner (#7974), so the owner decides; a DISABLED entry's idempotent removal runs again.
            return landing.GetCheckedActivation().Take(1)
                .SelectMany(activation =>
                {
                    var entry = activation.Entries.FirstOrDefault(e => string.Equals(e.Name, module, StringComparison.OrdinalIgnoreCase));
                    return ModuleLandingOwner.Remove(hub, record.Id, module, RequestPath, logger)
                        .SelectMany(outcome => entry is null && outcome == ModuleLandingOwner.NothingToRemove
                            ? Observable.Return(loaded
                                ? new ModuleRetired("", false, $"module {module} is loaded here but was {outcome} — "
                                                               + "an image-carried module is the deployment's, and no flag can retire it")
                                : new ModuleRetired($"{module}: {outcome}, and it is not loaded here — nothing to disable", false))
                            : (loaded
                                    ? LiveRetire(module, reason).Select(o => o.Swapped ? "retired LIVE here" : $"still loaded here ({o.Failure})")
                                    : Observable.Return("not loaded here"))
                                .Select(local => new ModuleRetired(
                                    $"{module}: {LandedGenerationDisabled} on the shared volume; {local}", true)));
                })
                .Catch((Exception ex) => Observable.Return(new ModuleRetired("", false,
                    $"module {module}'s landed generation could not be disabled durably: {ex.Message}")));
        }

        /// <summary>
        /// 🚨 Every replica drops a flag-retired module at ONE restart, and that restart is the
        /// coordinator's to dispatch (<see cref="PackageRestartCoordinator"/>): this states
        /// <see cref="PackageRestartFactKind.Ready"/> and does NOT wait for the dispatch — the boot pass
        /// enrolled every request of its batch as pending first, so the dispatch is decided only once
        /// the batch's last request is ready, and waiting here would hold each earlier phase 1 for the
        /// answer budget. A resumed phase 1 states it again; a covered member is never dispatched again.
        /// </summary>
        private IObservable<string> ReadyForEveryReplica(string module, string reason)
        {
            if (hub.ServiceProvider.GetService<IModuleActivationRestart>() is null)
                return WithdrawPending().Select(_ => "no self-update restart path on this host: every replica drops it at its next restart");
            return PackageRestartCoordinator.StateReady(hub, RequestPath, module, reason, logger)
                .Select(_ => $"every replica drops it at the ONE restart the restart coordinator {PackageRestartCoordinator.Path} dispatches for this batch")
                .Catch((Exception ex) => Observable.Return(
                    $"the restart coordinator could not be told ({ex.Message}): every replica drops it at its next restart"));
        }

        private IObservable<ModuleReloadSwapOutcome> LiveRetire(string module, string reason)
        {
            var live = hub.ServiceProvider.GetService<IModuleLiveActivation>();
            return live is not null && live.CanSwap(module)
                ? live.Retire(module, reason).Take(1).Catch((Exception ex) => Observable.Return(new ModuleReloadSwapOutcome(false, ex.Message)))
                : Observable.Return(new ModuleReloadSwapOutcome(false, live is null ? "this process has no live module loader" : "it cannot be unloaded in place"));
        }

        private IObservable<string> RetireLoaded(PackageUninstallRequest request, string module, bool loaded, string landed)
        {
            // 🚨 A request persisted before the restart coordinator existed may already have asked its
            // one restart itself (it stamped RestartRequestedAt first): never asked again — the
            // compatibility rule for in-flight requests across the upgrade.
#pragma warning disable CS0618 // read-only for requests written before the coordinator
            var legacy = request.RestartRequestedAt;
#pragma warning restore CS0618
            if (legacy is { } askedAt)
                return WithdrawPending().Select(_ =>
                    $"{module}: {landed}; this request already asked its one restart at {askedAt:u} (before the restart coordinator) — not asked again");
            if (!loaded)
                return WithdrawPending().Select(_ => $"{module}: {landed}; not loaded in this process — nothing to unload");
            var reason = $"package uninstall {ModuleReloadExecutor.PathOf(hub)}: {request.Reason}";
            return LiveRetire(module, reason).SelectMany(outcome => outcome.Swapped
                ? WithdrawPending().Select(_ => $"{module}: {landed}; retired LIVE")
                : Restart(module, request.Reason).Select(restart => $"{module}: {landed}; not retired live ({outcome.Failure}) — {restart}"));
        }

        /// <summary>
        /// 🚨 The restart is NOT this request's to issue (MeshWeaver.Plugins#5055): it states
        /// <see cref="PackageRestartFactKind.Ready"/> on the instance's ONE restart coordinator, whose
        /// own hub dispatches ONE restart for every request ready at that moment, records the outcome,
        /// retries a faulted dispatch without repeating this phase, and never dispatches an accepted
        /// one again. This waits for the dispatch's answer only to say it in the module outcome.
        /// </summary>
        private IObservable<string> Restart(string module, string reason)
        {
            if (hub.ServiceProvider.GetService<IModuleActivationRestart>() is null)
                return WithdrawPending().Select(_ => "no self-update restart path on this host: it unloads at the next restart");
            return PackageRestartCoordinator.Ready(hub, RequestPath, module, reason, logger)
                .Select(dispatch => dispatch switch
                {
                    null => $"a restart is enrolled with the restart coordinator {PackageRestartCoordinator.Path}; its dispatch was not "
                            + $"decided within {PackageRestartCoordinator.AnswerBudget} (another uninstall may still be retiring its module) — "
                            + "the coordinator dispatches it",
                    { State: PackageRestartDispatchState.Accepted } d =>
                        $"one restart requested ({d.Kind}: {d.Detail}) — dispatched by the restart coordinator as {d.Id}, "
                        + $"covering {d.Covers.Count} request(s)",
                    { State: PackageRestartDispatchState.Faulted } d =>
                        $"one restart requested from the restart coordinator; its dispatch {d.Id} faulted ({d.Detail}) and is retried "
                        + "by the reconcile pass — this phase is not repeated",
                    { State: PackageRestartDispatchState.Interrupted } d =>
                        $"one restart requested; its dispatch {d.Id} was interrupted after it was asked — never asked again",
                    var d => $"no restart could be requested ({d.Kind}: {d.Detail}): it unloads at the next restart",
                });
        }

        /// <summary>Disposes every hub this process hosts at or under <paramref name="partition"/> —
        /// through the off-router issuing hub, never from a hub being disposed. Emits how many.</summary>
        private IObservable<int> CloseHubs(string partition) => Observable.Defer(() =>
        {
            var meshHub = hub.ServiceProvider.GetService<IMessageHub>() ?? hub;
            var hosted = hub.ServiceProvider.GetService<HostedHubsCollection>()?.Hubs.ToList() ?? [];
            var targets = hosted
                .Where(h => h.RunLevel is MessageHubRunLevel.Starting or MessageHubRunLevel.Started)
                .Select(ActivationRecycle.PathOf)
                .Where(p => string.Equals(p, partition, StringComparison.OrdinalIgnoreCase)
                            || p.StartsWith(partition + "/", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var issuing = meshHub.NodeOperationIssuingHub();
            foreach (var target in targets)
                issuing.Post(new DisposeRequest { Reason = $"package uninstall {ModuleReloadExecutor.PathOf(hub)}: closing '{partition}'" },
                    o => o.WithTarget(new Address(target)));
            return Observable.Return(targets.Count);
        });

        // ── Phase 2 ──────────────────────────────────────────────────────────────────────────

        private IObservable<Unit> Confirmed(PackageUninstallRequest request)
        {
            // 🚨 First, before any other check: phase 1 of a flag retire did not measure the
            // partition, so no confirmation may ever drop it (policy package-uninstall-request).
            if (request.RetiredByFlag is { } flag)
                return Write(r => r with
                {
                    Confirmation = null,
                    ConfirmationRefusal = $"refused: this request was issued by the flag '{flag}' and its partition was {FlagRetireNotMeasured} — "
                                          + "nothing was dropped; the data is retained",
                }, Line($"confirmation refused: a flag retire (flag '{flag}') never drops"));
            if (!string.Equals(request.Confirmation?.Trim(), request.ConfirmationRequired, StringComparison.Ordinal))
                return Write(r => r with
                {
                    Confirmation = null,
                    ConfirmationRefusal = $"refused: the confirmation '{request.Confirmation}' does not match the required "
                                          + $"'{request.ConfirmationRequired}' — nothing was dropped; the data is retained",
                }, Line($"confirmation '{request.Confirmation}' refused (required '{request.ConfirmationRequired}')"));
            if (request.AwaitingConfirmationAt is not { } previewed || request.ConfirmedAt is not { } confirmedAt || confirmedAt < previewed)
                return Write(r => r with
                {
                    Confirmation = null,
                    ConfirmationRefusal = "refused: the confirmation was recorded before the preview it must answer — nothing was dropped; "
                                          + "read the preview and confirm again",
                }, Line("confirmation refused: it predates the preview"));
            if (request.RequestedBy is not { Length: > 0 } requester)
                return Write(r => r with
                {
                    Confirmation = null,
                    ConfirmationRefusal = "refused: the request names no requester, so nobody can be checked as the one confirming — "
                                          + "nothing was dropped; the data is retained",
                }, Line("confirmation refused: the request names no requester"));
            if (!string.Equals(request.ConfirmedBy, requester, StringComparison.OrdinalIgnoreCase))
                return Write(r => r with
                {
                    Confirmation = null,
                    ConfirmationRefusal = $"refused: confirmed by '{request.ConfirmedBy}', but only the requester '{requester}' may confirm — nothing was dropped",
                }, Line($"confirmation by '{request.ConfirmedBy}' refused (requester '{requester}')"));
            return Write(r => r with { Status = PackageUninstallStatus.TearingDown, ConfirmationRefusal = null },
                Line($"CONFIRMED by {request.ConfirmedBy ?? "(unnamed)"} at {request.ConfirmedAt:u} — dropping {request.ConfirmationRequired}"));
        }

        private IObservable<Unit> Phase2(PackageUninstallRequest request) =>
            request.RetiredByFlag is { } flag
                ? Finish(PackageUninstallStatus.Failed,
                    $"refused: a flag retire (flag '{flag}') never drops — its partition was {FlagRetireNotMeasured}; nothing was dropped")
                : request.Partitions.ToObservable()
                .Select(p => CloseHubs(p.Partition)
                    .SelectMany(_ => Access.RunAsSystem(() => hub.TearDownPartition(p.Partition,
                        $"package uninstall {ModuleReloadExecutor.PathOf(hub)} of '{request.Package}', confirmed by {request.ConfirmedBy}")))
                    .Select(outcome => p with
                    {
                        TornDown = true,
                        TeardownOutcome = $"dropped on {outcome.Providers} storage provider(s); registry record Admin/Partition/{outcome.Partition} "
                                          + (outcome.RecordDeleted ? "deleted" : "was already absent"),
                    }))
                .Concat()
                .ToList()
                .SelectMany(torn => Write(r => r with
                    {
                        Partitions = [.. torn],
                        Status = PackageUninstallStatus.Done,
                        CompletedAt = DateTimeOffset.UtcNow,
                    }, [.. torn.Select(t => Line($"'{t.Partition}': {t.TeardownOutcome}"))]))
                .Do(_ => Interlocked.Exchange(ref finished, 1))
                .Catch((Exception ex) => Finish(PackageUninstallStatus.Failed,
                    $"phase 2 faulted — the partition teardown's own record is kept as the retry handle: {ex.Message}"));

        // ── Writes ───────────────────────────────────────────────────────────────────────────

        private IObservable<Unit> Finish(string status, string failure)
        {
            Interlocked.Exchange(ref finished, 1);
            logger?.LogWarning("[PackageUninstall] {Path}: {Status} — {Failure}", ModuleReloadExecutor.PathOf(hub), status, failure);
            return Write(r => r with
            {
                Status = status,
                Failure = r.Failure is null ? failure : $"{r.Failure}; {failure}",
                CompletedAt = DateTimeOffset.UtcNow,
            }, Line(failure));
        }

        private IObservable<Unit> Write(Func<PackageUninstallRequest, PackageUninstallRequest> change, params string[] lines)
        {
            foreach (var line in lines)
                logger?.LogInformation("[PackageUninstall] {Path}: {Line}", ModuleReloadExecutor.PathOf(hub), line);
            return Access.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream()
                    .Update<PackageUninstallRequest>(current =>
                    {
                        var next = change(current);
                        var log = next.Log.AddRange(lines);
                        return next with { Log = log.Count > ModuleReloadExecutor.MaxLogLines ? log.RemoveRange(0, log.Count - ModuleReloadExecutor.MaxLogLines) : log };
                    }))
                .Take(1)
                .Select(_ => Unit.Default);
        }

        private static string Line(string text) => $"{DateTimeOffset.UtcNow:u} {text}";
    }
}
