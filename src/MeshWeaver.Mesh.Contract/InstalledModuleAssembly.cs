using System.Reflection;

namespace MeshWeaver.Mesh;

/// <summary>
/// One boot-installed module assembly (a <c>Modules:Assemblies</c> entry loaded by
/// <see cref="MeshBuilder.InstallAssemblies"/>), registered as an enumerable DI singleton — the
/// mesh-scoped record of "which modules does THIS deployment run" for surfaces that must treat
/// modules like platform (design #1644):
/// <list type="bullet">
/// <item>the in-mesh compile reference set — a module published into <c>modules/&lt;name&gt;/</c>
/// leaves <c>TRUSTED_PLATFORM_ASSEMBLIES</c>, so <c>MeshNodeCompilationService</c> composes its
/// references as the static TPA baseline PLUS these;</item>
/// <item>the bake fingerprint — the sorted installed-module MVID hash joins the usable-build
/// check, so a module upgrade invalidates baked node-type builds that could reference it.</item>
/// </list>
/// </summary>
/// <param name="Assembly">The loaded module assembly (Default ALC, file-backed).</param>
public sealed record InstalledModuleAssembly(Assembly Assembly)
{
    /// <summary>The assembly's MVID — the per-build identity the bake fingerprint hashes.</summary>
    public Guid Mvid => Assembly.ManifestModule.ModuleVersionId;
}
