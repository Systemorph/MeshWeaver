using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.PensionFund.Test;

/// <summary>
/// Basic contract for the #r-pluggable source-generator path: a NodeType whose Source
/// declares an <c>IScope&lt;,&gt;</c> business rule and pulls the BusinessRules framework +
/// its <c>ScopeCodeGenerator</c> in via <c>#r "nuget:..."</c> must compile cleanly — the
/// compiler resolves the packages from the mesh-local feed, <c>SourceGeneratorLoader</c>
/// discovers + runs the generator, and the emitted proxy compiles. We assert through the
/// canonical compile <b>activity log</b>: terminal <see cref="ActivityStatus.Succeeded"/> and
/// NO error/warning <see cref="LogMessage"/>s.
///
/// <para>A clean compile also proves the <c>#r</c> resolution itself worked: had
/// <c>#r "nuget:MeshWeaver.BusinessRules"</c> failed to resolve, <c>using MeshWeaver.BusinessRules;</c>
/// and <c>IScope&lt;,&gt;</c> would be unresolved symbols and the compile would error.</para>
/// </summary>
public class ScopeGeneratorViaRCompileTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    // A self-contained scope rule: state + an IScope interface whose computed member the
    // generator turns into a cached proxy. The two #r directives are the whole point —
    // BusinessRules and its generator are pulled in on demand, not baked into the platform.
    private const string ScopeSource = """
        #r "nuget:MeshWeaver.BusinessRules, 3.0.0-preview1"
        #r "nuget:MeshWeaver.BusinessRules.Generator, 3.0.0-preview1"
        using MeshWeaver.BusinessRules;

        public record CounterContent { public int Seed { get; init; } }

        public record CounterState { public int Value { get; init; } }

        public interface Doubled : IScope<string, CounterState>
        {
            int Result => GetStorage().Value * 2;
        }
        """;

    [Fact(Timeout = 120_000)]
    public async Task ScopeNodeType_WithBusinessRulesViaR_CompilesCleanly_NoActivityLogErrors()
    {
        var nodeTypePath = $"type/ScopeViaR_{Guid.NewGuid():N}";

        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = "Scope via #r",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config.WithContentType<CounterContent>()"
            },
            State = MeshNodeState.Active
        };

        await MeshService.CreateNode(typeNode)
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("code", $"{nodeTypePath}/Source")
            {
                NodeType = "Code",
                Name = "code",
                Content = new CodeConfiguration { Code = ScopeSource, Language = "csharp" },
                State = MeshNodeState.Active
            }))
            .Should().Within(30.Seconds()).Emit();

        // Compilation runs on node-type activation; wait for the terminal status on the NodeType.
        var node = await Mesh.GetMeshNodeStream(nodeTypePath)
            .Should().Within(90.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);
        var def = (NodeTypeDefinition)node.Content!;

        // Read the compile activity log it stamped, and surface its diagnostics in the test output.
        def.LastCompilationActivityPath.Should().NotBeNullOrEmpty(
            "a real compile stamps the activity path");
        var activity = await Mesh.GetMeshNodeStream(def.LastCompilationActivityPath!)
            .Where(n => n?.Content is ActivityLog log
                && log.Status is ActivityStatus.Succeeded or ActivityStatus.Failed)
            .Should().Within(20.Seconds()).Match(n => n is not null);
        var log = (ActivityLog)activity!.Content!;
        foreach (var m in log.Messages)
            Output.WriteLine($"[{m.LogLevel}] {m.Message}");

        // The contract: the #r-pulled generator ran and the node type compiled with no errors.
        def.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "the #r-resolved BusinessRules generator must compile the IScope node type cleanly");
        log.Messages.Where(m => m.LogLevel >= LogLevel.Error).Should().BeEmpty(
            "no compile errors should appear in the activity log");
        log.Messages.Where(m => m.LogLevel == LogLevel.Warning).Should().BeEmpty(
            "no compile warnings should appear in the activity log");
    }
}
