using System;
using System.IO;
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
}
