using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>The kinds of module operation an install record's owner executes — open string constants
/// (<c>Doc/Architecture/OpenVocabulariesAsStringConstants</c>).</summary>
public static class PackageModuleOperationKind
{
    /// <summary>Land the module's staged bytes (<see cref="ModuleLandingService"/>).</summary>
    public const string Land = "Land";

    /// <summary>Uninstall the module — the tombstone phase 1 of an uninstall writes.</summary>
    public const string Remove = "Remove";
}

/// <summary>Where a module operation stands on its install record — open string constants.</summary>
public static class PackageModuleOperationState
{
    /// <summary>Accepted by the owner, waiting for the one operation in flight to finish.</summary>
    public const string Queued = "Queued";

    /// <summary>The ONE operation the owner is executing now (or, when it carries a
    /// <see cref="PackageModuleOperation.Refusal"/>, answering without executing).</summary>
    public const string Executing = "Executing";
}

/// <summary>
/// A module operation on an install record (<see cref="PackageManifest.ModuleOperations"/>), keyed by
/// the fact key its writer stated it under — written ONLY by the record's own hub
/// (<see cref="ModuleLandingOwner"/>). Carries every argument of the landing except the bytes, which
/// wait on the shared volume under the same key.
/// </summary>
public sealed record PackageModuleOperation
{
    /// <summary>A <see cref="PackageModuleOperationKind"/> value.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The module's entry-assembly name.</summary>
    public string Module { get; init; } = "";

    /// <summary>A <see cref="PackageModuleOperationState"/> value.</summary>
    public string State { get; init; } = PackageModuleOperationState.Queued;

    /// <summary>The uninstall request a <see cref="PackageModuleOperationKind.Remove"/> speaks for.</summary>
    public string? Request { get; init; }

    /// <summary>The <see cref="PackageRecordWriteIntent"/> of a landing (null: not a person's).</summary>
    public string? Intent { get; init; }

    /// <summary>Landing argument: the framework identity the bytes were built against.</summary>
    public string? FrameworkMvid { get; init; }

    /// <summary>Landing argument: the install record's path, recorded on the activation entry.</summary>
    public string? PackagePath { get; init; }

    /// <summary>Landing argument: the version the bundle declares for its module bytes.</summary>
    public string? Version { get; init; }

    /// <summary>Landing argument: the module's declared (advisory) platform floor.</summary>
    public string? MinMeshVersion { get; init; }

    /// <summary>Landing argument: the producing repository's commit.</summary>
    public string? SourceCommit { get; init; }

    /// <summary>When the writer stated it — the owner executes in this order.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>When the owner started executing it.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Set by the owner when it promoted the operation: the refusal it answers with instead
    /// of executing (the package is being retired, the remover no longer holds the retirement).</summary>
    public string? Refusal { get; init; }

    /// <summary>Set ONLY by the owner when it promoted a person's landing: the person re-installs the
    /// package, so the landing is carried out over an uninstall tombstone an uninstall left behind
    /// (that uninstall lost its retirement to this install). Reset when a fact is queued, so no
    /// writer can claim it.</summary>
    public bool ReinstallByPerson { get; init; }
}

/// <summary>Every argument of one module landing, bytes included — the in-memory form of a landing
/// a writer stages and the owner reads back.</summary>
/// <param name="Name">The module's entry-assembly name.</param>
/// <param name="Assemblies">The module's closure, file name and bytes.</param>
internal sealed record ModuleLandingArguments(string Name, IReadOnlyList<(string FileName, byte[] Bytes)> Assemblies)
{
    public string? FrameworkMvid { get; init; }
    public string? PackagePath { get; init; }
    public string? Version { get; init; }
    public string? MinMeshVersion { get; init; }
    public IReadOnlyList<(string RelativePath, byte[] Bytes)>? StaticAssets { get; init; }
    public string? SourceCommit { get; init; }
    public IReadOnlyList<(string RelativePath, byte[] Bytes)>? NativeAssets { get; init; }
}

/// <summary>What became of one landing: landed, or the named refusal.</summary>
/// <param name="Applied">Whether it landed.</param>
/// <param name="Detail">What happened, or why not.</param>
internal sealed record ModuleLandingVerdict(bool Applied, string Detail);

/// <summary>
/// 🚨 <b>A module lands — and is removed — through its install record's OWN hub</b>
/// (MeshWeaver.Plugins#5055, <c>Doc/Architecture/PackageUninstall</c> → "The module lands through the
/// install record").
///
/// <para><b>The defect.</b> An install wrote its record and then downloaded and landed the module.
/// A download that started before an uninstall could finish after phase 1 had written the module's
/// tombstone, and its landing then re-enabled the module on every replica (the volume is shared) — a
/// landing decided on a snapshot taken before the download, executed after it.</para>
///
/// <para><b>The shape.</b> Nothing outside the owner lands or removes a package's module. A writer
/// STAGES the bytes on the shared volume (<see cref="ModuleLandingService.StageLanding"/>) and states
/// a <see cref="PackageRecordFactKind.ModuleLand"/> fact carrying every other argument; phase 1 states
/// a <see cref="PackageRecordFactKind.ModuleRemove"/>. The owner's fold
/// (<see cref="InstallRecordGate.Fold"/>) QUEUES each one, and promotes ONE at a time
/// (<see cref="Promote"/>), deciding it against the record's current state at that moment: a landing
/// while the package is being retired is refused (a person's landing clears a retirement that is not
/// yet removing the record, as a person's stamp does); a removal by an uninstall that no longer holds
/// the retirement is refused. The owner then EXECUTES the promoted operation itself, on its own
/// replica, and answers it under its key (<see cref="Arm"/>). One at a time per package, in the order
/// stated, so a removal stated after a landing runs after it — the tombstone is strictly last.</para>
///
/// <para><b>Resume.</b> The operation in flight is persisted as
/// <see cref="PackageModuleOperationState.Executing"/> BEFORE it runs, and its staged bytes are dropped by the writer,
/// only AFTER its answer is in storage. An owner activation that finds an executing operation it did not
/// start — the replica that started it died — executes it again: a landing re-reads the same staged
/// bytes, a removal writes one more tombstone. Neither changes the outcome by running twice.</para>
///
/// <para><b>A package with no install record</b> (the boot's modules-first wave lands a FRESH package's
/// module before any record exists) has no owner, and no retirement: a retirement lives on the record
/// and ends with it. That landing executes where it is, through
/// <see cref="ModuleLandingService.LandUnlessUninstalled"/>, which refuses it when the module's newest
/// event is an uninstall — the tombstone a completed uninstall left on the volume — decided in the
/// same work item that writes the bytes. What it cannot see is named on the doc page.</para>
/// </summary>
public static class ModuleLandingOwner
{
    /// <summary>How long a writer waits for the owner's answer to a module operation — one adoption's
    /// own budget (<see cref="RegistryUpdateReconciler.PerPackageAdoptBudget"/>): the answer comes
    /// only after the landing ran, and it may wait behind the one operation in flight.</summary>
    public static readonly TimeSpan OperationBudget = RegistryUpdateReconciler.PerPackageAdoptBudget;

    // ── Owner side ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Promotes the next operation when none is in flight — PURE, run inside the owner's serialised
    /// update (from <see cref="InstallRecordGate.Fold"/> and <see cref="Complete"/>). The oldest queued
    /// operation becomes <see cref="PackageModuleOperationState.Executing"/>, decided against the
    /// retirement as it stands NOW: a refused one is promoted with its
    /// <see cref="PackageModuleOperation.Refusal"/>, so the executor answers it without executing.
    /// </summary>
    /// <param name="operations">The record's operations.</param>
    /// <param name="retiring">The record's retirement.</param>
    /// <param name="recordModule">The module the install record declares — the only module its owner
    /// lands or removes.</param>
    /// <param name="now">The decision time.</param>
    internal static (ImmutableDictionary<string, PackageModuleOperation> Operations, PackageRetirement? Retiring) Promote(
        ImmutableDictionary<string, PackageModuleOperation> operations, PackageRetirement? retiring, string? recordModule,
        DateTimeOffset now)
    {
        if (operations.Values.Any(o => o.State == PackageModuleOperationState.Executing))
            return (operations, retiring);
        var next = operations
            .Where(kv => kv.Value.State == PackageModuleOperationState.Queued)
            .OrderBy(kv => kv.Value.At)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (KeyValuePair<string, PackageModuleOperation>?)kv)
            .FirstOrDefault();
        if (next is not { } head)
            return (operations, retiring);
        var (key, operation) = (head.Key, head.Value);
        string? refusal = null;
        var reinstall = false;
        switch (operation.Kind)
        {
            case PackageModuleOperationKind.Land or PackageModuleOperationKind.Remove
                when BindingRefusal(operation, recordModule) is { } unbound:
                refusal = unbound;
                break;
            case PackageModuleOperationKind.Land when retiring is not null
                                                     && !(operation.Intent == PackageRecordWriteIntent.Person && !retiring.Removing):
                refusal = retiring.Removing
                    ? $"refused: {retiring.Request} is removing this install record now — the module is not landed"
                    : $"refused: the package is being retired by {retiring.Request} — the module is not landed";
                break;
            case PackageModuleOperationKind.Land:
                // A person's landing clears a retirement that is not yet removing the record — the
                // rule a person's stamp follows (InstallRecordGate.Fold) — so the uninstall's removal
                // queued behind it is refused instead of taking the module the person just installed.
                // A person's landing is carried out even over a tombstone (ReinstallByPerson): the
                // uninstall that wrote it lost its retirement to this install.
                if (operation.Intent == PackageRecordWriteIntent.Person)
                {
                    retiring = null;
                    reinstall = true;
                }
                break;
            case PackageModuleOperationKind.Remove when retiring is null
                                                       || !string.Equals(retiring.Request, operation.Request, StringComparison.Ordinal):
                refusal = $"refused: {operation.Request} no longer holds this package's retirement (a person installed it, "
                          + "or it was never granted) — the module is not removed";
                break;
            case PackageModuleOperationKind.Remove:
                break;
            default:
                refusal = $"refused: unknown module operation '{operation.Kind}'";
                break;
        }
        return (operations.SetItem(key, operation with
        {
            State = PackageModuleOperationState.Executing,
            StartedAt = now,
            Refusal = refusal,
            ReinstallByPerson = refusal is null && reinstall,
        }), retiring);
    }

    /// <summary>Why the owner of a record declaring <paramref name="recordModule"/> must not carry out
    /// <paramref name="operation"/>, or null: an install record lands and removes ITS OWN module and no
    /// other (MeshWeaver#7974 review). Pure.</summary>
    internal static string? BindingRefusal(PackageModuleOperation operation, string? recordModule) =>
        string.IsNullOrWhiteSpace(recordModule)
            ? $"refused: this install record declares no module, so it does not {operation.Kind.ToLowerInvariant()} '{operation.Module}'"
            : !string.Equals(operation.Module?.Trim(), recordModule.Trim(), StringComparison.OrdinalIgnoreCase)
                ? $"refused: module '{operation.Module}' is not this package's module ('{recordModule}')"
                : null;

    /// <summary>
    /// Whether the hub at <paramref name="path"/> is an AUTHORITATIVE install record —
    /// <c>Plugins/{package}</c>, one segment under the installed partition — the only kind of node
    /// whose hub executes module operations. A node of the Package type anywhere else (a user's own
    /// namespace) executes nothing, whatever its content says (MeshWeaver#7974 review). Pure.
    /// </summary>
    /// <param name="path">The hub's node path.</param>
    internal static bool IsAuthoritativeRecordPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var segments = path.Trim('/').Split('/');
        return segments.Length == 2
               && string.Equals(segments[0], PackageInstaller.InstalledPartition, StringComparison.Ordinal)
               && segments[1].Length > 0
               && !segments[1].StartsWith('_');
    }

    /// <summary>
    /// The owner's own check, at EXECUTION, of the operation it is about to carry out — against the
    /// record as the owner holds it now. A promoted operation passes by construction; an operation
    /// read back as executing (a resume) is held to the same rules, so a persisted
    /// <see cref="PackageModuleOperationState.Executing"/> entry never bypasses the decision: the
    /// module must be the record's, and a removal must still be the retirement holder's. Pure.
    /// </summary>
    internal static string? ExecutionRefusal(PackageModuleOperation operation, PackageManifest? record)
    {
        if (record is null)
            return "refused: the install record is gone";
        if (BindingRefusal(operation, record.Module) is { } unbound)
            return unbound;
        return operation.Kind == PackageModuleOperationKind.Remove
               && (record.RetiringBy is null
                   || !string.Equals(record.RetiringBy.Request, operation.Request, StringComparison.Ordinal))
            ? $"refused: {operation.Request} does not hold this package's retirement — the module is not removed"
            : null;
    }

    /// <summary>The owner finished operation <paramref name="key"/>: its answer is written under the
    /// key, the operation leaves the queue and the next one is promoted — in ONE update. Pure.</summary>
    /// <param name="node">The install record as the owner holds it.</param>
    /// <param name="key">The operation's key.</param>
    /// <param name="outcome">The answer.</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <param name="now">The decision time.</param>
    internal static MeshNode Complete(MeshNode node, string key, PackageRecordFactOutcome outcome, JsonSerializerOptions options, DateTimeOffset now)
    {
        var record = node.ContentAs<PackageManifest>(options);
        if (record is null)
            return node;
        var (operations, retiring) = Promote(
            (record.ModuleOperations ?? ImmutableDictionary<string, PackageModuleOperation>.Empty).Remove(key),
            record.RetiringBy, record.Module, now);
        return node with
        {
            Content = record with
            {
                RetiringBy = retiring,
                ModuleOperations = operations.IsEmpty ? null : operations,
                FactOutcomes = (record.FactOutcomes ?? ImmutableDictionary<string, PackageRecordFactOutcome>.Empty).SetItem(key, outcome),
            },
        };
    }

    /// <summary>
    /// Arms the module-operation executor on an install record's OWN hub — on its init turn, coupled
    /// to its lifetime. It executes the ONE operation in flight once per activation (an activation
    /// that finds one it did not start resumes it), and promotes the next when nothing is in flight.
    /// A static method group, so repeat registrations collapse.
    /// </summary>
    /// <param name="hub">The install record's own hub.</param>
    public static IObservable<Unit> Arm(IMessageHub hub) => Observable.Defer(() =>
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(ModuleLandingOwner));
        // 🚨 Only an AUTHORITATIVE install record executes module operations (#7974 review): the
        // Package node type is armed wherever a Package node lives, and a node outside Plugins/{id}
        // must not move the shared module volume, whatever its content says.
        if (!IsAuthoritativeRecordPath(ModuleReloadExecutor.PathOf(hub)))
            return Observable.Return(Unit.Default);
        var workspace = hub.GetWorkspace();
        var options = hub.JsonSerializerOptions;
        // 🚨 Every execution is OWNED by this activation (#7974 review): disposed with it, so a
        // recycled or deactivated owner cancels its landing at the landing's own cancellation points
        // (before each staged file and before the commit rename) instead of running on unowned.
        var executions = new CompositeDisposable();
        hub.RegisterForDisposal(executions);
        // The keys THIS activation has started — per activation (a fresh activation resumes what a
        // dead one left executing), never static. Keys are fresh per submission, so the set only grows
        // by what this activation executed.
        var started = new StartedOperations();
        hub.RegisterForDisposal(ActivityControlPlaneExtensions.SubscribeHubWatcher(
            hub,
            () => workspace.GetMeshNodeStream()
                .Select(node => Head(node, options))
                .DistinctUntilChanged(),
            head =>
            {
                if (head.ExecutingKey is { } key && head.Executing is { } operation)
                {
                    if (started.TryStart(key))
                    {
                        var execution = new SingleAssignmentDisposable();
                        executions.Add(execution);
                        execution.Disposable = Execute(hub, key, operation, logger)
                            .Finally(() => executions.Remove(execution))
                            .Subscribe(_ => { }, ex => logger?.LogWarning(ex,
                                "[ModuleLandingOwner] the module operation '{Key}' on {Address} could not be answered", key, hub.Address));
                    }
                }
                else if (head.Queued > 0)
                    workspace.GetMeshNodeStream()
                        .Update(node => PromoteNode(node, options, DateTimeOffset.UtcNow))
                        .Subscribe(_ => { }, ex => logger?.LogWarning(ex,
                            "[ModuleLandingOwner] promoting the next module operation on {Address} failed", hub.Address));
            },
            logger,
            "module landing owner"));
        return Observable.Return(Unit.Default);
    });

    private sealed class StartedOperations
    {
        private ImmutableHashSet<string> keys = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

        public bool TryStart(string key)
        {
            var added = false;
            ImmutableInterlocked.Update(ref keys, set =>
            {
                added = !set.Contains(key);
                return set.Add(key);
            });
            return added;
        }
    }

    private sealed record OperationHead(string? ExecutingKey, PackageModuleOperation? Executing, int Queued);

    private static OperationHead Head(MeshNode? node, JsonSerializerOptions options)
    {
        var operations = node?.ContentAs<PackageManifest>(options)?.ModuleOperations;
        if (operations is not { Count: > 0 })
            return new OperationHead(null, null, 0);
        var executing = operations.FirstOrDefault(kv => kv.Value.State == PackageModuleOperationState.Executing);
        return new OperationHead(executing.Key, executing.Value,
            operations.Count(kv => kv.Value.State == PackageModuleOperationState.Queued));
    }

    private static MeshNode PromoteNode(MeshNode node, JsonSerializerOptions options, DateTimeOffset now)
    {
        var record = node.ContentAs<PackageManifest>(options);
        if (record?.ModuleOperations is not { Count: > 0 } operations)
            return node;
        var (promoted, retiring) = Promote(operations, record.RetiringBy, record.Module, now);
        return ReferenceEquals(promoted, operations)
            ? node
            : node with { Content = record with { ModuleOperations = promoted, RetiringBy = retiring } };
    }

    /// <summary>Executes one promoted operation on the owner's replica and writes its answer (which
    /// promotes the next). Its staged bytes are the WRITER's to drop, after the writer's durable
    /// barrier (<see cref="InstallRecordGate.Submit(IMessageHub, string, string, PackageRecordFact, ILogger?)"/>
    /// removes the answer cross-hub, which acknowledges only once the owner's state — the operation
    /// gone — is in storage): an owner that dropped them after its own in-memory commit could leave a
    /// stored Executing operation whose bytes are gone (#7974 review). Cold.</summary>
    private static IObservable<Unit> Execute(IMessageHub hub, string key, PackageModuleOperation operation, ILogger? logger)
    {
        var landing = hub.ServiceProvider.GetService<ModuleLandingService>();
        var workspace = hub.GetWorkspace();
        PackageRecordFactOutcome Answer(bool applied, string detail) => new(applied, detail, DateTimeOffset.UtcNow);
        return workspace.GetMeshNodeStream().Take(1)
            .Select(node => ExecutionRefusal(operation, node?.ContentAs<PackageManifest>(hub.JsonSerializerOptions)))
            .SelectMany(unauthorized => Run(unauthorized ?? operation.Refusal));

        IObservable<Unit> Run(string? refused) => (refused is not null
            ? Observable.Return(Answer(false, refused))
            : landing is null
                ? Observable.Return(Answer(false, "refused: the install record's host registers no module landing service"))
                : operation.Kind switch
                {
                    PackageModuleOperationKind.Land => landing!.LandStaged(key, operation)
                        .Select(verdict => Answer(verdict.Applied, verdict.Detail)),
                    PackageModuleOperationKind.Remove => landing!.RemoveModule(operation.Module)
                        .Select(_ => Answer(true, "landed generation disabled"))
                        .Catch((Exception ex) => Observable.Return(Answer(false, $"no landed generation to remove ({ex.Message})"))),
                    _ => Observable.Return(Answer(false, $"refused: unknown module operation '{operation.Kind}'")),
                })
            .Take(1)
            // A landing the link probe refuses faults — an answer like any other, never an operation
            // left executing for ever.
            .Catch((Exception ex) => Observable.Return(Answer(false, $"the {operation.Kind} faulted: {ex.Message}")))
            .Do(outcome => logger?.LogInformation(
                "[ModuleLandingOwner] {Address}: {Kind} of module '{Module}' ({Key}) — {Detail}",
                hub.Address, operation.Kind, operation.Module, key, outcome.Detail))
            .SelectMany(outcome => workspace.GetMeshNodeStream()
                .Update(node => Complete(node, key, outcome, hub.JsonSerializerOptions, DateTimeOffset.UtcNow))
                .Take(1))
            .Select(_ => Unit.Default);
    }

    // ── Writer side ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lands <paramref name="landing"/> through its install record's owner: the bytes are staged, the
    /// landing is stated as a fact, and the answer arrives once the owner has executed — or refused —
    /// it. With no install record at <see cref="ModuleLandingArguments.PackagePath"/> (a fresh
    /// package) there is no owner and no retirement, and the landing executes here, refused when the
    /// module was uninstalled since it was planned. Cold; a refusal is an answer, never a fault.
    /// </summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="landing">The landing service.</param>
    /// <param name="arguments">The landing.</param>
    /// <param name="intent">The <see cref="PackageRecordWriteIntent"/> it is stated under.</param>
    /// <param name="recordAtPlan">🚨 Whether the install record EXISTED when this adoption began —
    /// before its download (#7974 review). A landing that belonged to a record and finds it gone has
    /// LOST its owner (an uninstall removed it while the bytes travelled) and is refused; only a
    /// landing that never had a record is a fresh package's.</param>
    /// <param name="logger">Diagnostics.</param>
    internal static IObservable<ModuleLandingVerdict> Land(
        IMessageHub hub, ModuleLandingService landing, ModuleLandingArguments arguments, string? intent,
        bool recordAtPlan, ILogger? logger)
    {
        var path = arguments.PackagePath;
        if (intent is not null && !InstallRecordGate.IsKnownIntent(intent))
            return Observable.Return(new ModuleLandingVerdict(false,
                $"refused: '{intent}' is not a write intent this owner knows — nothing was landed"));
        if (string.IsNullOrWhiteSpace(path))
            return landing.LandUnlessUninstalled(arguments);
        var storage = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();
        return storage.Read(path, hub.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .SelectMany(existing =>
            {
                if (existing is null && recordAtPlan)
                    return Observable.Return(new ModuleLandingVerdict(false,
                        $"refused: the install record {path} existed when this adoption began and is gone now — "
                        + "the package was uninstalled while the module travelled; nothing was landed"));
                if (existing is null)
                {
                    logger?.LogInformation(
                        "[ModuleLandingOwner] {Path} holds no install record — module '{Module}' lands as a fresh package's, "
                        + "refused if it was uninstalled since it was planned", path, arguments.Name);
                    return landing.LandUnlessUninstalled(arguments);
                }
                var key = $"module-land-{Guid.NewGuid():N}";
                var fact = new PackageRecordFact
                {
                    Kind = PackageRecordFactKind.ModuleLand,
                    Intent = intent,
                    At = DateTimeOffset.UtcNow,
                    Module = new PackageModuleOperation
                    {
                        Kind = PackageModuleOperationKind.Land,
                        Module = arguments.Name,
                        Intent = intent,
                        FrameworkMvid = arguments.FrameworkMvid,
                        PackagePath = arguments.PackagePath,
                        Version = arguments.Version,
                        MinMeshVersion = arguments.MinMeshVersion,
                        SourceCommit = arguments.SourceCommit,
                    },
                };
                return landing.StageLanding(key, arguments)
                    // 🚨 The staged bytes are dropped ONLY on an ANSWER (#7974 review): applied or
                    // refused by the owner — read after Submit's durable barrier, so storage no longer
                    // holds the operation — or "the record was removed", when no owner remains. A
                    // submission whose fate is unknown (a timeout, an unreachable owner whose patch
                    // may still apply) leaves them for the owner that may yet read them; a stray
                    // folder is the cost, never an accepted landing whose bytes were pulled away.
                    .SelectMany(_ => InstallRecordGate.Submit(hub, path, key, fact, logger, removeAnswer: null, budget: OperationBudget))
                    .SelectMany(outcome => Drop(landing, key, logger)
                        .Select(_ => new ModuleLandingVerdict(outcome.Applied, outcome.Detail)));
            });
    }

    private static IObservable<Unit> Drop(ModuleLandingService landing, string key, ILogger? logger) =>
        landing.DropStagedLanding(key).Catch((Exception ex) =>
        {
            logger?.LogWarning(ex, "[ModuleLandingOwner] dropping the staged landing '{Key}' failed", key);
            return Observable.Return(Unit.Default);
        });

    /// <summary>
    /// Phase 1 of an uninstall removes the package's module THROUGH its install record's owner: the
    /// removal queues behind any landing in flight, so the tombstone is written last. Emits
    /// <c>landed generation disabled</c>; a removal the owner did not carry out (refused, or the
    /// landing service refused it) FAULTS with the owner's words. The
    /// caller holds the retirement (<see cref="InstallRecordGate.BeginRetirement"/>); a removal by a
    /// request that no longer holds it is refused. Cold.
    /// </summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="packageId">The package.</param>
    /// <param name="module">The module's entry-assembly name.</param>
    /// <param name="request">The uninstall request holding the retirement.</param>
    /// <param name="logger">Diagnostics.</param>
    public static IObservable<string> Remove(IMessageHub hub, string packageId, string module, string request, ILogger? logger)
    {
        var path = $"{PackageInstaller.InstalledPartition}/{packageId}";
        var key = $"module-remove-{Guid.NewGuid():N}";
        return InstallRecordGate.Submit(hub, path, key, new PackageRecordFact
                {
                    Kind = PackageRecordFactKind.ModuleRemove,
                    Request = request,
                    At = DateTimeOffset.UtcNow,
                    Module = new PackageModuleOperation
                    {
                        Kind = PackageModuleOperationKind.Remove,
                        Module = module,
                        Request = request,
                    },
                }, logger, removeAnswer: null, budget: OperationBudget)
            // A removal that did not happen FAULTS, carrying the owner's words — never a sentence a
            // caller could read as done (the rule phase 1 holds a failed removal to).
            .SelectMany(outcome => outcome.Applied
                ? Observable.Return(outcome.Detail)
                : Observable.Throw<string>(new PackageRecordWriteRefusedException(path, outcome.Detail)));
    }
}
