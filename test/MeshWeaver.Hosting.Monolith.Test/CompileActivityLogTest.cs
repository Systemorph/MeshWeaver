using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Verifies that <see cref="GetCompilationPathRequest"/> reports compilation
/// state through the response <see cref="GetCompilationPathResponse.Log"/> —
/// the <see cref="ActivityLog"/> the compile pipeline produces. The user's
/// design: every compile is a proper Activity with the executed source
/// queries, the matched Code paths, and the final compile result, so a
/// caller can render "compile saw 0 source files" without re-running the
/// pipeline.
/// </summary>
public class CompileActivityLogTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private IObservable<GetCompilationPathResponse> CreateAndCompile(
        string nodeTypeId,
        NodeTypeDefinition definition,
        params (string Name, string Code)[] sources)
    {
        var nodeTypePath = $"type/{nodeTypeId}";
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = nodeTypeId,
            NodeType = MeshNode.NodeTypePath,
            Content = definition,
            State = MeshNodeState.Active
        };

        return MeshService.CreateNode(typeNode)
            .SelectMany(_ => sources
                .Select(source => MeshService.CreateNode(new MeshNode(source.Name, $"{nodeTypePath}/Source")
                {
                    NodeType = "Code",
                    Name = source.Name,
                    Content = new CodeConfiguration { Code = source.Code, Language = "csharp" },
                    State = MeshNodeState.Active
                }))
                .Aggregate(Observable.Return<MeshNode?>(null), (chain, next) =>
                    chain.SelectMany(_ => next.Select(n => (MeshNode?)n))))
            .SelectMany(_ => Mesh.Observe(
                    new GetCompilationPathRequest(),
                    o => o.WithTarget(new Address(nodeTypePath))))
            .Select(d => d.Message);
    }

    [Fact(Timeout = 30_000)]
    public void SuccessfulCompile_ReportsActivityLogWithSourceQueriesAndMatchedPaths()
    {
        var response = CreateAndCompile("LogStory",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<LogStory>()" },
            ("code", "public record LogStory { public string Title { get; init; } = string.Empty; }"))
            .Should().Within(25.Seconds()).Emit();

        response.Success.Should().BeTrue($"compile should succeed; error: {response.Error}");
        response.Log.Should().NotBeNull("activity log must be returned for every compile");
        response.Log!.Category.Should().Be(ActivityCategory.Compilation);
        response.Log.Status.Should().Be(ActivityStatus.Succeeded);
        response.Log.HubPath.Should().Be("type/LogStory");

        var infos = response.Log.Infos();
        infos.Should().Contain(m => m.Message.Contains("Source query"),
            "the executed source-discovery queries must be in the log");
        infos.Should().Contain(m => m.Message.Contains("matched 1 Code"),
            "the matched-Code-paths summary must be in the log");
        infos.Should().Contain(m => m.Message.Contains("Compiled assembly"),
            "the final compile result must be in the log");
    }

    [Fact(Timeout = 30_000)]
    public void FailedCompile_FaultsActivityAndIncludesLogInResponse()
    {
        var response = CreateAndCompile("LogBroken",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<LogBroken>()" },
            ("code", "public record LogBroken { this is not valid C# }"))
            .Should().Within(25.Seconds()).Emit();

        response.Success.Should().BeFalse("invalid source must fault the compile");
        response.Log.Should().NotBeNull("the activity log must be returned even when compile fails");
        response.Log!.Status.Should().Be(ActivityStatus.Failed,
            "the activity status must reflect that compile faulted");
        response.Log.Errors().Should().NotBeEmpty(
            "the failure detail must be captured in the log as an Error message");
        response.Error.Should().NotBeNullOrEmpty(
            "the response must surface the human-readable failure summary");
    }

    /// <summary>
    /// <see cref="NodeCompilationResult.CompiledSources"/> must capture every
    /// source Code node that fed the compile, keyed by full path with the value
    /// being the source node's <c>MeshNode.Version</c>. The compile watcher
    /// persists this onto the NodeType MeshNode as
    /// <see cref="NodeTypeDefinition.CompiledSources"/> so future
    /// recompile-needed checks survive portal restart and silo move.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void SuccessfulCompile_PopulatesCompiledSourcesSnapshot()
    {
        const string nodeTypeId = "SnapStory";
        const string nodeTypePath = "type/SnapStory";
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = nodeTypeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config.WithContentType<SnapStory>()"
            },
            State = MeshNodeState.Active
        };
        MeshService.CreateNode(typeNode).Should().Emit();

        MeshService.CreateNode(new MeshNode("code", $"{nodeTypePath}/Source")
        {
            NodeType = "Code",
            Name = "code",
            Content = new CodeConfiguration
            {
                Code = "public record SnapStory { public string Title { get; init; } = string.Empty; }",
                Language = "csharp"
            },
            State = MeshNodeState.Active
        }).Should().Emit();

        // Drive the compile through the same service the watcher uses. The
        // result carries the snapshot directly — no need to wait for the
        // separate watcher → UpdateMeshNode round-trip.
        var compilationService = Mesh.ServiceProvider
            .GetRequiredService<IMeshNodeCompilationService>();
        var result = compilationService.CompileAndGetConfigurations(typeNode)
            .Should().Within(25.Seconds()).Emit();

        result.Should().NotBeNull();
        result!.AssemblyLocation.Should().NotBeNullOrEmpty(
            "compile should succeed and produce an assembly");

        result.CompiledSources.Should().NotBeNull(
            "CompiledSources must be set whenever the compile produces an assembly");
        result.CompiledSources!.Should().ContainKey($"{nodeTypePath}/Source/code",
            "the snapshot must include every source Code node the compile consumed");
        result.CompiledSources![$"{nodeTypePath}/Source/code"].Should().BeGreaterThanOrEqualTo(0,
            "snapshot value is the source MeshNode.Version (always non-negative)");
    }
}
