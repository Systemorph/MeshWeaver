using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Issue #1592 — the diagnostics probe could not fail.
///
/// <para><b>What it did.</b> <c>lsp_diagnostics_for_node @Edu/DefinitelyNotARealNodeTypeXyz</c>
/// answered <c>{"ok":true,"diagnostics":[]}</c> on the live memex portal. The language service
/// mapped a null workspace — no node at that path — to <c>Array.Empty&lt;DiagnosticInfo&gt;()</c>,
/// which is byte-identical to what a NodeType that compiled cleanly produces.</para>
///
/// <para><b>Why that is worse than a wrong answer.</b> AGENTS.md makes this the instrument of a
/// mandated gate: <i>"Before prod, sweep every NodeType green … LspDiagnosticsForNode per type …
/// re-sweep until all read Ok"</i>, because a NodeType left at <c>CompileError</c> refuses portal
/// readiness and parks every instance hub for the full activation budget. A renamed type, a
/// mistyped path, or a partition the answering replica does not hold each read GREEN forever — and
/// the sweep reported all-clear having verified nothing. A gate whose probe cannot fail is the
/// same shape as a shard that never ran leaving the required check green (#1472).</para>
///
/// <para>These cases pin the distinction at the framework level, where the sweep's callers
/// (<c>LspPlugin</c>, <c>McpMeshPlugin</c>) get it from. The read underneath always HAD the
/// distinction — <c>GetMeshNodeOutcome</c> separates Present / Absent / Unavailable — it was being
/// discarded one layer up.</para>
/// </summary>
public class DiagnosticsCannotAnswerGreenForAMissingNodeTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshLanguageService LanguageService =>
        Mesh.ServiceProvider.GetRequiredService<IMeshLanguageService>();

    /// <summary>The reported case, verbatim in shape: an invented path.</summary>
    [Fact]
    public async Task AnInventedPathIsAbsent_NotClean()
    {
        var outcome = await LanguageService
            .GetDiagnostics($"type/DefinitelyNotARealNodeType{Guid.NewGuid():N}")
            .Should().Within(60.Seconds()).Emit();

        outcome.Status.Should().Be(NodeDiagnosticsStatus.Absent,
            "nothing resolved at the path, so nothing was checked — reporting that as clean is how "
            + "a pre-prod sweep over stale paths reports all-green having verified nothing");
        outcome.IsClean.Should().BeFalse(
            "IsClean is the one flag a terse caller reads; it must be false for every status that "
            + "did not actually compile, or the type buys nothing");
        outcome.Diagnostics.Should().BeEmpty(
            "emptiness is still the payload — what changed is that it is no longer the ANSWER");
    }

    /// <summary>
    /// The problem is only closed if the reason survives to the caller: a sweep must be able to say
    /// WHICH entry it could not check, or a hundred-line sweep is unreadable.
    /// </summary>
    [Fact]
    public async Task TheReasonNamesThePathThatCouldNotBeChecked()
    {
        var path = $"type/Missing{Guid.NewGuid():N}";

        var outcome = await LanguageService.GetDiagnostics(path)
            .Should().Within(60.Seconds()).Emit();

        outcome.DescribeProblem(path).Should().NotBeNull().And.Contain(path,
            "a sweep prints one line per type; the line has to identify the type");
    }

    /// <summary>
    /// A real node with nothing to compile is a THIRD answer, not the same as absent: the caller
    /// asked about a path that exists but is the wrong kind of node, and telling them "not found"
    /// would send them hunting for a typo that is not there.
    ///
    /// <para>The shape that produces it is precise — <c>GetCompilationInputsAsync</c> refuses
    /// exactly one thing, a node whose <c>NodeType</c> is unset. 🚨 Worth stating what this test
    /// establishes by contrast: a node with SOME other NodeType (a Markdown node, say) does not
    /// take this branch — it assembles an empty compilation and reads <c>Compiled</c> with no
    /// diagnostics. That is outside #1592's failure (the mandated sweep enumerates
    /// <c>nodeType:NodeType</c>, so it never asks a Markdown node), but it is a real second-order
    /// green and belongs in the record rather than in a comment nobody wrote.</para>
    /// </summary>
    [Fact]
    public async Task ANodeWithNoNodeTypeIsNotCompilable_NotAbsentAndNotClean()
    {
        var id = $"NoTypeNode{Guid.NewGuid():N}";
        var path = $"type/{id}";
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode(id, "type")
            {
                Name = "A node with no NodeType",
                // Content but no NodeType — the one shape GetCompilationInputsAsync refuses, and
                // the only way to reach this status. (A bare node with neither is rejected by the
                // write itself: "bare nodes are not allowed".)
                Content = new CodeConfiguration { Code = "not code", Language = "markdown" },
                State = MeshNodeState.Active,
            })
            .Should().Within(60.Seconds()).Emit();

        var outcome = await LanguageService.GetDiagnostics(path)
            .Should().Within(60.Seconds()).Emit();

        outcome.Status.Should().Be(NodeDiagnosticsStatus.NotCompilable,
            "the path is real — sending the caller to hunt for a typo would waste the sweep's time");
        outcome.IsClean.Should().BeFalse("nothing compiled, so nothing is clean");
        outcome.DescribeProblem(path).Should().Contain(path);
    }

    /// <summary>
    /// 🚨 The case that makes this a gate rather than a nicety. A NodeType that genuinely compiles
    /// must still read Compiled + clean — a fix that made everything fail would be no better than
    /// one that made everything pass.
    /// </summary>
    [Fact]
    public async Task ARealNodeTypeThatCompilesStillReadsCleanlyCompiled()
    {
        var id = $"CleanType{Guid.NewGuid():N}";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        await meshService.CreateNode(MeshNode.FromPath($"type/{id}") with
        {
            Name = id,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = $"config => config.WithContentType<{id}>()"
            },
            State = MeshNodeState.Active,
        }).Should().Within(60.Seconds()).Emit();

        await meshService.CreateNode(new MeshNode($"{id}.cs", $"type/{id}/Source")
        {
            NodeType = "Code",
            Name = $"{id}.cs",
            Content = new CodeConfiguration
            {
                Code = $"public record {id} {{ public string Id {{ get; init; }} = string.Empty; }}",
                Language = "csharp"
            },
            State = MeshNodeState.Active,
        }).Should().Within(60.Seconds()).Emit();

        var outcome = await LanguageService.GetDiagnostics($"type/{id}")
            .Should().Within(60.Seconds()).Emit();

        outcome.Status.Should().Be(NodeDiagnosticsStatus.Compiled);
        outcome.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error,
            "clean source must still read clean — got: {0}",
            string.Join("; ", outcome.Diagnostics.Select(d => $"{d.Id} {d.Severity} {d.Message}")));
        outcome.IsClean.Should().BeTrue();
    }
}
