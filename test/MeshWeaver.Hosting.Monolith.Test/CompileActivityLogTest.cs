using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
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
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private Task<GetCompilationPathResponse> CreateAndCompile(
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
            .Select(d => d.Message)
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task SuccessfulCompile_ReportsActivityLogWithSourceQueriesAndMatchedPaths()
    {
        var response = await CreateAndCompile("LogStory",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<LogStory>()" },
            ("code", "public record LogStory { public string Title { get; init; } = string.Empty; }"));

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
    public async Task FailedCompile_FaultsActivityAndIncludesLogInResponse()
    {
        var response = await CreateAndCompile("LogBroken",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<LogBroken>()" },
            ("code", "public record LogBroken { this is not valid C# }"));

        response.Success.Should().BeFalse("invalid source must fault the compile");
        response.Log.Should().NotBeNull("the activity log must be returned even when compile fails");
        response.Log!.Status.Should().Be(ActivityStatus.Failed,
            "the activity status must reflect that compile faulted");
        response.Log.Errors().Should().NotBeEmpty(
            "the failure detail must be captured in the log as an Error message");
        response.Error.Should().NotBeNullOrEmpty(
            "the response must surface the human-readable failure summary");
    }
}
