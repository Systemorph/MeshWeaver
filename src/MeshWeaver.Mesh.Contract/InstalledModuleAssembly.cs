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

    /// <summary>
    /// 🚨 THE ONE READER of a module build's ORDERED version (#3934): its
    /// <see cref="AssemblyInformationalVersionAttribute"/> with the <c>+gitSha</c> build metadata
    /// stripped, or null when the assembly carries no stamp.
    ///
    /// <para><b>What it is for.</b> A compiled NodeType's dependency record states a FLOOR over
    /// this value — <c>min:&lt;version&gt;</c> — instead of the module's MVID, which moves on
    /// every compilation by construction and therefore declined every prebuilt bundle whenever a
    /// module was rebuilt anywhere (<c>Doc/Architecture/DependencyRecordFloor</c>). Producer and
    /// consumer must derive it identically or a floor is compared against a value nobody else
    /// computes, so both the portal's resolver and the bake host's go through this property.</para>
    ///
    /// <para>🚨 <b>Null is not a version, and never reads as one.</b> An unstamped module resolves
    /// its MVID instead, i.e. the pre-#3934 exact pin — inconclusive stays on the rebuild side.
    /// The metadata is stripped for the same reason <c>ModulePlatformFloor.RunningVersion</c>
    /// strips it: SemVer ignores it for ordering, and dropping it keeps a decline sentence
    /// readable.</para>
    /// </summary>
    public string? Version => VersionOf(Assembly);

    /// <summary>The pure half, so a producer holding a bare <see cref="Assembly"/> resolves the
    /// identical value.</summary>
    /// <param name="assembly">The module assembly.</param>
    public static string? VersionOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }
}
