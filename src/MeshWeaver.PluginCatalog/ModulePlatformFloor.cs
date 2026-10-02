using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The DECLARED platform floor of the MODULE lane (#1664): whether the RUNNING platform version
/// satisfies a module's declared <c>minMeshVersion</c>, and if not, a sentence naming both sides.
///
/// <para>🚨 <b>The floor HOLDS again — through <see cref="PlatformFloor"/>
/// (<see cref="PackagePlatformFloorGate.HoldFor"/>), never through
/// <see cref="DeclineReason(string?)"/></b> (policy <c>package-min-mesh-version</c>,
/// <c>Doc/Architecture/ModuleAdoptionPolicy</c> R2). A package version is used only when the
/// running platform satisfies its declared floor; the decision is <see cref="PlatformFloor"/>
/// (run-number aware, and an unordered comparison proceeds as advisory). Evidence that re-armed it
/// (2026-09-27): Store 1.16 used <c>IPaymentProvider</c> billing-portal members and Hosting used
/// <c>DeploymentContent.AnnouncementKeySecret</c>, both synced onto instances running
/// <c>3.0.0-ci.9412</c>/<c>9414</c> — images predating those members — and 14 NodeTypes were left
/// with no usable assembly (CS0117/CS1061). The link probe measures a compiled MODULE; it cannot
/// see NodeType SOURCE compiled in the mesh, which is exactly what broke.</para>
///
/// <para><b>Why the SemVer comparison below does not decide at runtime.</b> Between #3648 and
/// the policy above the floor was advisory everywhere, because this type's
/// <see cref="NuGetVersionComparer"/> comparison ranks <c>ci &lt; rc &lt; clean</c>: on
/// 2026-09-07 every installed <c>Plugins/*</c> record carrying an <c>rc</c> or <c>3.0.0</c> floor
/// made memex-cloud's self-updater decline all 11 candidate releases, and every production portal
/// was held on the morning build for the whole day. That comparison stays for exactly two jobs:
/// the WORDING of the advisory lines on the status surfaces (<c>PluginBundleClient.LandFromBundle</c>,
/// <c>ModuleLandingService.LandCore</c>, the boot union, <c>ReleaseAvailability</c>,
/// <c>RequiredModuleStatus</c>, the activation report — none of which refuses on it), and the
/// PACK-time gate: <c>check-module-platform-floor.py</c> refuses a module whose declared floor the
/// platform it is compiled against cannot satisfy, and <c>ModulePlatformFloorScriptParityTest</c>
/// pins that script against the two-argument overload here, which is why it stays exactly what
/// it was.</para>
///
/// <para><b>Deliberately NOT the MVID gate.</b> MVID equality is BAKE semantics — a NodeType
/// assembly is compiled in-process against exact framework references, so only the identical build
/// is known-good, and <c>PrebuiltAssemblySeeder.DeclineReason</c> rightly refuses everything else.
/// A module is an ordinary .NET assembly binding by SIMPLE NAME; the bundle RECORDS the MVID it was
/// built against as metadata the update reconcile compares to tell a rebuild from a no-op
/// (Plugins#931), never as a refusal.</para>
///
/// <para>The advisory comparison is SemVer via <see cref="NuGetVersionComparer"/> (string order
/// silently picks wrong across <c>ci.900</c>/<c>ci.3758</c>); an ABSENT floor is no constraint — most
/// modules need none, and inventing one would be a claim the author never made.</para>
/// </summary>
public static class ModulePlatformFloor
{
    /// <summary>
    /// The RUNNING platform's version — <see cref="PlatformBuildInfo.RunningPlatformVersion"/>, the
    /// ONE reader every version decision uses (<see cref="PlatformFloor"/>, the prebuilt-adoption
    /// policy, GitSync). It used to read MeshWeaver.Graph's <c>AssemblyInformationalVersion</c>,
    /// which carries no run number, so the module lane and the content lane compared a floor
    /// against two different "running" versions. Null when the build carries no version.
    /// </summary>
    public static string? RunningVersion => PlatformBuildInfo.RunningPlatformVersion;

    /// <summary>
    /// The ADVISORY for a module declaring <paramref name="minMeshVersion"/> on THIS process: null
    /// when the running platform satisfies the floor (or none is declared); otherwise a sentence
    /// naming both versions. The production overload every fetch/land/boot/status call site uses,
    /// so the wording never varies — and none of them treats a non-null answer as a reason to
    /// refuse, hold or skip: the runtime HOLD is <see cref="PlatformFloor"/> alone.
    /// </summary>
    public static string? DeclineReason(string? minMeshVersion) =>
        DeclineReason(minMeshVersion, RunningVersion);

    /// <summary>
    /// The pure comparison (unit-testable without an assembly stamp): null = the floor is satisfied
    /// (or none is declared); otherwise the reason, naming BOTH versions so an operator can see
    /// which side is behind. This is the parity oracle of the pack-time lint
    /// (<c>ModulePlatformFloorScriptParityTest</c>), which is the one place the answer still gates.
    /// </summary>
    /// <param name="minMeshVersion">The module's declared platform floor, or null/blank for none.</param>
    /// <param name="runningVersion">The running platform's version, or null when unknown.</param>
    public static string? DeclineReason(string? minMeshVersion, string? runningVersion)
    {
        if (string.IsNullOrWhiteSpace(minMeshVersion))
            // No declared floor = no constraint. Modules bind by simple name; without a stated
            // requirement there is nothing to verify, and refusing here would block every module
            // that predates the field.
            return null;

        if (string.IsNullOrWhiteSpace(runningVersion))
            // A DECLARED floor that cannot be compared is said so, not waved through as satisfied:
            // the pack-time lint reds on it (the floor could not be checked is never "checked and
            // fine"), and at runtime the sentence rides the log line. Unreachable on a
            // normally-stamped build.
            return $"the module declares minMeshVersion {minMeshVersion} but the running "
                   + "platform's version could not be determined";

        return NuGetVersionComparer.Instance.Compare(runningVersion, minMeshVersion) < 0
            ? $"the module declares platform ≥ {minMeshVersion} but this deployment runs "
              + $"{runningVersion} — advisory: this SemVer wording decides nothing at runtime; "
              + "whether the version is held is PlatformFloor's decision (policy "
              + "package-min-mesh-version)"
            : null;
    }
}
