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

    /// <summary>One sentence naming what went live and what still needs a restart, and why.</summary>
    public string Describe()
    {
        if (Undetermined is not null)
            return $"the live activation could not establish what is pending here ({Undetermined})";
        var parts = new List<string>();
        if (!WentLive.IsEmpty)
            parts.Add("went live without a restart: " + string.Join(", ", WentLive.Select(o => o.Module)));
        if (!StillPending.IsEmpty)
            parts.Add("still needs a restart: " + string.Join("; ", StillPending.Select(o => $"{o.Module} ({o.Kind}: {o.Reason})")));
        return parts.Count == 0 ? "nothing landed is waiting to be activated here" : string.Join("; ", parts);
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
        pool.InvokeBlocking(_ => (Report: activations.Read(), Root: landing.BaseDirectory))
            .Zip(landing.GetActivation(), (read, list) => (read.Report, read.Root, List: list))
            .SelectMany(state =>
            {
                if (state.Report.IsUndetermined)
                    return Observable.Return(new ModuleLiveActivationResult([], state.Report.UndeterminedReason));
                var pending = state.Report.Pending;
                if (pending.IsEmpty)
                    return Observable.Return(new ModuleLiveActivationResult([]));
                return pending.ToObservable()
                    .Select(row => Activate(row, state.List, state.Root, reason))
                    .Concat()
                    .ToList()
                    .Select(outcomes => new ModuleLiveActivationResult([.. outcomes]));
            })
            .Do(result =>
            {
                if (!result.WentLive.IsEmpty || result.NeedsRestart)
                    logger?.LogInformation("[ModuleLiveUpdate] {Result}", result.Describe());
            })
            .Catch((Exception ex) => Observable.Return(new ModuleLiveActivationResult(
                [], $"{ex.GetType().Name}: {ex.Message}")));

    private IObservable<ModuleSwapOutcome> Activate(
        PendingModuleActivation row, ModuleActivationList list, string root, string reason)
    {
        var entry = list.Entries.LastOrDefault(e => e.Enabled
            && string.Equals(e.Name, row.Name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return Observable.Return(new ModuleSwapOutcome(row.Name, ModuleSwapKind.Failed,
                $"{row.Name} is reported pending but the activation record names no enabled entry for it"));
        if (contexts.Current(entry.Name) is null)
            return Observable.Return(new ModuleSwapOutcome(entry.Name, ModuleSwapKind.NotHeld,
                $"{entry.Name} does not run in its own load context in this process (the image binds it, or it "
                + "was not installed at boot) — a restart activates the landed generation"));
        return pool.InvokeBlocking(_ => ModuleGenerationPin.PinnedLoadPath(
                root, entry, onWarn: warning => logger?.LogWarning("[ModuleLiveUpdate] {Warning}", warning)))
            .SelectMany(path => updater.Swap(path, reason));
    }
}
