namespace MeshWeaver.Mesh;

/// <summary>
/// How a RUNNING process learns its full, run-numbered platform version (e.g.
/// <c>3.0.0-rc3.ci.3961</c>) now that compiled assemblies are COMMIT-DETERMINISTIC (issue #1660
/// WS3): the run number must not be compiled in — it would fork the framework identity between the
/// CI run that bakes NodeType assemblies and the CD run that builds the image — so the container
/// publish injects it as an environment variable instead (image CONFIG, not assembly bytes; see
/// the <c>ContainerEnvironmentVariable</c> item in <c>Memex.Portal.Distributed.csproj</c> and the
/// version-channel note in <c>Directory.Build.props</c>).
/// </summary>
public static class PlatformBuildInfo
{
    /// <summary>
    /// The environment variable carrying the deployment's run-numbered platform version. Consumers
    /// (the self-updater's current-version, the Admin/PlatformVersion node, version displays) read
    /// it FIRST and fall back to the entry assembly's <c>AssemblyInformationalVersion</c> —
    /// which under CI determinism is the bare <c>PlatformVersion+&lt;sha&gt;</c>, still correct,
    /// just without the run number.
    /// </summary>
    public const string PlatformVersionEnvironmentVariable = "MESHWEAVER_PLATFORM_VERSION";

    /// <summary>
    /// The injected run-numbered platform version, or null when the process runs without one
    /// (local dev, tests — any non-container start).
    /// </summary>
    public static string? RuntimePlatformVersion =>
        Environment.GetEnvironmentVariable(PlatformVersionEnvironmentVariable) is { Length: > 0 } v
            ? v
            : null;
}
