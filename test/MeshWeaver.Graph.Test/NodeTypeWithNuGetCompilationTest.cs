using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.NuGet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Compiles a node type whose _Source references MathNet.Numerics via a #r "nuget:..." directive,
/// and verifies the resulting assembly can execute MathNet code end-to-end.
/// Requires network access to api.nuget.org on first run.
/// </summary>
[Collection("NuGetNetwork")]
public class NodeTypeWithNuGetCompilationTest : IDisposable
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("MESHWEAVER_SKIP_NUGET") == "1";

    private static readonly JsonSerializerOptions SetupJsonOptions = new();
    private readonly string _testCacheDir;
    private readonly IOptions<CompilationCacheOptions> _cacheOptions;
    private readonly ICompilationCacheService _cacheService;
    private readonly IMessageHub _mockHub;
    private readonly INuGetAssemblyResolver _nugetResolver;

    public NodeTypeWithNuGetCompilationTest()
    {
        _testCacheDir = Path.Combine(Path.GetTempPath(), $"nuget-compile-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testCacheDir);

        _cacheOptions = Options.Create(new CompilationCacheOptions
        {
            CacheDirectory = _testCacheDir,
            EnableCompilationCache = true,
            EnableSourceDebugging = true
        });

        _cacheService = new CompilationCacheService(_cacheOptions, NullLogger<CompilationCacheService>.Instance);
        _mockHub = Substitute.For<IMessageHub>();
        _mockHub.JsonSerializerOptions.Returns(SetupJsonOptions);
        _nugetResolver = new NuGetAssemblyResolver(NullLogger<NuGetAssemblyResolver>.Instance);
    }

    public void Dispose()
    {
        if (_cacheService is IDisposable disposable) disposable.Dispose();
        if (Directory.Exists(_testCacheDir))
        {
            try { Directory.Delete(_testCacheDir, recursive: true); } catch { }
        }
    }

    private MeshNodeCompilationService CreateService(InMemoryPersistenceService persistence)
    {
        IServiceCollection services = new ServiceCollection();
        services.AddInMemoryPersistence(persistence);
        services.AddScoped<IMessageHub>(_ => _mockHub);
        services.AddSingleton(new MeshConfiguration(new System.Collections.Generic.Dictionary<string, MeshNode>()));
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        var hubSp = Substitute.For<IServiceProvider>();
        hubSp.GetService(Arg.Any<Type>()).Returns(ci => sp.GetService(ci.Arg<Type>()));
        _mockHub.ServiceProvider.Returns(hubSp);

        var scope = sp.CreateScope();
        var meshQuery = scope.ServiceProvider.GetRequiredService<IMeshService>();
        hubSp.GetService(typeof(IMeshService)).Returns(meshQuery);

        return new MeshNodeCompilationService(
            _cacheService, _cacheOptions, _mockHub, _nugetResolver,
            NullLogger<MeshNodeCompilationService>.Instance);
    }

    private async Task SetupNodeType(InMemoryPersistenceService persistence, string nodeType, CodeConfiguration codeFile)
    {
        var node = MeshNode.FromPath($"type/{nodeType}") with
        {
            Name = nodeType,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition { }
        };
        await persistence.SaveNodeAsync(node, SetupJsonOptions, TestContext.Current.CancellationToken);

        var codeNode = new MeshNode("code", $"type/{nodeType}/_Source")
        {
            NodeType = "Code",
            Name = "Code",
            Content = codeFile
        };
        await persistence.SaveNodeAsync(codeNode, SetupJsonOptions, TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 240_000)]
    public async Task CompileNodeType_WithNuGetDirective_LoadsMathNetAssembly()
    {
        if (ShouldSkip) return;

        const string code = """
            #r "nuget:MathNet.Numerics, 5.0.0"

            using MathNet.Numerics.LinearAlgebra;

            public static class MatrixDemo
            {
                public static double Determinant()
                {
                    var m = Matrix<double>.Build.DenseOfArray(new double[,] { { 1, 2 }, { 3, 4 } });
                    return m.Determinant();
                }
            }
            """;

        var persistence = new InMemoryPersistenceService();
        await SetupNodeType(persistence, "matrix-demo", new CodeConfiguration { Code = code });
        var service = CreateService(persistence);

        var node = MeshNode.FromPath("org/demo/m1") with
        {
            Name = "M1",
            NodeType = "type/matrix-demo",
            LastModified = DateTimeOffset.UtcNow
        };

        var assemblyPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);
        assemblyPath.Should().NotBeNull();
        File.Exists(assemblyPath).Should().BeTrue();

        var nodeName = _cacheService.SanitizeNodeName(node.Path);
        var loadContext = _cacheService.GetOrCreateLoadContext(nodeName);
        var assembly = loadContext.LoadNodeAssembly();
        assembly.Should().NotBeNull();

        var type = assembly!.GetType("MatrixDemo");
        type.Should().NotBeNull("MatrixDemo type should be compiled into the assembly");

        var det = type!.GetMethod("Determinant", BindingFlags.Public | BindingFlags.Static);
        det.Should().NotBeNull();

        var result = (double)det!.Invoke(null, null)!;
        result.Should().BeApproximately(-2.0, 1e-9,
            "determinant of [[1,2],[3,4]] = -2");
    }

    [Fact(Timeout = 20_000)]
    public async Task CompileNodeType_WithoutNuGetDirective_FailsToFindMathNetType()
    {
        // Prove the directive is load-bearing: if absent, MathNet is NOT available.
        const string code = """
            using MathNet.Numerics.LinearAlgebra;
            public static class MatrixDemo { }
            """;

        var persistence = new InMemoryPersistenceService();
        await SetupNodeType(persistence, "matrix-demo-bad", new CodeConfiguration { Code = code });
        var service = CreateService(persistence);

        var node = MeshNode.FromPath("org/demo/bad") with
        {
            Name = "Bad",
            NodeType = "type/matrix-demo-bad",
            LastModified = DateTimeOffset.UtcNow
        };

        var act = () => service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<CompilationException>();
    }

    /// <summary>
    /// Reproduces the prod failure mode directly on the release-path compile
    /// (CompileToReleaseAsync), which bakes the combined _Source into a
    /// NodeTypeRelease and emits to a dedicated release folder. That path was
    /// initially missing the #r "nuget:..." strip + NuGet resolve step, so
    /// MathNet disappeared even though the on-demand path handled it. Keeping
    /// this test guarantees the release path goes through the same contract:
    ///
    ///   1. `#r "nuget:..."` is stripped before Roslyn parses.
    ///   2. Resolved package assemblies are added as MetadataReferences.
    ///   3. Probing directories are persisted alongside the release so the
    ///      AssemblyLoadContext can locate transitive deps at load time.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task CompileToRelease_WithNuGetDirective_LoadsAndInvokesMathNet()
    {
        if (ShouldSkip) return;

        const string code = """
            #r "nuget:MathNet.Numerics, 5.0.0"

            using MathNet.Numerics.Statistics;

            public static class StatsHelper
            {
                public static double MeanAndMaximum(double[] xs) => xs.Mean() + xs.Maximum();
            }
            """;

        var persistence = new InMemoryPersistenceService();
        var service = CreateService(persistence);

        var release = NodeTypeRelease.Create(
            nodeTypePath: "type/stats-demo",
            code: code,
            hubConfiguration: null,
            contentCollections: null,
            frameworkTimestamp: DateTimeOffset.UtcNow,
            frameworkVersion: "1.0.0");

        var node = MeshNode.FromPath(release.Path) with
        {
            Name = "StatsDemo",
            NodeType = MeshNode.NodeTypePath,
            LastModified = DateTimeOffset.UtcNow
        };

        var releaseFolder = Path.Combine(_testCacheDir, release.GetSanitizedPath());
        var result = await service.CompileToReleaseAsync(release, node, releaseFolder, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        File.Exists(Path.Combine(releaseFolder, $"{release.GetSanitizedPath()}.dll")).Should().BeTrue();
        File.Exists(Path.Combine(releaseFolder, "probing.json")).Should().BeTrue("NuGet probing dirs must be persisted alongside the release");

        // Load the assembly via the release LoadContext and invoke the MathNet call.
        var assembly = _cacheService.LoadAssemblyFromRelease(release, releaseFolder);
        assembly.Should().NotBeNull();

        var statsType = assembly!.GetType("StatsHelper");
        statsType.Should().NotBeNull();

        var method = statsType!.GetMethod("MeanAndMaximum", BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull();

        // For xs = 1..100: mean = 50.5 (exact), max = 100 (exact) — sum 150.5.
        // Both .Mean() and .Maximum() are MathNet extension methods, so a correct
        // result proves MathNet.Numerics.dll was actually loaded and linked in.
        var xs = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        var result2 = (double)method!.Invoke(null, new object[] { xs })!;
        result2.Should().Be(150.5);
    }

    /// <summary>
    /// Reproduces the prod failure mode: two _Source files in the same NodeType,
    /// each starting with `#r "nuget:MathNet.Numerics, 5.0.0"` and each using
    /// MathNet types. After `string.Join("\n\n", ...)` the second file's `#r`
    /// sits on a line that still starts at column 0, so Extract must strip
    /// both directives for Roslyn to compile in Regular mode.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task CompileNodeType_WithMultipleSourcesEachUsingNuGet_LoadsAssembly()
    {
        if (ShouldSkip) return;

        const string distributionsSource = """
            #r "nuget:MathNet.Numerics, 5.0.0"

            using MathNet.Numerics.Distributions;

            public static class DistributionsHelper
            {
                public static double SamplePoisson(double lambda, int seed)
                {
                    var rng = new System.Random(seed);
                    return new Poisson(lambda, rng).Sample();
                }
            }
            """;

        const string statsSource = """
            #r "nuget:MathNet.Numerics, 5.0.0"

            using MathNet.Numerics.Statistics;

            public static class StatsHelper
            {
                public static double MeanOf(double[] xs) => xs.Mean();
                public static double Quantile95(double[] xs) => xs.Quantile(0.95);
            }
            """;

        const string nodeType = "multi-math";
        var persistence = new InMemoryPersistenceService();

        var ntNode = MeshNode.FromPath($"type/{nodeType}") with
        {
            Name = nodeType,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition { }
        };
        await persistence.SaveNodeAsync(ntNode, SetupJsonOptions, TestContext.Current.CancellationToken);

        var c1 = new MeshNode("distributions", $"type/{nodeType}/_Source")
        {
            NodeType = "Code",
            Name = "Distributions",
            Content = new CodeConfiguration { Code = distributionsSource }
        };
        await persistence.SaveNodeAsync(c1, SetupJsonOptions, TestContext.Current.CancellationToken);

        var c2 = new MeshNode("stats", $"type/{nodeType}/_Source")
        {
            NodeType = "Code",
            Name = "Stats",
            Content = new CodeConfiguration { Code = statsSource }
        };
        await persistence.SaveNodeAsync(c2, SetupJsonOptions, TestContext.Current.CancellationToken);

        var service = CreateService(persistence);

        var node = MeshNode.FromPath("org/demo/multi") with
        {
            Name = "Multi",
            NodeType = $"type/{nodeType}",
            LastModified = DateTimeOffset.UtcNow
        };

        var assemblyPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);
        assemblyPath.Should().NotBeNull();
        File.Exists(assemblyPath).Should().BeTrue();

        var nodeName = _cacheService.SanitizeNodeName(node.Path);
        var loadContext = _cacheService.GetOrCreateLoadContext(nodeName);
        var assembly = loadContext.LoadNodeAssembly();
        assembly.Should().NotBeNull();

        var statsType = assembly!.GetType("StatsHelper");
        statsType.Should().NotBeNull("StatsHelper should be compiled into the assembly");

        var mean = statsType!.GetMethod("MeanOf", BindingFlags.Public | BindingFlags.Static);
        var meanResult = (double)mean!.Invoke(null, new object[] { new double[] { 1, 2, 3, 4 } })!;
        meanResult.Should().BeApproximately(2.5, 1e-9);

        var distType = assembly.GetType("DistributionsHelper");
        distType.Should().NotBeNull("DistributionsHelper should be compiled into the assembly");
    }
}
