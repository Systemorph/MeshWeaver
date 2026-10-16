using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.NuGet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 <b>A build that declares <c>#r "nuget:…"</c> LOADS wherever its bytes are read — not only in
/// the process that compiled it.</b>
///
/// <para>Measured 2026-10-16 on an enterprise client portal (platform 3.1.10992, which carries
/// #6834's compile fix): a NodeType declaring <c>#r "nuget: Microsoft.Data.SqlClient, 6.1.1"</c>
/// compiled Ok, and then on BOTH replicas, dozens of times, every load of the cached build failed —
/// <c>Failed to extract NodeTypeConfigurations from /data/assembly-cache/…/v33-….dll: Could not load
/// file or assembly 'Microsoft.Data.SqlClient, Version=6.0.0.0'</c> — at the start-up registration
/// pass, at <c>EnrichWithNodeType</c> and after a recompile attempt. The package's probing
/// directories were registered in memory by the emit alone, so a replica that never compiled the
/// type, or the compiling one after a restart, loaded the build with no way to find its packages.</para>
///
/// <para>Each case below produces the build in one place and loads it through a FRESH
/// <see cref="CompilationCacheService"/> + <see cref="MeshNodeCompilationService"/> pair — an empty
/// probing registry and an empty package-resolution memo, which is exactly what another replica or
/// a restarted process has — and then runs a type that needs the package: its base type is the
/// package's (so the configuration scan's <c>GetTypes()</c> needs the assembly, as the production
/// scan did) and its method constructs a <c>SqlConnection</c> (which contacts no server).</para>
///
/// <para>Needs the live NuGet feed (or a warm global-packages folder) and SKIPS with the reason when
/// it is unreachable, exactly as <see cref="NuGetPackageHostWinsTest"/> does (#679).</para>
/// </summary>
public class NuGetPackagesLoadOnAFreshReplicaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/SqlReplicaProbe";

    private const string Source = """
        #r "nuget: Microsoft.Data.SqlClient, 6.1.1"
        using System;
        using System.Threading.Tasks;
        using Microsoft.Data.SqlClient;

        /// <summary>The content type the probe's NodeType declares.</summary>
        public record SqlReplicaProbeContent
        {
            /// <summary>A label.</summary>
            public string? Label { get; init; }
        }

        /// <summary>A type whose BASE is the package's: realising it needs the package assembly.</summary>
        public sealed class SqlReplicaProbeAuthentication : SqlAuthenticationProvider
        {
            /// <summary>Never called.</summary>
            public override Task<SqlAuthenticationToken> AcquireTokenAsync(SqlAuthenticationParameters parameters)
                => throw new NotSupportedException();

            /// <summary>Supports nothing.</summary>
            public override bool IsSupported(SqlAuthenticationMethod authenticationMethod) => false;
        }

        /// <summary>Constructs a connection; contacts no server.</summary>
        public static class SqlReplicaProbe
        {
            /// <summary>Runs the probe.</summary>
            public static string Run(string connectionString)
            {
                using var connection = new SqlConnection(connectionString);
                return connection.DataSource + "|" + connection.State + "|"
                    + new SqlReplicaProbeAuthentication().IsSupported(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity);
            }
        }
        """;

    private const string ConnectionString =
        "Server=tcp:sql.invalid,1433;Database=Sample;"
        + "Authentication=Active Directory Managed Identity;Encrypt=True";

    private const string Expected = "tcp:sql.invalid,1433|Closed|False";

    private const string TypeConfiguration = "config => config.WithContentType<SqlReplicaProbeContent>()";

    /// <summary>The runtime compile's build, read by a replica that never compiled it.</summary>
    [Fact(Timeout = 300_000)]
    public async Task ARuntimeBuild_LoadsOnAReplicaThatNeverCompiledIt()
    {
        await NuGetPackageHostWinsTest.SkipUnlessNuGetOrgReachable(TestContext.Current.CancellationToken);
        var compiler = Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();

        var compiled = await compiler.CompileAndGetConfigurations(TypeNode(), [SourceNode()])
            .Take(1)
            .Should().Within(TimeSpan.FromMinutes(4))
            .Emit("the runtime compile must settle", cancellationToken: TestContext.Current.CancellationToken);
        compiled.Should().NotBeNull();
        compiled!.AssemblyLocation.Should().NotBeNullOrEmpty(
            "the probe compiles; " + string.Join("; ", compiled.Log?.Messages.Select(m => m.Message) ?? []));
        File.Exists(compiled.AssemblyLocation).Should().BeTrue("the disk cache is on in this mesh");

        await AssertLoadsOnAFreshReplica(compiled.AssemblyLocation!);
    }

    /// <summary>The bake's build (a CI bundle a portal adopts), read by a portal process that never
    /// resolved the package.</summary>
    [Fact(Timeout = 300_000)]
    public async Task ABakedBuild_LoadsOnAReplicaThatNeverCompiledIt()
    {
        await NuGetPackageHostWinsTest.SkipUnlessNuGetOrgReachable(TestContext.Current.CancellationToken);
        var resolver = new NuGetAssemblyResolver(NullLogger<NuGetAssemblyResolver>.Instance);
        var resolved = await resolver.ResolveAsync(
            [new NuGetPackageReference("Microsoft.Data.SqlClient", "6.1.1")],
            targetFramework: null, TestContext.Current.CancellationToken);
        var bakeOutput = Path.Combine(Path.GetTempPath(), "mw-replica-bake-" + Guid.NewGuid().ToString("N"));
        try
        {
            var compiled = NodeSetCompiler.Compile(
                NodeSet.Create([TypeNode(), SourceNode()]),
                TypeNode(),
                sources: null,
                tests: null,
                configuration: TypeConfiguration,
                contentCollections: null,
                references: CompileReferences.Default,
                dependencyIdOf: _ => null,
                toolchainId: NodeTypeCompilationHelpers.ProcessToolchainId,
                outputDirectory: bakeOutput,
                resolveNuGet: (_, _) => resolved.AssemblyPaths,
                ct: TestContext.Current.CancellationToken);

            await AssertLoadsOnAFreshReplica(compiled.DllPath);
        }
        finally
        {
            try { Directory.Delete(bakeOutput, recursive: true); }
            catch (IOException) { /* the loaded image may still be mapped; the temp dir is disposable */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The build's bytes at a path this process has never loaded (the assembly store's copy, as
    /// another replica reads it), through a compilation service with nothing in memory.
    /// </summary>
    private async Task AssertLoadsOnAFreshReplica(string builtDll)
    {
        var replicaRoot = Path.Combine(Path.GetTempPath(), "mw-replica-" + Guid.NewGuid().ToString("N"));
        var storeDir = Directory.CreateDirectory(Path.Combine(replicaRoot, "assembly-cache", "type_SqlReplicaProbe"));
        var storeCopy = Path.Combine(storeDir.FullName, "v33-c003e001-replica.dll");
        File.Copy(builtDll, storeCopy);

        var cacheOptions = Options.Create(new CompilationCacheOptions
        {
            CacheDirectory = Path.Combine(replicaRoot, "mesh-cache"),
        });
        var freshCache = new CompilationCacheService(cacheOptions, NullLogger<CompilationCacheService>.Instance);
        try
        {
            var freshReplica = new MeshNodeCompilationService(
                freshCache, cacheOptions, Mesh,
                new NuGetAssemblyResolver(NullLogger<NuGetAssemblyResolver>.Instance),
                NullLogger<MeshNodeCompilationService>.Instance);

            var loaded = await freshReplica.GetConfigurationsFromExistingAssembly(storeCopy, TypePath)
                .Take(1)
                .Should().Within(TimeSpan.FromMinutes(4))
                .Emit("loading a cached build must settle", cancellationToken: TestContext.Current.CancellationToken);

            loaded.Should().NotBeNull();
            loaded!.AssemblyLocation.Should().Be(storeCopy,
                "a replica that never compiled the type must load its build — the loader said: "
                + string.Join("; ", loaded.Log?.Messages.Select(m => m.Message) ?? []));
            loaded.NodeTypeConfigurations.Should().NotBeEmpty("the configuration scan realised every type");

            var assembly = AssemblyLoadContext.All
                .SelectMany(c => c.Assemblies)
                .Single(a => !a.IsDynamic && string.Equals(a.Location, storeCopy, StringComparison.Ordinal));
            var run = assembly.GetType("SqlReplicaProbe", throwOnError: true)!.GetMethod("Run")!;
            ((string)run.Invoke(null, [ConnectionString])!).Should().Be(Expected);

            var context = AssemblyLoadContext.GetLoadContext(assembly)!;
            var sqlClient = context.Assemblies.Should().ContainSingle(
                a => a.GetName().Name == "Microsoft.Data.SqlClient",
                "the package's assembly is bound in the build's own context").Which;
            sqlClient.Location.Replace('\\', '/').Should().Contain("/runtimes/",
                "the package's RID runtime asset, never its lib/ compile stub");
        }
        finally
        {
            freshCache.Dispose();
            try { Directory.Delete(replicaRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static MeshNode TypeNode() => MeshNode.FromPath(TypePath) with
    {
        Name = "SqlReplicaProbe",
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        LastModified = DateTimeOffset.UtcNow,
        Content = new NodeTypeDefinition
        {
            Description = "a #r nuget NodeType whose build is loaded where it was not compiled",
            Configuration = TypeConfiguration,
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
}

/// <summary>
/// The record itself (<see cref="NuGetBuildRecord"/>), with no feed: a NodeType that declares no
/// package keeps a byte-identical generated source — so its content key, and every build already
/// published for it, is untouched — and one that declares packages records each in the skeleton's
/// assembly-attribute region, readable back from the emitted image's metadata.
/// </summary>
public class NuGetBuildRecordTest
{
    private const string Skeleton = """
        // Auto-generated from MeshNode: type/Probe
        using System;

        [assembly: MeshWeaver.Graph.Generated.type_ProbeMeshNodeProvider]

        public static class Probe { }
        """;

    /// <summary>No package, no change.</summary>
    [Fact]
    public void ASourceWithoutPackages_IsUnchanged()
        => NuGetBuildRecord.Stamp(Skeleton, []).Should().BeSameAs(Skeleton);

    /// <summary>Each reference is recorded ahead of the skeleton's attribute and round-trips through
    /// an emitted image.</summary>
    [Fact]
    public void EachReference_IsRecordedInTheImage()
    {
        NuGetPackageReference[] references =
        [
            new("Microsoft.Data.SqlClient", "6.1.1"),
            new("Humanizer.Core", null),
            new("Some.Package", "[1.0.0, 2.0.0)"),
        ];
        var stamped = NuGetBuildRecord.Stamp(Skeleton, references);
        stamped.IndexOf("AssemblyMetadata", StringComparison.Ordinal)
            .Should().BeLessThan(stamped.IndexOf("[assembly: MeshWeaver.Graph.Generated.", StringComparison.Ordinal));

        // The skeleton's own attribute type does not exist here; the record must not need it.
        var source = stamped.Replace("[assembly: MeshWeaver.Graph.Generated.type_ProbeMeshNodeProvider]", "");
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "RecordProbe_" + Guid.NewGuid().ToString("N"),
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)],
            CompileReferences.Default,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        var path = Path.Combine(Path.GetTempPath(), "mw-record-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            Microsoft.CodeAnalysis.Emit.EmitResult emitted;
            using (var file = File.Create(path))
                emitted = compilation.Emit(file);
            emitted.Success.Should().BeTrue(string.Join("; ", emitted.Diagnostics));
            NuGetBuildRecord.Read(path).Should().Equal(references);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A build that recorded nothing, and a path that holds nothing, read as empty.</summary>
    [Fact]
    public void NothingRecorded_ReadsEmpty()
    {
        NuGetBuildRecord.Read(typeof(NuGetBuildRecordTest).Assembly.Location).Should().BeEmpty();
        NuGetBuildRecord.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll")).Should().BeEmpty();
    }
}

/// <summary>
/// The build's recorded closure takes precedence over the node-wide registration (whatever this
/// process last compiled, possibly another generation) — also when the context was created before
/// the build's resolution answered.
/// </summary>
public class BuildProbingPrecedenceTest
{
    /// <summary>A context created on the node-wide fallback is given its build's closure when it lands.</summary>
    [Fact]
    public void TheBuildsRecordedClosure_ReplacesTheNodeWideFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-probe-precedence-" + Guid.NewGuid().ToString("N"));
        var cache = new CompilationCacheService(
            Options.Create(new CompilationCacheOptions { CacheDirectory = root }),
            NullLogger<CompilationCacheService>.Instance);
        try
        {
            var dll = Path.Combine(root, "store", "v1-build.dll");
            cache.RegisterProbingDirectories("type_Probe", [Path.Combine(root, "other-generation")]);
            var context = cache.GetOrCreateLoadContextForPath("type_Probe", dll);
            context.HasProbingDirectories.Should().BeTrue("the node-wide fallback applies until the build's own closure is known");
            context.HasBuildProbingDirectories.Should().BeFalse();

            cache.RegisterBuildProbingDirectories(dll, [Path.Combine(root, "this-build")]);
            context.HasBuildProbingDirectories.Should().BeTrue(
                "the build's recorded closure must replace the fallback, which may name another generation");

            // …and the node-wide registration never displaces it again.
            cache.RegisterProbingDirectories("type_Probe", [Path.Combine(root, "yet-another")]);
            cache.GetOrCreateLoadContextForPath("type_Probe", dll).HasBuildProbingDirectories.Should().BeTrue();
        }
        finally
        {
            cache.Dispose();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
