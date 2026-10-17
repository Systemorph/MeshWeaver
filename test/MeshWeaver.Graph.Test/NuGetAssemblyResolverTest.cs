using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.NuGet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    // Resolve_UnknownPackage_Throws and Resolve_TwiceWithSameInputs_HitsCache moved to
    // NuGetOfflineFirstResolutionTest as hermetic cases (a local folder feed): neither asks anything
    // only the live feed can answer, and the unknown-package case timed out whenever api.nuget.org
    // was slow to answer one 404. Only cases that need REAL packages from the live feed stay here.

    /// <summary>
    /// The motivating case (client-a.meshweaver.cloud, linux-x64, 2026-10-11): a NodeType referencing
    /// Microsoft.Data.SqlClient compiled and then failed at render, because the resolver surfaced only
    /// the package's <c>lib/</c> assembly — the "not supported on this platform" stub. This compiles a
    /// snippet the way a NodeType is compiled (against the COMPILE assets), loads it into a context
    /// that binds packages the way a NodeType's context does, and constructs a
    /// <c>SqlConnection</c> with a managed-identity connection string — which runs the real client's
    /// connection-string parser and authentication-provider registration (MSAL / Azure.Identity). No
    /// server is contacted. It also pins host unification: nothing the host already ships at an
    /// equal-or-newer version is loaded a second time.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task Resolve_SqlClient_LoadsTheRuntimeAssetAndBuildsManagedIdentityAndDefaultConnections()
    {
        await SkipUnlessNuGetOrgReachable(TestContext.Current.CancellationToken);
        var resolver = new NuGetAssemblyResolver(NullLogger<NuGetAssemblyResolver>.Instance);

        var result = await resolver.ResolveAsync(
            [new NuGetPackageReference("Microsoft.Data.SqlClient", "6.1.1")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        var rid = OperatingSystem.IsWindows() ? "win" : "unix";
        var runtimeSqlClient = result.RuntimeAssemblyPaths
            .Single(p => Path.GetFileName(p) == "Microsoft.Data.SqlClient.dll");
        runtimeSqlClient.Replace('\\', '/').Should().Contain($"/runtimes/{rid}/lib/");
        result.AssemblyPaths.Should().Contain(p => p.EndsWith("Microsoft.Data.SqlClient.dll"));
        result.ProbingDirectories.Should().Contain(Path.GetDirectoryName(runtimeSqlClient)!);
        result.ProbingDirectories.Should().NotContain(d =>
            d.Replace('\\', '/').Contains("/microsoft.data.sqlclient/") && !d.Replace('\\', '/').Contains("/runtimes/"),
            "the RID group REPLACES the stub's lib/ directory — probing it would load the stub");

        // Both authentication modes the portal uses: Managed Identity, and Active Directory Default
        // (Azure.Identity's DefaultAzureCredential — AKS workload identity) with the identity's
        // client id as User Id, the shape of the ESL Office connection string.
        const string source = """
            public static class SqlProbe
            {
                public static string Run(string connectionString)
                {
                    using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
                    var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection.ConnectionString);
                    return connection.DataSource + "|" + builder.Authentication + "|" + connection.State;
                }
            }
            """;
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var compileNames = result.AssemblyPaths.Select(Path.GetFileName).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var references = trusted
            .Where(p => !compileNames.Contains(Path.GetFileName(p)))
            .Concat(result.AssemblyPaths)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("SqlProbe" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emit = compilation.Emit(image);
        emit.Success.Should().BeTrue(string.Join(Environment.NewLine, emit.Diagnostics));

        var context = new NuGetRuntimeAssetTest.ProbingContext([.. result.ProbingDirectories]);
        try
        {
            image.Position = 0;
            var probe = context.LoadFromStream(image).GetType("SqlProbe")!;
            var run = probe.GetMethod("Run")!;
            ((string)run.Invoke(null, [
                    "Server=tcp:sql.invalid,1433;Database=ESLOffice;"
                    + "Authentication=Active Directory Managed Identity;Encrypt=True"])!)
                .Should().Be("tcp:sql.invalid,1433|ActiveDirectoryManagedIdentity|Closed");
            ((string)run.Invoke(null, [
                    "Server=tcp:sql.invalid,1433;Database=ESLOffice;Authentication=Active Directory Default;"
                    + "User Id=00000000-0000-0000-0000-000000000000;Encrypt=True;"])!)
                .Should().Be("tcp:sql.invalid,1433|ActiveDirectoryDefault|Closed");
            var loadedSqlClient = context.Assemblies.Single(a => a.GetName().Name == "Microsoft.Data.SqlClient");
            loadedSqlClient.Location.Should().Be(runtimeSqlClient);
            foreach (var loaded in context.Assemblies.Where(a => !a.IsDynamic && a.Location.Length > 0))
            {
                var host = PackageAssemblyProbe.HostVersionOf(loaded.GetName().Name!);
                if (host is not null)
                    (host < loaded.GetName().Version!).Should().BeTrue(
                        $"{loaded.GetName().Name} is shipped by the host at {host} and must bind there, not load beside it");
            }
        }
        finally
        {
            context.Unload();
        }
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
