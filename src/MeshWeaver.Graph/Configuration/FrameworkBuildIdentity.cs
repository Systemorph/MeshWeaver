using System.Reflection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// THE framework build identity every compiled NodeType release is pinned to (issue #1660 WS3) —
/// the value <see cref="NodeTypeCompilationHelpers.FrameworkVersion"/> resolves and every other
/// reading (the assembly-store filename tag, <c>CompiledFrameworkVersion</c> stamps, the CI bake
/// artifact key, <c>PrebuiltAssemblySeeder</c>'s adoption gate, the build-protocol fingerprint)
/// flows from. One identity, one resolution — a producer and a consumer can never disagree about
/// what "the framework" is.
///
/// <para><b>Two schemes, discriminated by how the build was made:</b></para>
/// <list type="bullet">
/// <item><description><b>CI builds (CIRun)</b> carry
/// <c>AssemblyMetadata("MeshWeaverFrameworkIdentity", "g&lt;commit-sha&gt;")</c>, stamped by the
/// <c>AddCommitHashMetadata</c> target in <c>Directory.Build.props</c>. The identity is the COMMIT:
/// CI compile inputs are commit-deterministic (no per-build ticks or run numbers reach any compiled
/// attribute), so two CI builds of the same commit — the Build-and-Test run that bakes NodeType
/// assemblies and the main-cd run that builds the image — share the identity, and the baked
/// assemblies seed at portal boot instead of recompiling. Any code or package-pin change is a new
/// commit and therefore a new identity: a commit names the ENTIRE tree, so this covers the WHOLE
/// NodeType compile reference set (the TPA — <c>MeshNodeCompilationService.GetDefaultReferences</c>),
/// not just Graph's own dependency closure — the widening the old per-build-ticks scheme's 🚨
/// comment demanded before that stamp could be removed.</description></item>
/// <item><description><b>Local builds</b> carry no stamp and resolve to MeshWeaver.Graph's MVID —
/// a content identity that is exact for a dirty working tree (a commit identity would
/// under-invalidate there: two different local builds can sit on the same commit) and stable
/// across incremental rebuilds that don't change Graph's bytes.</description></item>
/// </list>
/// </summary>
public static class FrameworkBuildIdentity
{
    /// <summary>
    /// The <see cref="AssemblyMetadataAttribute"/> key carrying the CI build identity — stamped
    /// into every assembly by <c>Directory.Build.props</c> (target <c>AddCommitHashMetadata</c>)
    /// when <c>CIRun=true</c> and a commit SHA is resolvable. The value shape is
    /// <c>g&lt;full-commit-sha&gt;</c>.
    /// </summary>
    public const string MetadataKey = "MeshWeaverFrameworkIdentity";

    /// <summary>
    /// The pure resolution rule, unit-testable without controlling how the test assembly was
    /// built: a non-blank stamped identity wins; otherwise the caller's content identity
    /// (the Graph MVID) applies.
    /// </summary>
    /// <param name="stamped">The <see cref="MetadataKey"/> attribute value, or null when the
    /// assembly carries none (every local build).</param>
    /// <param name="contentIdentity">The fallback content identity (Graph's MVID, "N" format).</param>
    public static string Resolve(string? stamped, string contentIdentity) =>
        string.IsNullOrWhiteSpace(stamped) ? contentIdentity : stamped;

    /// <summary>
    /// Reads the stamped <see cref="MetadataKey"/> value off a LOADED assembly, or null when the
    /// assembly carries none. (For reading the same stamp off an assembly FILE without loading it,
    /// see <c>MeshWeaver.Plugin.Build.FrameworkIdentity.ReadIdentity</c>.)
    /// </summary>
    public static string? StampedIdentityOf(Assembly assembly) =>
        assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, MetadataKey, StringComparison.Ordinal))
            ?.Value is { Length: > 0 } value
            ? value
            : null;
}
