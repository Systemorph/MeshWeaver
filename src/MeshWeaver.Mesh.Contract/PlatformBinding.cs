namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 <b>THE binding rule for a platform-shared assembly reference</b> out of a plugin, module or
/// NodeType assembly — policy <c>platform-backwards-compatibility</c>
/// (<c>Doc/Architecture/PolicyNotProse</c>).
///
/// <para>Bind to the RUNNING platform's assembly whenever it is the SAME or a HIGHER version than
/// the one compiled against: never a hard link to the exact version, never a private copy because
/// the requested version is lower. <c>AssemblyVersion</c> is pinned per MAJOR (<c>3.0.0.0</c> for
/// every 3.x build, policy <c>platform-semver-versioning</c>), so within a major every plugin binds
/// and the version half decides only ACROSS a major: a plugin compiled against <c>3.0.0.0</c> binds on
/// a platform stamped <c>4.0.0.0</c>, while a reference to a HIGHER version than the running one means
/// the plugin needs a newer platform and is declined loudly by the caller, naming both versions.
/// Within a major, a plugin built for a newer platform is declined by its floor
/// (<c>PlatformFloor</c>, <c>PlatformCompatibility.ProducerIsNewer</c>), not by binding.</para>
///
/// <para>Lives here, below both the module link probe (<see cref="ModulePlatformLink"/>, this
/// assembly) and the compile toolchain (<c>MeshWeaver.Compiler.PlatformCompatibility.MayBind</c>
/// forwards to it), so the probe that decides a module's loadability and every other reader apply
/// ONE comparison. Pure.</para>
/// </summary>
public static class PlatformBinding
{
    /// <summary>
    /// True when a reference compiled against <paramref name="compiledAgainst"/> binds to a running
    /// copy of <paramref name="running"/>: <c>running &gt;= compiledAgainst</c>. An unversioned
    /// reference (null or <c>0.0.0.0</c>) binds to any version.
    /// </summary>
    /// <param name="compiledAgainst">The AssemblyRef version the referencing assembly carries.</param>
    /// <param name="running">The version of the running platform's copy.</param>
    public static bool MayBind(Version? compiledAgainst, Version running)
    {
        ArgumentNullException.ThrowIfNull(running);
        return compiledAgainst is null
               || compiledAgainst == new Version(0, 0, 0, 0)
               || running >= compiledAgainst;
    }
}
