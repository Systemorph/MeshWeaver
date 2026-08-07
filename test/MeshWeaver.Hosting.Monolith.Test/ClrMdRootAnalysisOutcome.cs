namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Result of a ClrMD GC-root analysis. Tri-state on purpose (#674): on macOS
/// <c>DataTarget.CreateSnapshotAndAttach</c> throws <c>PlatformNotSupportedException</c>,
/// and folding that into a bare <c>false</c> made the leak guards pass unconditionally
/// on every Mac — a probe run alongside showed the hub demonstrably alive while the
/// assertion stayed green, and the false assurance derailed the #664/#673 investigations
/// (22 green macOS runs "refuted" a correlation that Linux then reproduced 3/3).
/// <see cref="Unavailable"/> must never satisfy a leak assertion; tests skip loudly on it.
/// </summary>
internal enum ClrMdRootAnalysisOutcome
{
    /// <summary>The analysis could not run (platform, snapshot, or runtime failure) — inconclusive, never a pass.</summary>
    Unavailable,

    /// <summary>The analysis ran and found no target object reachable from a non-stack root.</summary>
    NotDetected,

    /// <summary>The analysis ran and found the target object rooted — the leak signature.</summary>
    Detected,
}
