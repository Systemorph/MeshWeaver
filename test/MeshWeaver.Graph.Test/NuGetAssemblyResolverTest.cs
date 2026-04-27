using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.NuGet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// End-to-end test of NuGet restore against api.nuget.org. Requires network access.
/// Disable with environment variable MESHWEAVER_SKIP_NUGET=1.
/// </summary>
[Collection("NuGetNetwork")]
public class NuGetAssemblyResolverTest
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("MESHWEAVER_SKIP_NUGET") == "1";

    [Fact(Timeout = 180_000)]
    public async Task Resolve_Humanizer_ReturnsExistingDllPaths()
    {
        if (ShouldSkip) return;
        var resolver = new NuGetAssemblyResolver(NullLogger<NuGetAssemblyResolver>.Instance);

        var result = await resolver.ResolveAsync(
            [new NuGetPackageReference("Humanizer", "2.14.1")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        result.AssemblyPaths.Should().NotBeEmpty();
        result.AssemblyPaths.Should().OnlyContain(p => File.Exists(p));
        result.AssemblyPaths.Should().Contain(p => p.EndsWith("Humanizer.dll", StringComparison.OrdinalIgnoreCase));
        result.ResolvedVersions.Should().ContainKey("Humanizer");
    }

    [Fact(Timeout = 180_000)]
    public async Task Resolve_MathNetNumerics_LoadsTransitiveDeps()
    {
        if (ShouldSkip) return;
        var resolver = new NuGetAssemblyResolver(NullLogger<NuGetAssemblyResolver>.Instance);

        var result = await resolver.ResolveAsync(
            [new NuGetPackageReference("MathNet.Numerics", "5.0.0")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        result.AssemblyPaths.Should().Contain(p =>
            p.EndsWith("MathNet.Numerics.dll", StringComparison.OrdinalIgnoreCase));
        result.ProbingDirectories.Should().NotBeEmpty();
    }

    [Fact(Timeout = 30_000)]
    public async Task Resolve_UnknownPackage_Throws()
    {
        if (ShouldSkip) return;
        var resolver = new NuGetAssemblyResolver(NullLogger<NuGetAssemblyResolver>.Instance);

        var act = () => resolver.ResolveAsync(
            [new NuGetPackageReference("This.Package.Does.Not.Exist.Really", "1.0.0")],
            targetFramework: null);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact(Timeout = 180_000)]
    public async Task Resolve_TwiceWithSameInputs_HitsCache()
    {
        if (ShouldSkip) return;
        var resolver = new NuGetAssemblyResolver(NullLogger<NuGetAssemblyResolver>.Instance);
        var refs = new[] { new NuGetPackageReference("Humanizer", "2.14.1") };

        var first = await resolver.ResolveAsync(refs, targetFramework: null, TestContext.Current.CancellationToken);
        var second = await resolver.ResolveAsync(refs, targetFramework: null, TestContext.Current.CancellationToken);

        second.Should().BeSameAs(first);
    }
}
