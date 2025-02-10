// ------------------------------------------------------------------------
// MIT License - Copyright (c) Microsoft Corporation. All rights reserved.
// ------------------------------------------------------------------------

using System.Reflection;

namespace MeshWeaver.Portal.Shared.Web.Infrastructure;
internal class AppVersionService : IAppVersionService
{
    public string Version
    {
        get => GetVersionFromAssembly();
    }

    public static string GetVersionFromAssembly()
    {
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
