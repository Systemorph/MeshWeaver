using System;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// Issue #3888 — the speculative pre-flight could not fail.
///
/// <para><b>What it did.</b> On <c>memex.systemorph.com</c>, 2026-09-10, as a global admin:
/// <c>lsp_check_node @BinaryClickerV2/BinaryToggle</c> with <c>proposedCode</c> =
/// <c>"this is definitely not valid C# ###"</c> answered <c>{"ok":true,"diagnostics":[]}</c>. Not a
/// wrong verdict about the code — no verdict at all, wearing the costume of a clean one. The
/// NodeType lives in a partition that identity has no read grant on, so nothing was ever compiled;
/// <c>CheckSpeculative</c> mapped BOTH of its no-answer branches (owner unresolvable, and no
/// compilation inputs) to <c>Array.Empty&lt;DiagnosticInfo&gt;()</c>, which is byte-identical to
/// what a clean compile produces.</para>
///
/// <para><b>Why it is the worst shape available.</b> The <c>/code</c> skill's edit loop is built on
/// this call — <i>"edit a Source/*.cs file in your head → lsp_check_node → if diagnostics, fix →
/// repeat → only then patch + compile"</i> — so a probe that cannot fail blesses every proposed
/// edit against every path it cannot reach. AGENTS.md states the rule absolutely: <i>"A
/// verification step that cannot fail is not a verification step"</i>, and <i>"ok:false with a
/// status other than Compiled … is a sweep FAILURE, not a pass — that entry was never
/// checked."</i></para>
///
/// <para><b>The same defect, already fixed next door.</b> #1592/#1618 gave
/// <see cref="IMeshLanguageService.GetDiagnostics"/> a <see cref="NodeDiagnosticsOutcome"/> for
/// exactly this reason. It was left in the sibling method because ONE return type served two
/// consumers with OPPOSITE needs: the Monaco editor, for which silence is right (squiggles computed
/// under the wrong language rules are worse than none), and the MCP/agent tool, for which silence
/// is rendered as <c>ok:true</c> and therefore reads as approval. The fix is not to make the editor
/// noisy — it is to let the instrument SAY which of the two it is doing, via
/// <see cref="IMeshLanguageService.CheckSpeculativeOutcome"/>.</para>
///
/// <para>🚨 <b>Both directions are pinned here on purpose.</b> A fix that made every check answer
/// "not checked" would satisfy the negative cases alone and be no better than the bug. So
/// <see cref="ARealNodeTypeStillCompilesTheProposal_CleanReadsClean"/> and
/// <see cref="ARealNodeTypeStillCompilesTheProposal_BrokenReadsBroken"/> hold the other end: the
/// substituted source really is compiled, and its verdict really does follow the code.</para>
/// </summary>
public class SpeculativeCheckCannotAnswerGreenForAnUncheckedNodeTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>The measured payload, verbatim: text that cannot parse as C# at all.</summary>
    private const string NotEvenCSharp = "this is definitely not valid C# ###";

    private IMeshLanguageService LanguageService =>
        Mesh.ServiceProvider.GetRequiredService<IMeshLanguageService>();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    // ─────────────────────────────── the negative controls: it must be able to refuse

    /// <summary>
    /// The reported case, verbatim in shape: a NodeType path that resolves to nothing, and a
    /// proposal that is not C#. The check must NOT come back clean.
    /// </summary>
    [Fact]
    public async Task AnInventedNodeTypePathIsAbsent_NotClean()
    {
        var path = $"type/DefinitelyNotARealNodeType{Guid.NewGuid():N}";

        var outcome = await LanguageService
            .CheckSpeculativeOutcome(path, $"{path}/Source/Probe.cs", NotEvenCSharp)
            .Should().Within(TestTimeouts.Convergence).Emit();

        outcome.Status.Should().Be(NodeDiagnosticsStatus.Absent,
            "nothing resolved at the path, so the proposed source was never compiled against "
            + "anything — reporting that as a clean pre-flight is how the /code loop came to bless "
            + "text that is not even C# (#3888)");
        outcome.IsClean.Should().BeFalse(
            "IsClean is the one flag a terse caller reads, and lsp_check_node renders it as ok; it "
            + "must be false for every status that did not actually compile");
        outcome.Diagnostics.Should().BeEmpty(
            "emptiness is still the payload — what changed is that it is no longer the ANSWER");
    }

    /// <summary>
    /// The reason has to survive to the caller, or a refusal is just a different unreadable green:
    /// the message must name the path that could not be checked.
    /// </summary>
    [Fact]
    public async Task TheRefusalNamesThePathThatCouldNotBeChecked()
    {
        var path = $"type/Missing{Guid.NewGuid():N}";

        var outcome = await LanguageService
            .CheckSpeculativeOutcome(path, $"{path}/Source/Probe.cs", NotEvenCSharp)
            .Should().Within(TestTimeouts.Convergence).Emit();

        var problem = outcome.DescribeProblem(path);
        problem.Should().NotBeNull(
            "a non-Compiled status without a reason hands the caller a bare 'no' it cannot act on");
        problem!.Should().Contain(path,
            "the agent asked about one path and gets one line back; the line has to identify it");
    }

    /// <summary>
    /// 🚨 <b>The measurement that corrected the issue's own reading of the code.</b> #3888 named TWO
    /// silent branches and said which one produced the live observation was "not established". It is
    /// now: it can only be the unresolvable-owner branch, because the other one — the
    /// <c>inputs is null</c> arm — is <b>unreachable from the speculative path</b>. Reaching it
    /// requires <c>GetCompilationInputsAsync</c> to answer null, which it does for exactly one
    /// shape, a node whose <c>NodeType</c> is unset; and a node whose <c>NodeType</c> is unset is
    /// classified as SCRIPT here, never as NodeType, so it never asks for compilation inputs at all.
    ///
    /// <para>This case pins what such a node ACTUALLY does, which is the opposite of a silent
    /// branch: it is compiled — as a script, which is what the kernel would really run that text as
    /// — so it is genuinely checked, and the verdict follows the code. Written as a control in both
    /// directions for that arm, so it cannot pass by the arm doing nothing.</para>
    ///
    /// <para>The <c>NotCompilable</c> mapping stays in the implementation as the honest reading of a
    /// nullable contract — the alternative is mapping a null to <c>Compiled</c>, which IS the defect
    /// — but nothing here pretends to pin it. A test asserting an unreachable branch would be one
    /// more control that cannot fail.</para>
    /// </summary>
    [Fact]
    public async Task ANodeThatIsNotANodeTypeIsGenuinelyCheckedAsAScript()
    {
        var id = $"NoTypeNode{Guid.NewGuid():N}";
        var path = $"type/{id}";
        await MeshService.CreateNode(new MeshNode(id, "type")
        {
            Name = "A node with no NodeType",
            Content = new CodeConfiguration { Code = "not code", Language = "markdown" },
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        var broken = await LanguageService
            .CheckSpeculativeOutcome(path, $"{path}/Source/Probe.cs", NotEvenCSharp)
            .Should().Within(TestTimeouts.Convergence).Emit();

        broken.Status.Should().Be(NodeDiagnosticsStatus.Compiled,
            "the owner exists, so the kernel's script environment is the honest one to diagnose in "
            + "— something really was compiled");
        broken.Diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error,
            "text that cannot parse must produce Roslyn errors even on the script arm");
        broken.IsClean.Should().BeFalse();

        var clean = await LanguageService
            .CheckSpeculativeOutcome(path, $"{path}/Source/Probe.cs", "var probe = 1 + 1;")
            .Should().Within(TestTimeouts.Convergence).Emit();

        clean.Status.Should().Be(NodeDiagnosticsStatus.Compiled);
        clean.IsClean.Should().BeTrue(
            "the other direction: valid script text must still read clean, or this arm would be "
            + "refusing everything rather than checking anything — got: {0}",
            string.Join("; ", clean.Diagnostics.Select(d => $"{d.Id} {d.Severity} {d.Message}")));
    }

    // ─────────────────────────── the positive controls: it must still actually check

    /// <summary>
    /// 🚨 The control that stops the fix from passing by making everything fail. A real NodeType
    /// with a clean proposed source must still read <see cref="NodeDiagnosticsStatus.Compiled"/>
    /// and clean — an instrument that refuses everything is exactly as useless as one that blesses
    /// everything.
    /// </summary>
    [Fact]
    public async Task ARealNodeTypeStillCompilesTheProposal_CleanReadsClean()
    {
        var (typePath, sourcePath, typeName) = await ANodeTypeWithOneSource();

        var outcome = await LanguageService
            .CheckSpeculativeOutcome(
                typePath, sourcePath,
                $"public record {typeName} {{ public string Title {{ get; init; }} = string.Empty; }}")
            .Should().Within(TestTimeouts.Convergence).Emit();

        outcome.Status.Should().Be(NodeDiagnosticsStatus.Compiled,
            "the NodeType resolved and Roslyn answered — this is the ONE status whose empty "
            + "diagnostics list means clean");
        outcome.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error,
            "clean source must still read clean — got: {0}",
            string.Join("; ", outcome.Diagnostics.Select(d => $"{d.Id} {d.Severity} {d.Message}")));
        outcome.IsClean.Should().BeTrue();
    }

    /// <summary>
    /// The other half of the positive control: the substituted source is genuinely COMPILED, so a
    /// broken proposal against a resolvable NodeType comes back Compiled-with-errors. Without this,
    /// the suite could not tell "the checker works" from "the checker resolves paths and stops".
    /// </summary>
    [Fact]
    public async Task ARealNodeTypeStillCompilesTheProposal_BrokenReadsBroken()
    {
        var (typePath, sourcePath, _) = await ANodeTypeWithOneSource();

        var outcome = await LanguageService
            .CheckSpeculativeOutcome(typePath, sourcePath, NotEvenCSharp)
            .Should().Within(TestTimeouts.Convergence).Emit();

        outcome.Status.Should().Be(NodeDiagnosticsStatus.Compiled,
            "the path resolved and Roslyn ran — the failure is in the CODE, not in the lookup");
        outcome.Diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error,
            "text that cannot parse as C# must produce Roslyn errors; a check that answers empty "
            + "here is the #3888 defect wearing a resolvable path");
        outcome.IsClean.Should().BeFalse();
    }

    // ────────────────────────────────────── the split is deliberate, and stays

    /// <summary>
    /// The editor's overload keeps its silence. This is not an oversight being tolerated: Monaco's
    /// contract is "no diagnostics means no squiggles", and a squiggle computed under the wrong
    /// language rules is worse than none. Pinning it here says the two overloads differ ON PURPOSE
    /// — so a later reader cannot "fix" the list overload and quietly paint phantom errors into
    /// every editor whose NodeType read was slow.
    /// </summary>
    [Fact]
    public async Task TheEditorOverloadStaysSilentForAPathItCannotResolve()
    {
        var path = $"type/DefinitelyNotARealNodeType{Guid.NewGuid():N}";

        var diagnostics = await LanguageService
            .CheckSpeculative(path, $"{path}/Source/Probe.cs", NotEvenCSharp)
            .Should().Within(TestTimeouts.Convergence).Emit();

        diagnostics.Should().BeEmpty(
            "the list overload is the EDITOR's contract and cannot carry a status; that is exactly "
            + "why a caller rendering a verdict must use CheckSpeculativeOutcome instead");
    }

    // ────────────────────────────────────────────────────────────────────────── fixtures

    /// <summary>A real NodeType with one real source file — the subject of the positive controls.</summary>
    private async Task<(string TypePath, string SourcePath, string TypeName)> ANodeTypeWithOneSource()
    {
        var id = $"CleanType{Guid.NewGuid():N}";
        var typePath = $"type/{id}";

        await MeshService.CreateNode(MeshNode.FromPath(typePath) with
        {
            Name = id,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = $"config => config.WithContentType<{id}>()"
            },
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        await MeshService.CreateNode(new MeshNode($"{id}.cs", $"{typePath}/Source")
        {
            NodeType = "Code",
            Name = $"{id}.cs",
            Content = new CodeConfiguration
            {
                Code = $"public record {id} {{ public string Id {{ get; init; }} = string.Empty; }}",
                Language = "csharp"
            },
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        return (typePath, $"{typePath}/Source/{id}.cs", id);
    }
}
