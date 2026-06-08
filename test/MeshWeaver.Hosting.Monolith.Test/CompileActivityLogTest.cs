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
/// Verifies that every compile produces a proper <see cref="ActivityLog"/> — the executed
/// source-discovery queries, the matched Code paths, and the final compile result (full Roslyn
/// diagnostics on failure) — observable through the Activity Control Plane: the compile watcher
/// stamps <see cref="NodeTypeDefinition.LastCompilationActivityPath"/> on the NodeType, and the
/// activity node at that path carries the log. This is how the UI surfaces the compile result;
/// the test retrieves the activity log the same way (NOT via the deprecated
/// <c>GetCompilationPathRequest</c>, whose response is posted cross-hub and can be lost).
/// </summary>
public class CompileActivityLogTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// Creates the NodeType + its Source Code node, then OBSERVES the compile settle via the
    /// NodeType's own stream (the first-build kickoff drives Roslyn — no GetCompilationPathRequest).
    /// Returns the settled <see cref="NodeTypeDefinition"/> (CompilationStatus Ok/Error +
    /// LastCompilationActivityPath + CompilationError).
    /// </summary>
    private NodeTypeDefinition CreateAndCompile(
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

        MeshService.CreateNode(typeNode)
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
            .Should().Within(30.Seconds()).Emit();

        var node = Mesh.GetMeshNodeStream(nodeTypePath)
            .Should().Within(40.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);
        return (NodeTypeDefinition)node.Content!;
    }

    /// <summary>
    /// Reads the <see cref="ActivityLog"/> at <paramref name="activityPath"/> via the stream,
    /// waiting for the activity to reach a TERMINAL status (anything but
    /// <see cref="ActivityStatus.Running"/>). The compile activity is created Running and only
    /// later flipped to Succeeded/Failed; a one-shot "first ActivityLog emission" read races that
    /// transition and captures the intermediate Running. Crucially, the activity's terminal write
    /// is a CROSS-HUB write (the inline RunCompile runs on the NodeType hub, the activity lives on
    /// its own per-node hub) while the parent's <c>CompilationStatus = Ok/Error</c> is a fast OWN
    /// write — so by the time <see cref="CreateAndCompile"/> observes the parent settle, the
    /// activity terminal write may not have landed yet. Wait on the actual condition (terminal
    /// status), not the first emission.
    /// </summary>
    private ActivityLog ReadActivityLog(string activityPath) =>
        (ActivityLog)Mesh.GetMeshNodeStream(activityPath)
            .Should().Within(15.Seconds())
            .Match(n => n?.Content is ActivityLog log && log.Status != ActivityStatus.Running)
            .Content!;

    [Fact(Timeout = 60_000)]
    public void SuccessfulCompile_ReportsActivityLogWithSourceQueriesAndMatchedPaths()
    {
        var def = CreateAndCompile("LogStory",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<LogStory>()" },
            ("code", "public record LogStory { public string Title { get; init; } = string.Empty; }"));

        def.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"compile should succeed; error: {def.CompilationError}");
        def.LastCompilationActivityPath.Should().NotBeNullOrEmpty(
            "a successful compile must link its activity log");

        var log = ReadActivityLog(def.LastCompilationActivityPath!);
        log.Category.Should().Be(ActivityCategory.Compilation);
        log.Status.Should().Be(ActivityStatus.Succeeded);

        var infos = log.Infos();
        infos.Should().Contain(m => m.Message.Contains("Source query"),
            "the executed source-discovery queries must be in the log");
        infos.Should().Contain(m => m.Message.Contains("matched 1 Code"),
            "the matched-Code-paths summary must be in the log");
        infos.Should().Contain(m => m.Message.Contains("Compiled assembly"),
            "the final compile result must be in the log");
    }

    [Fact(Timeout = 60_000)]
    public void FailedCompile_FaultsActivityAndSurfacesFullDiagnostics()
    {
        var def = CreateAndCompile("LogBroken",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<LogBroken>()" },
            ("code", "public record LogBroken { this is not valid C# }"));

        def.CompilationStatus.Should().Be(CompilationStatus.Error,
            "invalid source must fault the compile");
        def.CompilationError.Should().NotBeNullOrEmpty(
            "the NodeType must surface the human-readable failure summary");
        def.LastCompilationActivityPath.Should().NotBeNullOrEmpty(
            "a failed compile must still link its activity log so the UI can show diagnostics");

        var log = ReadActivityLog(def.LastCompilationActivityPath!);
        log.Status.Should().Be(ActivityStatus.Failed,
            "the activity status must reflect that compile faulted");

        var errors = log.Errors();
        errors.Should().NotBeEmpty(
            "the failure detail must be captured in the log as Error message(s)");
        // 🚨 The FULL Roslyn diagnostics must be surfaced — not a bare "Compilation failed for X:".
        // The invalid source ("this is not valid C#") yields a CS#### diagnostic; assert it is
        // present so a regression to empty/summary-only error surfacing is caught.
        errors.Should().Contain(m => m.Message.Contains("CS"),
            "the activity log must carry the concrete Roslyn diagnostic codes (CS####), not just a summary");
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
