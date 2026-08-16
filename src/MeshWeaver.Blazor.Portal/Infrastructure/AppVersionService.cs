// ------------------------------------------------------------------------
// MIT License - Copyright (c) Microsoft Corporation. All rights reserved.
// ------------------------------------------------------------------------

using System.Reflection;

namespace MeshWeaver.Blazor.Portal.Infrastructure;

/// <summary>
/// Resolves the application version from the executing assembly's informational version attribute.
/// </summary>
public class AppVersionService : IAppVersionService
{
    /// <summary>Gets the current application version string.</summary>
    public string Version
    {
        get => GetVersionFromAssembly();
    }

    /// <summary>
    /// Reads the deployment's injected run-numbered platform version
    /// (<see cref="MeshWeaver.Mesh.PlatformBuildInfo"/> — CI assemblies are commit-deterministic,
    /// so the <c>.ci.N</c> run number rides the image config, not the compiled attribute), falling
    /// back to the assembly's informational version with any trailing build metadata trimmed to a
    /// short commit hash.
    /// </summary>
    /// <returns>The version string, or an empty string when no version attribute is present.</returns>
    public static string GetVersionFromAssembly()
    {
        if (MeshWeaver.Mesh.PlatformBuildInfo.RuntimePlatformVersion is { } injected)
            return injected;
        var strVersion = string.Empty;
        var versionAttribute = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (versionAttribute != null)
        {
            var version = versionAttribute.InformationalVersion;
            var plusIndex = version.IndexOf('+');
            if (plusIndex >= 0 && plusIndex + 9 < version.Length)
            {
                strVersion = version[..(plusIndex + 9)];
            }
            else
            {
                strVersion = version;
            }
        }

        return strVersion;
    }
}
