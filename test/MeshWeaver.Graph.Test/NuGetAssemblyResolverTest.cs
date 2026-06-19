using System;
using System.IO;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
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

    /// <summary>
    /// Pins the <c>#r "nuget:MeshWeaver.BusinessRules.Generator"</c> MECHANISM that every
    /// scope-bearing node Source (PensionFund BalanceSheet, and any <c>IScope&lt;,&gt;</c> node)
    /// depends on. The scope SOURCE GENERATOR is deliberately NOT a framework reference — that
    /// propagated the analyzer and bloated every build (commit ef2e756d6,
    /// project_graph_generator_build_bloat). Instead it travels WITH the node Source via a
    /// <c>#r</c> directive, is resolved from the <b>mesh-local feed</b> (<c>dist/packages</c>,
    /// baked into the container image — it is NOT published to nuget.org), surfaced from
    /// <c>lib/</c>, and discovered as a Roslyn <c>[Generator]</c>. If ANY link breaks — the local
    /// feed not on the source list, the generator not shipped under <c>lib/</c>, or the loader
    /// failing to see it — scope nodes compile but emit no <c>IScope</c> implementations and their
    /// views render empty. That exact regression silently broke the PensionFund balance sheet;
    /// this test fails loudly instead.
    ///
    /// <para>The reference is VERSION-LESS, exactly as the node Source declares it: the resolver
    /// returns the highest version the feed carries, so there is ONE global version
    /// (<c>PlatformVersion</c>) and no per-sample pin to drift.</para>
    ///
    /// <para>Skipped only on an unprepared clone where <c>dist/packages</c> has not been packed.
    /// CI's "Pack mesh-local #r packages" step produces it before tests; locally run
    /// <c>dotnet pack src/MeshWeaver.BusinessRules.Generator -c Release -o dist/packages</c>.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ScopeGenerator_ResolvesFromMeshLocalFeed_AndIsDiscoverableAsAGenerator()
    {
        if (ShouldSkip) return;

        // dist/packages lives at the repo root (5 levels up from bin/Debug/net10.0) and is
        // git-ignored — populated by the CI pack step / a local `dotnet pack`. When absent,
        // exit cleanly so a missing artefact surfaces as SKIP, not as a misleading resolver error.
        var distPackages = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dist", "packages"));
        if (!Directory.Exists(distPackages)
            || Directory.GetFiles(distPackages, "MeshWeaver.BusinessRules.Generator.*.nupkg").Length == 0)
            return;

        var resolver = new NuGetAssemblyResolver(NullLogger<NuGetAssemblyResolver>.Instance);

        // Version-LESS resolve. If the mesh-local feed were not among the configured sources this
        // throws "no package found" — so a green result IS the "local path is in the places we
        // look for packages" assertion.
        var result = await resolver.ResolveAsync(
            [new NuGetPackageReference("MeshWeaver.BusinessRules.Generator", null)],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        // The generator csproj ships its DLL under BOTH analyzers/ AND lib/netstandard2.0 precisely
        // so this lib-only resolver surfaces it; assert that contract holds.
        result.AssemblyPaths.Should().Contain(
            p => p.EndsWith("MeshWeaver.BusinessRules.Generator.dll", StringComparison.OrdinalIgnoreCase),
            "the generator package must surface its assembly from lib/ so SourceGeneratorLoader can load it");
        result.AssemblyPaths.Should().OnlyContain(p => File.Exists(p));

        // …and the resolved assembly really is a usable Roslyn source generator (ScopeCodeGenerator),
        // so MeshNodeCompilationService.RunSourceGenerators will emit the IScope<,> implementations.
        var generators = SourceGeneratorLoader.Discover(result.AssemblyPaths, NullLogger.Instance);
        generators.Should().NotBeEmpty(
            "the resolved generator assembly must expose a [Generator] — otherwise scope nodes compile but generate nothing");
    }
}
