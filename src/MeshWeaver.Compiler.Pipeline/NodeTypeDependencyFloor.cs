namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 <b>A process that does not run what a NodeType's package declares it needs never forms a
/// verdict about that NodeType's code</b> (MeshWeaver#6195). The compile half of the
/// dependency-floor rule whose import half is <c>ModuleSyncDecision.DeclineUnmetRequirements</c>.
///
/// <para><b>What was missing.</b> A package's <c>requires</c> floor (<c>AI@^1.40.0</c>) was judged
/// where SOURCES are written — the GitSync import, the installer — against the module THAT process
/// loaded. The compile judged nothing. During a roll the replicas run different generations of a
/// dependency, and the per-node NodeType hub is placed on whichever replica asks first; a replica
/// still running the older dependency then compiled sources another replica had legitimately
/// written, failed with <c>CS0246</c>/<c>CS0103</c> on exactly the members the floor names, and
/// stamped <c>CompilationStatus.Error</c> onto the ONE shared record every replica
/// reads. Measured on memex-cloud 2026-10-28: Hosting 1.73 declares <c>AI@^1.40.0</c>; the replicas
/// of the outgoing generation ran AI 1.39.8 (their own import had declined Hosting on that ground
/// every ten minutes for hours); at 04:54:51Z the <c>Hosting/Deployment</c> hub activated on one of
/// them, compiled, and failed with <c>CS0246 FillOutcome</c> / <c>CS0103 NodeDescriptions</c> —
/// symbols AI 1.40 ships. The record read <c>Error</c> for 25 minutes, the reboot watchdog
/// declared the instance WEDGED, and only the next roll's boot bake cleared it.</para>
///
/// <para><b>The rule.</b> Before a compile reaches Roslyn — or loads a cached build — the
/// compilation service asks this contract whether THIS process meets the floor of the package
/// that owns the NodeType. An unmet floor ends the attempt with
/// <see cref="DependencyFloorUnmetException"/>, which <see cref="NodeTypeCompilationHelpers.IsAvailabilityNonVerdict"/>
/// classifies as "no verdict": the record is stamped <c>CompilationStatus.Unavailable</c>
/// (never <c>Error</c>), the type is not parked, and its last good build — the one a replica
/// that DOES meet the floor compiled — keeps its coordinates.</para>
///
/// <para><b>Abstains, never guesses.</b> No implementation registered (a mesh without a module
/// host), a NodeType whose package declares no requirement, a dependency this process has no
/// loaded version for, a range that cannot be read, or a failed read of the package root: each
/// answers <c>null</c>, and the compile runs exactly as it did before this rule existed. Same
/// "absent means not judged, never met or unmet" contract as the import's rule.</para>
///
/// <para><b>The platform floor is the same floor (#7467).</b> The package root's
/// <c>minMeshVersion</c> is judged by the same contract, before its <c>requires</c>: a replica whose
/// image runs an older platform than the package declares answers "package X requires platform ≥ N;
/// this instance runs M", so it can no longer compile sources a newer replica synced and fail on a
/// platform member its image lacks.</para>
/// </summary>
public interface INodeTypeDependencyFloor
{
    /// <summary>
    /// Whether this process meets the declared dependency floor of the package that owns
    /// <paramref name="nodeTypePath"/>.
    /// </summary>
    /// <param name="nodeTypePath">The NodeType's own path (<c>Hosting/Deployment</c>).</param>
    /// <returns>Cold; emits exactly once: <c>null</c> when the floor is met or nothing could be
    /// judged, otherwise a sentence naming the requirement, the module this process runs for it
    /// and that module's version. Never faults — a failed reading answers <c>null</c>.</returns>
    IObservable<string?> Unmet(string nodeTypePath);
}

/// <summary>
/// The compile of <see cref="NodeTypePath"/> did not run because this process does not meet the
/// declared dependency floor of the package that owns it (<see cref="INodeTypeDependencyFloor"/>).
/// An AVAILABILITY fact about the replica, never a statement about the code: see
/// <see cref="NodeTypeCompilationHelpers.IsAvailabilityNonVerdict"/>.
/// </summary>
/// <param name="nodeTypePath">The NodeType whose compile was not run.</param>
/// <param name="unmet">The sentence <see cref="INodeTypeDependencyFloor.Unmet"/> returned.</param>
public sealed class DependencyFloorUnmetException(string nodeTypePath, string unmet)
    : Exception(
        $"Compile of '{nodeTypePath}' NOT RUN on this replica: {unmet} — a replica that meets the "
        + "package's floor (its dependencies and its platform) compiles it; this one forms no verdict "
        + "and leaves the last good build in place (MeshWeaver#6195, #7467).")
{
    /// <summary>The NodeType whose compile was not run.</summary>
    public string NodeTypePath { get; } = nodeTypePath;

    /// <summary>The unmet requirement, as <see cref="INodeTypeDependencyFloor.Unmet"/> stated it.</summary>
    public string Unmet { get; } = unmet;
}
