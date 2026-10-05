namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 The LIVE half of a module reload: swap a landed generation into THIS running process. The
/// live module loader registers it (policy <c>module-live-update-default</c>,
/// <c>Doc/Architecture/LiveModuleUpdate</c>); a process with no registration cannot swap, and the
/// reload then takes its fallback — exactly one restart through <see cref="IModuleActivationRestart"/>.
///
/// <para>This interface is the reload's call site, never a second loader: the swap itself (load
/// N+1 into a fresh context, recycle the hubs its contributions configure, retire N) belongs to
/// the loader alone. A swap that cannot complete must leave generation N serving and answer a
/// <see cref="ModuleSwapOutcome"/> naming why — never throw past the reload, never half-swap.</para>
/// </summary>
public interface IModuleLiveActivation
{
    /// <summary>
    /// Whether this process can swap <paramref name="module"/> live at all — false for a module
    /// that declares <c>restartRequired</c> or is bound into the image. Pure over what the loader
    /// knows; the reload asks it BEFORE requesting a swap, so a module no process can swap goes
    /// straight to the one restart instead of waiting for a swap that will only fail.
    /// </summary>
    /// <param name="module">The module's entry-assembly name.</param>
    bool CanSwap(string module);

    /// <summary>
    /// Swaps the generation the activation record names for <paramref name="module"/> into this
    /// process. Cold; emits ONE outcome.
    /// </summary>
    /// <param name="module">The module's entry-assembly name.</param>
    /// <param name="reason">Why — for the loader's own log lines and the hubs' quiesce reason.</param>
    IObservable<ModuleSwapOutcome> Swap(string module, string reason);
}

/// <summary>What one live swap did.</summary>
/// <param name="Swapped">The new generation is serving in this process.</param>
/// <param name="Failure">Why it is not, by name — null when <paramref name="Swapped"/>.</param>
public sealed record ModuleSwapOutcome(bool Swapped, string? Failure = null);

/// <summary>
/// 🚨 The RESTART half of a module reload: ask the deployment to re-create its pods on the image
/// they run, so a landed generation loads — through the existing self-update restart path (a
/// self-patch restart, or <c>self-update-restart-pending</c> handed to the control lane, which
/// routes it to an unattended <c>Restart</c> with no approval and no confirmation). Registered by
/// the portal's self-updater; a host without one answers <see cref="ModuleRestartKinds.Unavailable"/>
/// by name from the reload itself.
/// </summary>
public interface IModuleActivationRestart
{
    /// <summary>
    /// Requests ONE restart. Never paced by the self-updater's roll floor: the reload is an
    /// explicit, authorised request and the caller issues it exactly once (it stamps
    /// <c>RestartRequestedAt</c> first). Cold; emits ONE outcome.
    /// </summary>
    /// <param name="reason">Why — carried into the announcement and the log.</param>
    IObservable<ModuleRestartOutcome> RequestRestart(string reason);
}

/// <summary>What the restart lane answered — <see cref="Kind"/> is a <see cref="ModuleRestartKinds"/> value.</summary>
/// <param name="Kind">How the restart was taken.</param>
/// <param name="Detail">The lane's own sentence.</param>
public sealed record ModuleRestartOutcome(string Kind, string Detail)
{
    /// <summary>Whether a restart is on its way (taken here, or handed to the control lane).</summary>
    public bool Scheduled =>
        string.Equals(Kind, ModuleRestartKinds.Restarted, StringComparison.Ordinal)
        || string.Equals(Kind, ModuleRestartKinds.HandedOver, StringComparison.Ordinal);
}

/// <summary>How a restart request was taken — open string constants.</summary>
public static class ModuleRestartKinds
{
    /// <summary>This install rolled its own workloads on the image they run.</summary>
    public const string Restarted = "Restarted";

    /// <summary>Handed to the control lane as <c>self-update-restart-pending</c>; the control plane
    /// opens the unattended Restart.</summary>
    public const string HandedOver = "HandedOver";

    /// <summary>No restart could be requested — the detail names why (no updater, no control inbox,
    /// a failed hand-over).</summary>
    public const string Unavailable = "Unavailable";
}
