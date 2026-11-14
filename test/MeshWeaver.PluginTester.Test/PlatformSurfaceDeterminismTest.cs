using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The surface a bake publishes (<see cref="ModulePlatformSurface.OfRunningPlatform"/>) describes
/// what binds on this platform. It must not depend on which framework assemblies the process happens
/// to have loaded when the bake writes it. A shared-framework assembly sits in the trusted platform
/// list whether or not anything has loaded it yet. Measured from the loaded set alone, a parallel bake
/// that touched one more framework assembly than its serial twin published a different
/// <c>platform-surface.json</c> (<see cref="ParallelBakeTest"/>, first seen on the .NET 11 runtime).
/// </summary>
public sealed class PlatformSurfaceDeterminismTest
{
    [Fact]
    public void LoadingAFrameworkAssembly_DoesNotChangeThePublishedSurface()
    {
        var notYetLoaded = TrustedPlatformAssemblyNotLoaded();
        Assert.True(notYetLoaded is not null,
            "the trusted platform list holds no System.* assembly this process has not loaded — the test cannot set up its own precondition");

        var before = ModulePlatformSurface.OfRunningPlatform(AppContext.BaseDirectory).ToJson("probe");
        AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(notYetLoaded!));
        Assert.Contains(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == notYetLoaded);
        var after = ModulePlatformSurface.OfRunningPlatform(AppContext.BaseDirectory).ToJson("probe");

        // The assembly KEY, not the name: a type of that name is listed under System.Runtime anyway.
        Assert.Contains($"\"{notYetLoaded}\":[", before);
        Assert.Equal(before, after);
    }

    private static string? TrustedPlatformAssemblyNotLoaded()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        return tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && n.StartsWith("System.", StringComparison.Ordinal)
                        && !n.EndsWith(".Native", StringComparison.Ordinal) && !loaded.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
