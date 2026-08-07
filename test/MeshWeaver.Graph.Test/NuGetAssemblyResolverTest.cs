using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.NuGet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// End-to-end test of NuGet restore against api.nuget.org. Requires network access:
/// each test probes the live feed first and SKIPS with an explicit reason when it is
/// unreachable (#679) — a feed outage must present as a skip, never as a red that
/// points at NuGet internals. Disable outright with MESHWEAVER_SKIP_NUGET=1.
/// </summary>
[Collection("NuGetNetwork")]
public class NuGetAssemblyResolverTest
{
    /// <summary>
    /// Loud gate for the live-feed dependency (#679): when nuget.org has a bad minute
    /// these tests used to fail with a FatalProtocolException naming NuGet internals —
    /// a phantom flake someone then triages. Skipping with the cause on the result is
    /// honest; a silent early-return would be a vacuous pass. Probed per test on
    /// purpose: no cached static state, and the serialized collection makes the cost
    /// one round-trip per test against 180 s budgets.
    /// </summary>
    private static async Task SkipUnlessNuGetOrgReachable(CancellationToken ct)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MESHWEAVER_SKIP_NUGET") == "1",
            "NuGet end-to-end tests disabled via MESHWEAVER_SKIP_NUGET=1");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await http.GetAsync("https://api.nuget.org/v3/index.json", ct);
            Assert.SkipUnless(response.IsSuccessStatusCode,
                $"api.nuget.org service index answered {(int)response.StatusCode} — feed unhealthy; " +
                "this end-to-end test needs the live feed (#679)");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Assert.Skip($"api.nuget.org unreachable ({ex.GetType().Name}: {ex.Message}) — this " +
                "end-to-end test needs the live feed; a network outage presents as a SKIP, not a " +
                "phantom test failure (#679)");
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task Resolve_Humanizer_ReturnsExistingDllPaths()
    {
        await SkipUnlessNuGetOrgReachable(TestContext.Current.CancellationToken);
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
        await SkipUnlessNuGetOrgReachable(TestContext.Current.CancellationToken);
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
        await SkipUnlessNuGetOrgReachable(TestContext.Current.CancellationToken);
        var resolver = new NuGetAssemblyResolver(NullLogger<NuGetAssemblyResolver>.Instance);

        var act = () => resolver.ResolveAsync(
            [new NuGetPackageReference("This.Package.Does.Not.Exist.Really", "1.0.0")],
            targetFramework: null);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact(Timeout = 180_000)]
    public async Task Resolve_TwiceWithSameInputs_HitsCache()
    {
        await SkipUnlessNuGetOrgReachable(TestContext.Current.CancellationToken);
        var resolver = new NuGetAssemblyResolver(NullLogger<NuGetAssemblyResolver>.Instance);
        var refs = new[] { new NuGetPackageReference("Humanizer", "2.14.1") };

        var first = await resolver.ResolveAsync(refs, targetFramework: null, TestContext.Current.CancellationToken);
        var second = await resolver.ResolveAsync(refs, targetFramework: null, TestContext.Current.CancellationToken);

        second.Should().BeSameAs(first);
    }

    // The former ScopeGenerator_ResolvesFromMeshLocalFeed test was DELETED with the CI
    // "Pack mesh-local #r packages" step: the feed-resolved generator mechanism it pinned is
    // dead — the scope generator ships WITH the platform and runs in-process (no #r, no feed,
    // no NuGet round-trip), pinned by BuiltInScopeGeneratorTest; a legacy
    // #r "nuget:MeshWeaver.BusinessRules.Generator" is filtered out of the resolve set at
    // compile (also pinned there). The test only ever ran when dist/packages existed (CI's
    // pack step / a manual local pack) and self-skipped otherwise — after the pack-step
    // removal it would have been a permanently-skipped pin of a removed mechanism.
}
