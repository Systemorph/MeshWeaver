using System.Reflection;

namespace MeshWeaver.Mesh;

/// <summary>
/// WHICH BUILD is this process — the one answer every version surface reads, so they cannot
/// disagree.
///
/// <para>Two stamps carry the identity, and they are produced in different places on purpose. The
/// run-numbered platform version (e.g. <c>3.0.0-rc3.ci.3961</c>) rides the container IMAGE CONFIG
/// as an environment variable, because compiled assemblies are COMMIT-DETERMINISTIC (issue #1660
/// WS3): baking the run number in would fork the framework identity between the CI run that bakes
/// NodeType assemblies and the CD run that builds the image. The commit sha and the base version
/// are ASSEMBLY metadata, stamped by <c>AddCommitHashMetadata</c> in the root
/// <c>Directory.Build.props</c>.</para>
///
/// <para>🚨 <b>The entry assembly is not the build.</b> Reading <c>Assembly.GetEntryAssembly()</c>
/// blindly reports whatever host happens to have started the process, and on two hosts that
/// matter it is not part of this build at all: a test runner (<c>test/Directory.Build.props</c>
/// deliberately does not import the root props) and — since the portal hosts moved to
/// MeshWeaver.Plugins on 2026-08-25 — the deployed portal executable itself, which is built in a
/// repo that defines no <c>Version</c>. Both report the SDK default <c>1.0.0</c> and no commit.
/// <see cref="SelectBuildAssembly"/> is the guard: an entry assembly this build did not stamp is
/// refused in favour of one it did.</para>
///
/// <para>That guard used to exist in exactly one of the two readers. <c>/api/version</c> had it;
/// the About page and the self-updater did not — so the deployed portal answered
/// <c>3.0.0-rc9+0a1eabdc…</c> on the endpoint and <c>1.0.0</c> on the page, and because
/// <c>VersionSelect.IsNewer</c> compares against that number, every registry tag looked newer
/// forever: the install could never reach "up to date" and re-rolled on every check floor. The
/// selection lives here, below both readers, so there is no second copy to drift.</para>
/// </summary>
public static class PlatformBuildInfo
{
    /// <summary>
    /// The environment variable carrying the deployment's run-numbered platform version. Consumers
    /// (the self-updater's current-version, the Admin/PlatformVersion node, version displays) read
    /// it FIRST and fall back to the build assembly's <c>AssemblyInformationalVersion</c> —
    /// which under CI determinism is the bare <c>PlatformVersion+&lt;sha&gt;</c>, still correct,
    /// just without the run number.
    /// </summary>
    public const string PlatformVersionEnvironmentVariable = "MESHWEAVER_PLATFORM_VERSION";

    /// <summary>The assembly metadata key <c>AddCommitHashMetadata</c> writes the git sha to.</summary>
    private const string CommitHashKey = "CommitHash";

    /// <summary>
    /// The injected run-numbered platform version, or null when the process runs without one
    /// (local dev, tests — any non-container start).
    /// </summary>
    public static string? RuntimePlatformVersion =>
        Environment.GetEnvironmentVariable(PlatformVersionEnvironmentVariable) is { Length: > 0 } v
            ? v
            : null;

    /// <summary>
    /// The assembly that represents this build: the entry assembly when this build stamped it,
    /// otherwise an assembly that IS part of this build. One build stamps every one of its
    /// assemblies with the same commit, so the fallback answer is the real one rather than a
    /// foreign host's.
    /// </summary>
    /// <param name="entry">
    /// The process entry assembly — <c>null</c> is treated as "not part of this build" (a host
    /// that reports none, e.g. an unmanaged launcher). Taken as a parameter so the selection is
    /// testable without controlling the process.
    /// </param>
    public static Assembly SelectBuildAssembly(Assembly? entry) =>
        entry is not null && CommitOf(entry) is not null ? entry : typeof(PlatformBuildInfo).Assembly;

    /// <summary>
    /// The build identity of THIS process, resolved once — the values are compile-time constants
    /// baked into the assembly, so re-reading them per call would buy nothing.
    /// </summary>
    public static Assembly BuildAssembly { get; } = SelectBuildAssembly(Assembly.GetEntryAssembly());

    /// <summary>
    /// This build's version as the ASSEMBLY carries it: the informational version (<c>
    /// PlatformVersion+&lt;sha&gt;</c> on a CI build), then the numeric assembly version, then
    /// <c>"unknown"</c>. Deliberately does NOT consult the injected run number — a surface that
    /// promises assembly metadata (<c>/api/version</c>) must report exactly that; the surfaces that
    /// want the run number read <see cref="PlatformVersion"/>.
    /// </summary>
    public static string AssemblyVersion =>
        BuildAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? BuildAssembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>
    /// The version the platform IDENTIFIES ITSELF BY: the injected run-numbered version when the
    /// container publish supplied one, else what the assembly carries. This is the value the
    /// self-updater compares against registry tags, so the run number must be visible here.
    /// </summary>
    public static string PlatformVersion => RuntimePlatformVersion ?? AssemblyVersion;

    /// <summary>
    /// The git commit sha this build was produced from, or <c>null</c> when the build carried no
    /// source-control information (a git-less source drop).
    /// </summary>
    public static string? CommitHash => CommitOf(BuildAssembly);

    private static string? CommitOf(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, CommitHashKey, StringComparison.OrdinalIgnoreCase))
            ?.Value is { Length: > 0 } sha
            ? sha
            : null;
}
