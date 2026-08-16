using System.Reflection;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The ONE platform gate of the MODULE lane (#1664): a module bundle may land when the RUNNING
/// platform version satisfies the module's declared <c>minMeshVersion</c> FLOOR.
///
/// <para><b>Deliberately NOT the MVID gate.</b> MVID equality is BAKE semantics — a NodeType
/// assembly is compiled in-process against exact framework references, so only the identical build
/// is known-good, and <c>PrebuiltAssemblySeeder.DeclineReason</c> rightly refuses everything else.
/// A module is an ordinary .NET assembly binding by SIMPLE NAME; its real contract is API
/// compatibility, which a semver floor expresses and an MVID cannot. Gating modules on MVID
/// equality would force rebundling every module on every CI build and forbid installing a module
/// ex post onto an older-or-newer platform — exactly the Store scenario the module lane exists
/// for. The bundle still RECORDS the MVID it was built against, but as DIAGNOSTIC metadata
/// (logged at landing, surfaced in the index), never a refusal.</para>
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
    /// Why a module declaring <paramref name="minMeshVersion"/> may not land on THIS process, or
    /// null when it may — the production overload every serve/fetch/land/boot call site uses, so
    /// there is never a second notion of the platform floor.
    /// </summary>
    public static string? DeclineReason(string? minMeshVersion) =>
        DeclineReason(minMeshVersion, RunningVersion);

    /// <summary>
    /// The pure decision (unit-testable without an assembly stamp): null = the floor is satisfied
    /// (or none is declared); otherwise the reason, naming BOTH versions so an operator can see
    /// which side is behind.
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
            // A DECLARED floor that cannot be checked is not waved through: landing on faith
            // surfaces later as a MissingMethodException with nothing connecting it to the
            // install. Unreachable on a normally-stamped build.
            return $"the module declares minMeshVersion {minMeshVersion} but the running "
                   + "platform's version could not be determined — not landing on faith";

        return NuGetVersionComparer.Instance.Compare(runningVersion, minMeshVersion) < 0
            ? $"the module requires platform {minMeshVersion} or newer but this deployment runs "
              + $"{runningVersion} — it becomes installable after the platform updates"
            : null;
    }
}
