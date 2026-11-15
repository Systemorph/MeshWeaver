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

    /// <summary>The directory under the shared root that holds one landed-module tree per platform
    /// major line from <see cref="LastSharedRootMajor"/> + 1 onward: <c>&lt;root&gt;/lines/&lt;major&gt;/</c>.</summary>
    public const string LinesDirectoryName = "lines";

    /// <summary>
    /// The last platform major whose landed-module tree is the shared root ITSELF
    /// (<c>&lt;root&gt;/modules/</c>). Every image up to and including this major was built before
    /// module trees were split by line, so it reads and writes the root's own <c>modules/</c> and can
    /// never be taught otherwise. Every later major gets a tree of its own under
    /// <see cref="LinesDirectoryName"/>.
    /// </summary>
    public const int LastSharedRootMajor = 3;

    /// <summary>
    /// 🚨 The root of THIS process's landed-module tree — the directory whose <c>modules/</c> holds
    /// the generations, the activation records (<c>activation.d/</c>), the module sets
    /// (<c>sets/</c>), the markers and every other file the module lane reads or writes (policy
    /// <c>major-lines-run-separately</c>, Doc/Architecture/Modules → "One module tree per platform
    /// line").
    ///
    /// <para><b>Why the tree is split by line.</b> Every replica of a deployment mounts one shared
    /// volume, and during a roll from one platform major to the next, replicas of BOTH majors run on
    /// it at once. A module built against one major's libraries does not load on the other (a v3 view
    /// pack links UI types the v4 platform no longer ships), yet a module's activation record names
    /// ONE head per module name. With one tree, the new-major replica booted the old major's heads and
    /// never became ready, while the old-major replicas kept landing their own generations into the
    /// same records, so the new major could not even keep a head it had landed.</para>
    ///
    /// <para><b>The rule.</b> With no configured root (<see cref="ConfigKey"/> absent) the tree is the
    /// image's own directory, which no other image ever sees, and this is
    /// <see cref="Resolve(IConfiguration?)"/> unchanged. With a configured — shared — root, a process
    /// whose major is at most <see cref="LastSharedRootMajor"/> (or unknown) uses the root itself,
    /// exactly as every image before the split did; a later major uses
    /// <c>&lt;root&gt;/lines/&lt;major&gt;/</c>. So the two majors share no path: a new-major replica
    /// never reads, adopts, overwrites or garbage-collects an old-major record or generation, and the
    /// old-major images need no change at all — their sweep enumerates <c>&lt;root&gt;/modules/</c>
    /// only, which <c>lines/</c> is not under.</para>
    ///
    /// <para>🚨 <b>Only the MODULE tree moves.</b> Everything else kept under the root — the instance
    /// setup manifest, the boot progress — belongs to the INSTANCE, not to a platform line, and keeps
    /// <see cref="Resolve(IConfiguration?)"/>. A caller that reads or writes anything under
    /// <c>modules/</c> resolves its root here.</para>
    ///
    /// <para>The major is the running CORE's own assembly major (<see cref="RunningMajor"/>), never a
    /// configured line or update pattern: what decides whether a module's bytes load is the binary
    /// this process runs, and during a roll the configuration every replica reads can already name
    /// the next line while an old-major replica is still running.</para>
    /// </summary>
    /// <param name="configuration">The instance configuration, or null.</param>
    /// <returns>The directory whose <c>modules/</c> is this process's landed-module tree.</returns>
    public static string ResolveLine(IConfiguration? configuration) =>
        ResolveLine(configuration?[ConfigKey], RunningMajor);

    /// <summary>The pure form of <see cref="ResolveLine(IConfiguration?)"/>: the configured root and
    /// the major as already read.</summary>
    /// <param name="configured">The <see cref="ConfigKey"/> value, or null.</param>
    /// <param name="major">The running platform major, or null when unknown.</param>
    /// <returns>The directory whose <c>modules/</c> is the landed-module tree for that major.</returns>
    public static string ResolveLine(string? configured, int? major) =>
        string.IsNullOrWhiteSpace(configured)
            ? AppContext.BaseDirectory
            : ForLine(configured.Trim(), major);

    /// <summary>
    /// The landed-module tree root for <paramref name="major"/> under the shared
    /// <paramref name="root"/>: the root itself through <see cref="LastSharedRootMajor"/> (and for an
    /// unknown major), <c>&lt;root&gt;/lines/&lt;major&gt;/</c> after it. Pure.
    /// </summary>
    /// <param name="root">The shared root.</param>
    /// <param name="major">The platform major, or null when unknown.</param>
    /// <returns>The directory whose <c>modules/</c> is that major's tree.</returns>
    public static string ForLine(string root, int? major)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return major is { } line && line > LastSharedRootMajor
            ? System.IO.Path.Combine(root, LinesDirectoryName,
                line.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : root;
    }

    /// <summary>
    /// The major of the platform this process RUNS, read off the core's own assembly version — which
    /// the build pins to <c>&lt;major&gt;.&lt;minor&gt;.0.0</c> for a whole line, so it is never
    /// "unknown" on a real build and cannot be moved by configuration.
    /// </summary>
    public static int? RunningMajor { get; } =
        typeof(ModuleRoot).Assembly.GetName().Version is { Major: > 0 } version ? version.Major : null;
}
