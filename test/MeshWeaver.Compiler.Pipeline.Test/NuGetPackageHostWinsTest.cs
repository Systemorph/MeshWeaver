using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.NuGet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 <b>A <c>#r "nuget:…"</c> closure never adds a second copy of an assembly the host already
/// references — the host wins.</b>
///
/// <para>Measured 2026-10-15 on an enterprise client portal (platform 3.1.10932): a client
/// NodeType declares <c>#r "nuget: Microsoft.Data.SqlClient, 6.1.1"</c>, whose transitive closure
/// carries System.Text.Json 9, Microsoft.Extensions.DependencyInjection.Abstractions 9,
/// System.Data.Common and more. Every compile path appended the WHOLE closure to the host's
/// reference set, so Roslyn saw two <c>JsonElement</c>s (CS0433), two
/// <c>GetRequiredService&lt;T&gt;</c>s (CS0121), an ambiguous <c>MaxBy</c> and a cascade of
/// inference errors behind them — and the type never compiled.</para>
///
/// <para>The source below is that NodeType's shape cut to the four things it broke on, and it runs
/// through BOTH producers of a NodeType's bytes: the bake (<see cref="NodeSetCompiler.Compile"/>)
/// and the portal's runtime compile (<see cref="IMeshNodeCompilationService"/>). Then it RUNS: the
/// dropped compile references must bind to the host's copies at load, and the genuinely new
/// assembly (Microsoft.Data.SqlClient) to the package's runtime asset — a <c>SqlConnection</c> is
/// constructed over a managed-identity connection string, which contacts no server.</para>
///
/// <para>Needs the live NuGet feed (or a warm global-packages folder): each case probes
/// api.nuget.org first and SKIPS with the reason when it is unreachable, exactly as
/// <c>NuGetAssemblyResolverTest</c> does (#679). Disable outright with MESHWEAVER_SKIP_NUGET=1.</para>
/// </summary>
public class NuGetPackageHostWinsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/SqlShapedSqlProbe";

    /// <summary>The client NodeType's shape: the directive, then JsonElement / JsonObject,
    /// GetRequiredService, MaxBy and a SqlConnection in one source.</summary>
    internal const string Source = """
        #r "nuget: Microsoft.Data.SqlClient, 6.1.1"
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Text.Json;
        using System.Text.Json.Nodes;
        using Microsoft.Data.SqlClient;
        using Microsoft.Extensions.DependencyInjection;

        /// <summary>The content type the probe's NodeType declares.</summary>
        public record SqlShapedSqlProbeContent
        {
            /// <summary>A label.</summary>
            public string? Label { get; init; }
        }

        /// <summary>A service resolved through GetRequiredService.</summary>
        public sealed record SqlShapedMarker(string Name);

        /// <summary>Exercises every name the closure used to duplicate.</summary>
        public static class SqlShapedSqlProbe
        {
            /// <summary>Runs the probe; contacts no server.</summary>
            public static string Run(string json, string connectionString)
            {
                using var document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                var largest = root.EnumerateArray().Select(e => e.GetInt32()).ToList().MaxBy(v => v);
                var node = new JsonObject { ["largest"] = largest };

                IServiceProvider services = new ServiceCollection()
                    .AddSingleton(new SqlShapedMarker("di"))
                    .BuildServiceProvider();
                var marker = services.GetRequiredService<SqlShapedMarker>();
                var optional = services.GetService<SqlShapedSqlProbeContent>();

                using var connection = new SqlConnection(connectionString);
                var builder = new SqlConnectionStringBuilder(connection.ConnectionString);
                return string.Join("|",
                    node["largest"]!.GetValue<int>(), marker.Name, optional is null,
                    connection.DataSource, builder.Authentication, connection.State);
            }
        }
        """;

    private const string ConnectionString =
        "Server=tcp:sql.invalid,1433;Database=Sample;"
        + "Authentication=Active Directory Managed Identity;Encrypt=True";

    private const string Expected = "7|di|True|tcp:sql.invalid,1433|ActiveDirectoryManagedIdentity|Closed";

    /// <summary>The bake: <see cref="NodeSetCompiler.Compile"/> with the bake's own resolver bridge.</summary>
    [Fact(Timeout = 300_000)]
    public async Task TheBake_CompilesAgainstTheHostCopies_AndRuns()
    {
        await SkipUnlessNuGetOrgReachable(TestContext.Current.CancellationToken);
        var resolver = new NuGetAssemblyResolver(NullLogger<NuGetAssemblyResolver>.Instance);
        // Resolved up front, so the synchronous bake's callback hands back a finished answer
        // rather than blocking on the feed.
        var resolved = await resolver.ResolveAsync(
            [new NuGetPackageReference("Microsoft.Data.SqlClient", "6.1.1")],
            targetFramework: null, TestContext.Current.CancellationToken);
        var output = Path.Combine(Path.GetTempPath(), "mw-hostwins-" + Guid.NewGuid().ToString("N"));

        try
        {
            var compiled = NodeSetCompiler.Compile(
                NodeSet.Create([TypeNode(), SourceNode()]),
                TypeNode(),
                sources: null,
                tests: null,
                configuration: null,
                contentCollections: null,
                references: CompileReferences.Default,
                dependencyIdOf: _ => null,
                toolchainId: NodeTypeCompilationHelpers.ProcessToolchainId,
                outputDirectory: output,
                resolveNuGet: (refs, _) =>
                {
                    refs.Should().ContainSingle(r => r.Id == "Microsoft.Data.SqlClient" && r.VersionRange == "6.1.1");
                    return resolved.AssemblyPaths;
                },
                ct: TestContext.Current.CancellationToken);

            var context = new PackageProbingContext([.. resolved.ProbingDirectories]);
            try
            {
                var assembly = context.LoadFromAssemblyPath(compiled.DllPath);
                AssertRunsAndBindsToTheHost(assembly, context);
            }
            finally
            {
                context.Unload();
            }
        }
        catch (CompilationException e)
        {
            Assert.Fail("the bake must compile the client NodeType's shape — every name the "
                + "SqlClient closure duplicates is the host's: " + e.Message);
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); }
            catch (IOException) { /* the loaded image may still be mapped; the temp dir is disposable */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The portal's runtime compile, through the mesh's real compilation service and the
    /// NodeType load context it binds the assembly into.</summary>
    [Fact(Timeout = 300_000)]
    public async Task TheRuntimeCompile_CompilesAgainstTheHostCopies_AndRuns()
    {
        await SkipUnlessNuGetOrgReachable(TestContext.Current.CancellationToken);
        var compiler = Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();

        var result = await compiler.CompileAndGetConfigurations(TypeNode(), [SourceNode()])
            .Take(1)
            .Should().Within(TimeSpan.FromMinutes(4))
            .Emit("the runtime compile must settle", cancellationToken: TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        var errors = (result!.Diagnostics ?? []).Where(d => d.Severity.ToString() == "Error").Select(d => d.ToString()).ToList();
        result.AssemblyLocation.Should().NotBeNullOrEmpty(
            "the client NodeType's shape must compile at runtime; errors: " + string.Join(Environment.NewLine, errors));

        result.NodeTypeConfigurations.Should().NotBeEmpty("the compiled assembly was loaded and its configuration read");
        // The NodeType load context the compile service bound the assembly into.
        var assembly = AssemblyLoadContext.All
            .SelectMany(c => c.Assemblies)
            .Single(a => a.GetName().Name!.StartsWith("DynamicNode_", StringComparison.Ordinal)
                         && a.GetType("SqlShapedSqlProbe") is not null);
        AssertRunsAndBindsToTheHost(assembly, AssemblyLoadContext.GetLoadContext(assembly)!);
    }

    /// <summary>
    /// The MaxBy half of the measurement needs no package at all: <c>Enumerable.MaxBy</c> against
    /// the host set alone. System.Interactive's IMPLEMENTATION assembly (the one the host ships)
    /// keeps a binary-compatibility <c>EnumerableEx.MaxBy</c> in namespace <c>System.Linq</c> that
    /// its REFERENCE assembly hides from every net6+ compile — so in-mesh C# saw an ambiguity no
    /// <c>src/</c> project can.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void EnumerableMaxBy_CompilesAgainstTheHostSet()
    {
        var output = Path.Combine(Path.GetTempPath(), "mw-maxby-" + Guid.NewGuid().ToString("N"));
        var source = new MeshNode("Probe", $"{TypePath}/Source")
        {
            NodeType = "Code",
            Name = "Probe",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = """
                    using System.Linq;

                    /// <summary>Uses the BCL's MaxBy.</summary>
                    public static class MaxByProbe
                    {
                        /// <summary>The largest of three.</summary>
                        public static int Run() => new[] { 3, 7, 5 }.ToList().MaxBy(v => v);
                    }
                    """,
            },
        };
        try
        {
            var compiled = NodeSetCompiler.Compile(
                NodeSet.Create([TypeNode(), source]), TypeNode(),
                sources: null, tests: null, configuration: null, contentCollections: null,
                references: CompileReferences.Default,
                dependencyIdOf: _ => null,
                toolchainId: NodeTypeCompilationHelpers.ProcessToolchainId,
                outputDirectory: output,
                ct: TestContext.Current.CancellationToken);
            File.Exists(compiled.DllPath).Should().BeTrue();
        }
        catch (CompilationException e)
        {
            Assert.Fail("Enumerable.MaxBy must bind against the host set: " + e.Message);
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void AssertRunsAndBindsToTheHost(Assembly assembly, AssemblyLoadContext context)
    {
        var run = assembly.GetType("SqlShapedSqlProbe", throwOnError: true)!.GetMethod("Run")!;
        ((string)run.Invoke(null, ["[3, 7, 5]", ConnectionString])!).Should().Be(Expected);

        // The compile referenced only the host's copies, so the emitted image names the host's
        // identities — and nothing the host ships was loaded a second time beside it.
        var hostReferences = assembly.GetReferencedAssemblies()
            .Where(r => r.Name is "System.Text.Json" or "Microsoft.Extensions.DependencyInjection.Abstractions")
            .ToList();
        hostReferences.Should().HaveCount(2);
        foreach (var reference in hostReferences)
            reference.Version.Should().Be(PackageAssemblyProbe.HostVersionOf(reference.Name!),
                $"{reference.Name} is the host's — the package's older copy must not be the compile reference");
        foreach (var loaded in context.Assemblies.Where(a => !a.IsDynamic && a.Location.Length > 0))
        {
            if (loaded == assembly) continue;
            var host = PackageAssemblyProbe.HostVersionOf(loaded.GetName().Name!);
            if (host is not null)
                (host < loaded.GetName().Version!).Should().BeTrue(
                    $"{loaded.GetName().Name} is shipped by the host at {host} and must bind there, not load beside it");
        }
        var sqlClient = context.Assemblies.Should().ContainSingle(a => a.GetName().Name == "Microsoft.Data.SqlClient",
            "the genuinely new assembly comes from the package").Which;
        sqlClient.Location.Replace('\\', '/').Should().Contain("/runtimes/",
            "the package's RID runtime asset, never its lib/ compile stub");
        AssemblyLoadContext.Default.Assemblies.Should().NotContain(a => a.GetName().Name == "Microsoft.Data.SqlClient",
            "compiling must not load a package's compile asset into the default context — the "
            + "NodeType context would then unify with it as if the host shipped it");
    }

    private static MeshNode TypeNode() => MeshNode.FromPath(TypePath) with
    {
        Name = "SqlShapedSqlProbe",
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        LastModified = DateTimeOffset.UtcNow,
        Content = new NodeTypeDefinition
        {
            Description = "the client NodeType's shape: a #r nuget package whose closure overlaps the host",
            Configuration = "config => config.WithContentType<SqlShapedSqlProbeContent>()",
        },
    };

    private static MeshNode SourceNode() => new("SqlDatabase", $"{TypePath}/Source")
    {
        NodeType = "Code",
        Name = "SqlDatabase",
        State = MeshNodeState.Active,
        LastModified = DateTimeOffset.UtcNow,
        Content = new CodeConfiguration { Language = "csharp", Code = Source },
    };

    /// <summary>The live-feed gate of <c>NuGetAssemblyResolverTest</c> (#679): an outage is a SKIP
    /// with its cause, never a red pointing at NuGet internals.</summary>
    internal static async Task SkipUnlessNuGetOrgReachable(CancellationToken ct)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MESHWEAVER_SKIP_NUGET") == "1",
            "NuGet end-to-end tests disabled via MESHWEAVER_SKIP_NUGET=1");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await http.GetAsync("https://api.nuget.org/v3/index.json", ct);
            Assert.SkipUnless(response.IsSuccessStatusCode,
                $"api.nuget.org service index answered {(int)response.StatusCode} — feed unhealthy; "
                + "this end-to-end test needs the live feed (#679)");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Assert.Skip($"api.nuget.org unreachable ({ex.GetType().Name}: {ex.Message}) — this "
                + "end-to-end test needs the live feed (#679)");
        }
    }

    /// <summary>Binds packages the way a NodeType's context does: host unification, then the
    /// package probe, else the default context.</summary>
    private sealed class PackageProbingContext(IReadOnlyCollection<string> probingDirectories)
        : AssemblyLoadContext("mw-hostwins-" + Guid.NewGuid().ToString("N"), isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
            => PackageAssemblyProbe.Resolve(this, assemblyName, probingDirectories);

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
            => PackageAssemblyProbe.ResolveUnmanaged(unmanagedDllName, probingDirectories, load: LoadUnmanagedDllFromPath);
    }
}
