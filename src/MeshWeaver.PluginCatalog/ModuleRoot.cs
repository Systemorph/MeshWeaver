using Microsoft.Extensions.Configuration;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The deployment root the writable <c>modules/</c> tree lives under — ONE resolution, shared by
/// the writer (<see cref="ModuleLandingService"/>) and by boot-time activation, because they must
/// name the same directory or a landed module is written where nothing ever looks for it.
///
/// <para>🚨 <b>Why this is configurable at all: <c>AppContext.BaseDirectory</c> is READ-ONLY in the
/// container, and the landing path cannot tolerate that.</b> Boot already degrades gracefully there
/// (the <c>PendingRestart</c> reset is best-effort and documented as cosmetic), which is exactly why
/// the defect stayed invisible: everything that only READS <c>/app/modules</c> works, and the first
/// caller that must WRITE it fails. That caller is the registry's publish route, and it failed the
/// whole module lane with
/// <c>Access to the path '/app/modules/.staging-…' is denied</c> — surfaced to the build as
/// HTTP 409, four steps from the cause.</para>
///
/// <para>🚨 <b>And a writable path is not merely a workaround — a per-POD directory would be the
/// wrong place even if it were writable.</b> A module landed by whichever replica served the
/// publish must be visible to every other replica, so the root has to be the deployment's SHARED
/// volume. On AKS that is the RWX <c>/data</c> PVC every portal pod already mounts.</para>
///
/// <para>The default is deliberately unchanged (<c>AppContext.BaseDirectory</c>): an unconfigured
/// deployment, a dev run and every test behave exactly as before, so this key only ever MOVES the
/// tree for a deployment that sets it.</para>
/// </summary>
public static class ModuleRoot
{
    /// <summary>
    /// Configuration key holding the writable module root. Absent/blank keeps
    /// <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public const string ConfigKey = "Modules:Root";

    /// <summary>
    /// The configured root, or <see cref="AppContext.BaseDirectory"/> when none is set.
    /// Blank is treated as absent — an empty <c>Modules:Root=</c> is a deployment that meant to
    /// unset the key, never a request to root the tree at the process's working directory.
    /// </summary>
    public static string Resolve(IConfiguration? configuration) =>
        Resolve(configuration?[ConfigKey]);

    /// <summary>Pure form, for tests and for callers that already read the value.</summary>
    public static string Resolve(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? AppContext.BaseDirectory : configured.Trim();
}
