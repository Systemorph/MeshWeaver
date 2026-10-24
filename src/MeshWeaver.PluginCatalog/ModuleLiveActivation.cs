using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What one live activation pass decided for THIS process: an outcome per landed-but-not-serving
/// module, or why the pass could not tell.
/// </summary>
/// <param name="Outcomes">One per module the activation record names that was not serving here.</param>
/// <param name="Undetermined">Why the pass could not establish what is pending, or null.</param>
public sealed record ModuleLiveActivationResult(
    ImmutableList<ModuleSwapOutcome> Outcomes,
    string? Undetermined = null)
{
    /// <summary>
    /// Whether a restart is still needed to activate what landed: the pass could not tell (the
    /// conservative answer is the restart that was always taken), or some module did not go live.
    /// </summary>
    public bool NeedsRestart => Undetermined is not null || Outcomes.Any(o => o.NeedsRestart);

    /// <summary>The modules that went live in this pass.</summary>
    public ImmutableList<ModuleSwapOutcome> WentLive => Outcomes.Where(o => o.Kind == ModuleSwapKind.Live).ToImmutableList();

    /// <summary>The modules that still need the restart, with their reasons.</summary>
    public ImmutableList<ModuleSwapOutcome> StillPending => Outcomes.Where(o => o.NeedsRestart).ToImmutableList();

    /// <summary>
    /// True when the pass ESTABLISHED that nothing landed is pending in this process — so nothing
    /// went live and nothing was swapped (#6620). Distinct from a pass that swapped something in.
    /// </summary>
    public bool NothingWasPending => Undetermined is null && Outcomes.IsEmpty;

    /// <summary>
    /// When <see cref="NothingWasPending"/>: the per-process activation report's own line
    /// (<see cref="ModuleActivationReport.Describe"/>) — which names, for instance, a landed
    /// generation DECLINED in favour of the image's copy, quarantined, or running a previous
    /// generation. Those are exactly the states in which a landed generation is "not pending" while
    /// the old code keeps serving, so a verdict must carry them rather than claim a swap.
    /// </summary>
    public string? NothingPendingBecause { get; init; }

    /// <summary>One sentence naming what went live and what still needs a restart, and why.</summary>
    public string Describe()
    {
        if (Undetermined is not null)
            return $"the live activation could not establish what is pending here ({Undetermined})";
        var parts = new List<string>();
        if (!WentLive.IsEmpty)
            parts.Add("went live without a restart: " + string.Join(", ", WentLive.Select(o => o.Module)));
        var upToDate = Outcomes.Where(o => o.Kind == ModuleSwapKind.UpToDate).ToImmutableList();
        if (!upToDate.IsEmpty)
            parts.Add("already serving the landed generation, so nothing was swapped in: "
                + string.Join(", ", upToDate.Select(o => o.Module)));
        if (!StillPending.IsEmpty)
            parts.Add("still needs a restart: " + string.Join("; ", StillPending.Select(o => $"{o.Module} ({o.Kind}: {o.Reason})")));
        return parts.Count == 0
            ? "nothing landed is pending activation in this process, so nothing was swapped in"
              + (string.IsNullOrWhiteSpace(NothingPendingBecause) ? string.Empty : " — " + NothingPendingBecause)
            : string.Join("; ", parts);
    }
}

/// <summary>
/// The LIVE-FIRST activation of landed modules (policy <c>module-live-update-default</c>,
/// <c>Doc/Architecture/LiveModuleUpdate</c>): for every module the activation record names that is
/// not serving its landed generation in THIS process, ask the <see cref="ModuleLiveUpdater"/> to
/// swap it in live. Only what does not go live — a module that declares or measures a contribution
/// the process cannot re-apply, a swap that fails, a module this process does not hold in its own
/// context — still needs the restart, and the result says which and why.
///
/// <para>"Pending here" is <see cref="PendingModuleActivations"/>' per-process answer — the same
/// reader every surface uses — so this pass and <c>/health</c> can never disagree about what is
/// waiting. The generation swapped in is the PINNED copy (<see cref="ModuleGenerationPin"/>), the
/// same path boot loads, so a landing that later moves the shared directory cannot pull bytes out
/// from under a live generation.</para>
///
/// <para>Mesh-scoped instance; registered by <c>AddPluginCatalog</c>.</para>
/// </summary>
public sealed class ModuleLiveActivation(
    PendingModuleActivations activations,
    ModuleLandingService landing,
    ModuleContexts contexts,
    ModuleLiveUpdater updater,
    IoPoolRegistry pools,
    ILogger<ModuleLiveActivation>? logger = null)
{
    private readonly IIoPool pool = pools.Get(IoPoolNames.FileSystem);

    /// <summary>
    /// Swaps in, live, every module that has landed and is not serving here. Cold; emits one result.
    /// Never errors: a fault is reported as <see cref="ModuleLiveActivationResult.Undetermined"/>,
    /// which keeps the restart the caller would otherwise have taken.
    /// </summary>
    /// <param name="reason">Why the pass runs — carried into every recycled hub's quiesce line.</param>
    public IObservable<ModuleLiveActivationResult> ActivatePending(string reason) =>
        pool.InvokeBlocking(ct => (Report: activations.Read(ct), Root: landing.BaseDirectory))
            .Zip(landing.GetActivation(), (read, list) => (read.Report, read.Root, List: list))
            .SelectMany(state =>
            {
                if (state.Report.IsUndetermined)
                    return Observable.Return(new ModuleLiveActivationResult([], state.Report.UndeterminedReason));
                var pending = state.Report.Pending;
                if (pending.IsEmpty)
                    return Observable.Return(new ModuleLiveActivationResult([])
                    {
                        NothingPendingBecause = state.Report.Describe(),
                    });
                // 🚨 ONE swap for the whole pass (#6729): every pending module is committed first and
                // the hubs bound to any of them are recycled ONCE. A swap per module tore each owner
                // address down once per module, killing the successor its subscribers had just
                // re-activated each time — read by every sync stream as an owner recycling in a loop.
                return pending.ToObservable()
                    .Select(row => Resolve(row, state.List, state.Root))
                    .Concat()
                    .ToList()
                    .SelectMany(resolved =>
                    {
                        var paths = resolved.Where(r => r.Path is not null).Select(r => r.Path!).ToImmutableList();
                        return (paths.IsEmpty
                                ? Observable.Return(ImmutableList<ModuleSwapOutcome>.Empty)
                                : updater.SwapAll(paths, reason))
                            .Select(swapped =>
                            {
                                var next = 0;
                                return new ModuleLiveActivationResult(
                                    [.. resolved.Select(r => r.Outcome ?? swapped[next++])]);
                            });
                    });
            })
            .Do(result =>
            {
                if (!result.WentLive.IsEmpty || result.NeedsRestart)
                    logger?.LogInformation("[ModuleLiveUpdate] {Result}", result.Describe());
            })
            .Catch((Exception ex) => Observable.Return(new ModuleLiveActivationResult(
                [], $"{ex.GetType().Name}: {ex.Message}")));

    /// <summary>One pending module, resolved to the pinned generation to swap in — or to the outcome
    /// that means it is not swapped at all.</summary>
    private sealed record Resolved(ModuleSwapOutcome? Outcome, string? Path);

    private IObservable<Resolved> Resolve(
        PendingModuleActivation row, ModuleActivationList list, string root)
    {
        var entry = list.Entries.LastOrDefault(e => e.Enabled
            && string.Equals(e.Name, row.Name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return Observable.Return(new Resolved(new ModuleSwapOutcome(row.Name, ModuleSwapKind.Failed,
                $"{row.Name} is reported pending but the activation record names no enabled entry for it"), null));
        if (contexts.Current(entry.Name) is null)
            return Observable.Return(new Resolved(new ModuleSwapOutcome(entry.Name, ModuleSwapKind.NotHeld,
                $"{entry.Name} does not run in its own load context in this process (the image binds it, or it "
                + "was not installed at boot) — a restart activates the landed generation"), null));
        return pool.InvokeBlocking(ct => ModuleGenerationPin.PinnedLoadPath(
                root, entry, ct, onWarn: warning => logger?.LogWarning("[ModuleLiveUpdate] {Warning}", warning)))
            .Select(path => new Resolved(null, path));
    }
}
