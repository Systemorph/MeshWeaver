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
/// holding nodes created by anyone but the installer (user data). Then: retires the module (live
/// through <see cref="IModuleLiveActivation.Retire"/>, else exactly one restart through
/// <see cref="IModuleActivationRestart"/>), closes the partition's hubs, removes the install record
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
    /// install pass may bring back. Read from a LISTING of the request namespace (stale-tolerant:
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
                .Where(r => r is { Status: PackageUninstallStatus.AwaitingConfirmation or PackageUninstallStatus.TearingDown or PackageUninstallStatus.Done })
                .Select(r => r!.Package)
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
                    return Measure(partition).SelectMany(measured => measured.Refusal is { } refused
                        ? Finish(PackageUninstallStatus.Failed, $"refused: {refused}; nothing was touched")
                        : RetireModule(request, record)
                            .SelectMany(moduleOutcome => CloseHubs(partition).Select(closed => (moduleOutcome, closed)))
                            .SelectMany(x => PackageInstaller.RemoveInstalledRecord(hub, record.Id, logger).Take(1)
                                .Select(removed => (x.moduleOutcome, x.closed, removed)))
                            .SelectMany(x => Write(r => r with
                                {
                                    Module = string.IsNullOrWhiteSpace(record.Module) ? null : record.Module,
                                    ModuleOutcome = x.moduleOutcome,
                                    Partitions = [measured.Preview!],
                                    ConfirmationRequired = partition,
                                    Status = PackageUninstallStatus.AwaitingConfirmation,
                                },
                                Line($"module: {x.moduleOutcome}"),
                                Line($"closed {x.closed} hub(s) under '{partition}'"),
                                Line(x.removed ? $"install record {PackageInstaller.InstalledPartition}/{record.Id} removed — unattended passes will not re-install it"
                                               : $"install record {PackageInstaller.InstalledPartition}/{record.Id} was already gone"),
                                Line($"UNINSTALLED, data RETAINED. Phase 2 drops partition '{partition}' ({Describe(measured.Preview!)}) — "
                                     + $"send the confirmation '{partition}' to run it"))));
                })
                .Catch((Exception ex) => Finish(PackageUninstallStatus.Failed, $"phase 1 faulted: {ex.Message}"));

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
                            .Select(n => (Path: p, n?.CreatedBy))
                            .Catch((Exception _) => Observable.Return((Path: p, CreatedBy: (string?)null))))
                        .MergeBounded(8)
                        .ToList()
                        .SelectMany(read => storeExists.Select(exists =>
                        {
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

        /// <summary>Retires the package's module: removes its landed generation, then unloads it live
        /// where the process can, else asks for exactly ONE restart (stamped first).</summary>
        private IObservable<string> RetireModule(PackageUninstallRequest request, PackageManifest record)
        {
            var module = record.Module?.Trim();
            if (string.IsNullOrWhiteSpace(module))
                return Observable.Return("the package declares no compiled module");
            var landing = hub.ServiceProvider.GetService<ModuleLandingService>();
            var removed = landing is null
                ? Observable.Return("no landing service on this host")
                : landing.RemoveModule(module).Select(_ => "landed generation disabled")
                    .Catch((Exception ex) => Observable.Return($"no landed generation to remove ({ex.Message})"));
            var agent = hub.ServiceProvider.GetService<ModuleReloadAgent>();
            var loaded = (agent?.LoadedNames() ?? ModuleActivationStatus.LoadedAssemblyNames()).Contains(module);
            return removed.SelectMany(landed =>
            {
                if (!loaded)
                    return Observable.Return($"{module}: {landed}; not loaded in this process — nothing to unload");
                var reason = $"package uninstall {ModuleReloadExecutor.PathOf(hub)}: {request.Reason}";
                var live = hub.ServiceProvider.GetService<IModuleLiveActivation>();
                var liveTry = live is not null && live.CanSwap(module)
                    ? live.Retire(module, reason).Take(1).Catch((Exception ex) => Observable.Return(new ModuleSwapOutcome(false, ex.Message)))
                    : Observable.Return(new ModuleSwapOutcome(false, live is null ? "this process has no live module loader" : "it cannot be unloaded in place"));
                return liveTry.SelectMany(outcome => outcome.Swapped
                    ? Observable.Return($"{module}: {landed}; retired LIVE")
                    : Restart(reason).Select(restart => $"{module}: {landed}; not retired live ({outcome.Failure}) — {restart}"));
            });
        }

        private IObservable<string> Restart(string reason)
        {
            var restart = hub.ServiceProvider.GetService<IModuleActivationRestart>();
            if (restart is null)
                return Observable.Return("no self-update restart path on this host: it unloads at the next restart");
            return Write(r => r with { RestartRequestedAt = DateTimeOffset.UtcNow }, Line("requesting ONE restart to unload the module"))
                .SelectMany(_ => restart.RequestRestart(reason).Take(1))
                .Select(o => $"one restart requested ({o.Kind}: {o.Detail})");
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
            if (!string.Equals(request.Confirmation?.Trim(), request.ConfirmationRequired, StringComparison.Ordinal))
                return Write(r => r with
                {
                    Confirmation = null,
                    ConfirmationRefusal = $"refused: the confirmation '{request.Confirmation}' does not match the required "
                                          + $"'{request.ConfirmationRequired}' — nothing was dropped; the data is retained",
                }, Line($"confirmation '{request.Confirmation}' refused (required '{request.ConfirmationRequired}')"));
            if (request.RequestedBy is { Length: > 0 } requester && !string.Equals(request.ConfirmedBy, requester, StringComparison.OrdinalIgnoreCase))
                return Write(r => r with
                {
                    Confirmation = null,
                    ConfirmationRefusal = $"refused: confirmed by '{request.ConfirmedBy}', but only the requester '{requester}' may confirm — nothing was dropped",
                }, Line($"confirmation by '{request.ConfirmedBy}' refused (requester '{requester}')"));
            return Write(r => r with { Status = PackageUninstallStatus.TearingDown, ConfirmationRefusal = null },
                Line($"CONFIRMED by {request.ConfirmedBy ?? "(unnamed)"} at {request.ConfirmedAt:u} — dropping {request.ConfirmationRequired}"));
        }

        private IObservable<Unit> Phase2(PackageUninstallRequest request) =>
            request.Partitions.ToObservable()
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
