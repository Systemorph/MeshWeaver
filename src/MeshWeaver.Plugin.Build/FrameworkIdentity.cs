using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace MeshWeaver.Plugin.Build;

/// <summary>
/// Resolves the framework identity a prebuilt assembly is bound to.
///
/// <para>🚨 <b>The identity is the MVID, not the package version.</b> The runtime compares
/// <c>NodeTypeDefinition.CompiledFrameworkVersion</c> against
/// <c>NodeTypeCompilationHelpers.FrameworkVersion</c>, which is the <b>Module Version Id of the
/// MeshWeaver.Graph assembly</b> — a content identity, chosen over
/// <c>AssemblyInformationalVersion</c> because deriving it from the version string forced a fresh
/// stamp into every build and destroyed incremental compilation.</para>
///
/// <para>So a package recording only <c>3.0.0-rc2</c> records something the runtime never looks at:
/// two builds can share a version string and differ in content, and the MVID is what says whether
/// the bytes are ABI-compatible with the running process. Recording the MVID is what lets an
/// installer decide honestly whether a prebuilt assembly may be seeded — and seeding one under the
/// live framework tag when it was built against different content is <b>actively harmful</b>: the
/// store hit suppresses the rebuild that was needed, and the mismatch surfaces as a
/// <c>TypeLoadException</c> inside an ALC at activation, where there is no diagnostic and no
/// overlay.</para>
/// </summary>
public static class FrameworkIdentity
{
    /// <summary>The assembly whose MVID IS the framework identity.</summary>
    public const string IdentityAssembly = "MeshWeaver.Graph";

    /// <summary>
    /// Reads an assembly's MVID without loading it — metadata only, so it cannot execute a module
    /// initializer or pin the file in an ALC.
    /// </summary>
    public static string ReadMvid(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        return mvid.ToString("N");
    }

    /// <summary>
    /// The MVID of the <see cref="IdentityAssembly"/> a unit compiled against, or null when the
    /// restored package cannot be located. Null is reported rather than guessed: an installer that
    /// cannot establish the identity must fall back to compiling, never seed on faith.
    /// </summary>
    public static string? ResolveFrameworkMvid(string frameworkVersion)
    {
        var packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                           ?? Path.Combine(
                               Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                               ".nuget", "packages");

        var candidate = Path.Combine(
            packagesRoot,
            IdentityAssembly.ToLowerInvariant(),
            frameworkVersion,
            "lib", "net10.0", IdentityAssembly + ".dll");

        return File.Exists(candidate) ? ReadMvid(candidate) : null;
    }
}
