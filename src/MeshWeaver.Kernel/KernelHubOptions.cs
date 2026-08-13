namespace MeshWeaver.Kernel;

/// <summary>
/// Tuning knobs for hubs that host a kernel — which, via
/// <c>ActivityNodeType.CreateMeshNode</c>'s <c>.AddKernelSubHubHandlers()</c>, means every
/// <b>Activity</b> node hub. Optional: when not registered in DI the defaults below apply.
/// </summary>
public sealed record KernelHubOptions
{
    /// <summary>
    /// How long a kernel-hosting hub may sit with NO inbound message before it disposes itself
    /// (<c>KernelContainer.DisposeOnTimeout</c>). A one-shot timer, re-armed by every message the
    /// hub receives, so it fires only after a genuinely idle window.
    ///
    /// <para>🚨 <b>This is the ceiling on how long a finished <c>_Activity/compile-&lt;ts&gt;</c>
    /// node hub is retained</b> — the residual #1324 measures. It is the one reclaimer that reaches
    /// an Activity hub in the monolith (the host has no other idle collection for node hubs), and it
    /// is why that residual is a BOUNDED retention rather than a leak. It never fired before #1435
    /// because a finished activity's still-warm mirror posted a <c>HeartBeatEvent</c> every 45 s
    /// (<c>SyncStreamOptions.HeartbeatInterval</c>) expressly to keep the hub alive, re-arming this
    /// timer forever; releasing that mirror on the terminal write is what let the clock run.</para>
    ///
    /// <para>🚨 <b>Do NOT shorten this to make a memory number look better.</b> #1324 prohibits it
    /// by name, and #1435's finding is exactly why: the clocks were correct and something was
    /// resetting them, so a shorter window would have hidden the defect instead of curing it. It is
    /// settable so a TEST can assert the reclamation property inside a test budget
    /// (<c>CompileActivityHubRetentionTest</c>), and for a host that genuinely wants a different
    /// idle policy — not as a memory tuning knob. Default: 15 minutes.</para>
    /// </summary>
    public TimeSpan IdleDisconnectTimeout { get; init; } = TimeSpan.FromMinutes(15);
}
