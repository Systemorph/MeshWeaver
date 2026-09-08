using System.Reflection;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The DECLARED platform floor of the MODULE lane (#1664): whether the RUNNING platform version
/// satisfies a module's declared <c>minMeshVersion</c>, and if not, a sentence naming both sides.
///
/// <para>🚨 <b>ADVISORY at runtime since #3648 — it decides nothing.</b> Maintainer directive of
/// 2026-09-07 (<c>Doc/Architecture/ModuleAdoptionPolicy</c>, rule R2): whether a module loads is
/// MEASURED — the type-level link probe <c>MeshWeaver.Mesh.ModulePlatformLink</c> at landing and at
/// boot, plus the actual load — never declared by a version string. The floor is a CLAIM a module
/// author writes by hand, compared by a total order that encodes a release POLICY rather than
/// compatibility: <see cref="NuGetVersionComparer"/> ranks <c>ci &lt; rc &lt; clean</c>, so on
/// 2026-09-07 every installed <c>Plugins/*</c> record carrying an <c>rc</c> or <c>3.0.0</c> floor
/// made memex-cloud's self-updater decline all 11 candidate releases ("77 plugins required … every
/// one declined") — every one of which the link probe would have loaded — and every production
/// portal was held on the morning build for the whole day. The floor had already been wrong the
/// other way round two days earlier (#3538): a declared <c>3.0.0-rc8</c> was SATISFIED by
/// <c>3.0.0-rc9.ci.7693</c> while the bytes were linked against a type that platform did not have.
/// A string can be unsatisfiable and satisfied-yet-wrong; the probe reads the bytes and is neither.</para>
///
/// <para><b>What the answer is used for now.</b> <see cref="DeclineReason(string?)"/> still names
/// both versions, and every runtime decision point LOGS that sentence (Information) and carries it
/// onto the status surfaces as "declares platform ≥ X; running Y" — <c>ModuleUpdateDecision</c>,
/// <c>PluginBundleClient.LandFromBundle</c>, <c>ModuleLandingService.LandCore</c>, the boot union,
/// <c>ReleaseAvailability</c> (an advisory beside the verdict, never in <c>IsUpdatable</c>),
/// <c>RequiredModuleStatus</c> and the activation report. None of them refuses, holds or skips on
/// it. Its one remaining GATE is at PACK time: <c>check-module-platform-floor.py</c> refuses a
/// module whose declared floor the platform it is compiled against cannot satisfy — an authoring
/// error — and <c>ModulePlatformFloorScriptParityTest</c> pins that script against the two-argument
/// overload here, which is why the comparison stays exactly what it was.</para>
///
/// <para><b>Deliberately NOT the MVID gate.</b> MVID equality is BAKE semantics — a NodeType
/// assembly is compiled in-process against exact framework references, so only the identical build
/// is known-good, and <c>PrebuiltAssemblySeeder.DeclineReason</c> rightly refuses everything else.
/// A module is an ordinary .NET assembly binding by SIMPLE NAME; the bundle RECORDS the MVID it was
/// built against as metadata the update reconcile compares to tell a rebuild from a no-op
/// (Plugins#931), never as a refusal.</para>
///
/// <para>The comparison is SemVer via <see cref="NuGetVersionComparer"/> (string order silently
/// picks wrong across <c>ci.900</c>/<c>ci.3758</c>); an ABSENT floor is no constraint — most
/// modules need none, and inventing one would be a claim the author never made.</para>
/// </summary>
public static class ModulePlatformFloor
{
    /// <summary>
    /// The RUNNING platform's version: MeshWeaver.Graph's <c>AssemblyInformationalVersion</c>
    /// (stamped centrally by <c>Directory.Build.props</c>), with the <c>+gitSha</c> build metadata
    /// stripped (SemVer ignores it for ordering; stripping keeps log lines readable). Null when
    /// the assembly carries no version stamp — which <see cref="DeclineReason(string?)"/> treats
    /// as "cannot verify a declared floor".
    /// </summary>
    public static string? RunningVersion { get; } = Resolve();

    private static string? Resolve()
    {
        var version = typeof(PrebuiltAssemblySeeder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    /// <summary>
    /// The ADVISORY for a module declaring <paramref name="minMeshVersion"/> on THIS process: null
    /// when the running platform satisfies the floor (or none is declared); otherwise a sentence
    /// naming both versions. The production overload every fetch/land/boot/status call site uses,
    /// so there is never a second notion of the floor — and since #3648 none of them treats a
    /// non-null answer as a reason to refuse, hold or skip.
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
              + $"{runningVersion} — advisory (#3648): whether it loads is measured by the link "
              + "probe, never by this comparison"
            : null;
    }
}
