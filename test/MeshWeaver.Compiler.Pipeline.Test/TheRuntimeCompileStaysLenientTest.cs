using System.Linq;
using MeshWeaver.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>THE WARNING STANDARD IS A GATE POLICY. THE RUNTIME COMPILE STAYS LENIENT — ALWAYS.</b>
///
/// <para><see cref="EmitPipeline.CreateCompilationOptions"/> is ONE factory shared by the CI bake
/// and by <c>MeshNodeCompilationService</c>, the compile every portal replica runs for every
/// NodeType on every boot. Promoting warnings to errors there — the obvious way to "make in-mesh
/// C# hold to a warning standard" — would park every NodeType with a missing XML doc comment, in
/// production: parked ⇒ <c>DynamicTypePreWarmer</c> refuses readiness ⇒ the rollout stalls ⇒ no
/// instance actions at all. That is not a hypothetical; it is the outage this fleet spent a night
/// recovering from, and it is why the policy lives in the tester's <c>WarningBaseline</c> and the
/// compiler carries none.</para>
///
/// <para>These cases are the CONTROL for that separation, and they are deliberately written so
/// they FAIL if anyone ever adds a <c>GeneralDiagnosticOption</c>, a
/// <c>SpecificDiagnosticOption</c> escalating a warning, or a <c>WarningLevel</c>/nullable setting
/// that turns a warning into an error.</para>
/// </summary>
public class TheRuntimeCompileStaysLenientTest
{
    /// <summary>
    /// A NodeType whose public members carry NO doc comment must still EMIT. This is the exact
    /// shape the maintainer's report is about ("lots of missing xml comments"), exercised through
    /// the real option set rather than asserted about it.
    /// </summary>
    [Fact]
    public void AMissingDocComment_StillCompilesAndEmits()
    {
        var result = Emit("public class NotDocumented { public int Value; }");

        Assert.True(result.Success,
            "a missing XML doc comment must never stop a NodeType emitting — that is a parked type, "
            + "a refused readiness probe and a stalled rollout. "
            + string.Join("; ", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())));
        // …and it IS produced as a warning, so the gate has something to ratchet on. A green emit
        // with no diagnostic at all would mean the standard has nothing to measure.
        Assert.Contains(EmitPipelineAccess.Collect(result.Diagnostics),
            w => w.Id == CompileWarning.MissingDocComment);
    }

    /// <summary>The other codes the gate ratchets on are warnings too — never errors.</summary>
    [Theory]
    [InlineData("public class P { public int Go() { int unused = 42; return 1; } }")]          // CS0219
    [InlineData("/// <summary>See <see cref=\"Nope\"/>.</summary>\npublic class P { }")]        // CS1574
    [InlineData("/// <summary>A &euro; entity.</summary>\npublic class P { }")]                 // CS1570
    public void EveryDiagnosticTheGateRatchetsOn_IsAWarning_NotAnError(string source)
    {
        var result = Emit(source);

        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotEmpty(EmitPipelineAccess.Collect(result.Diagnostics));
    }

    /// <summary>
    /// The option set itself, asserted directly: no blanket escalation, and no per-diagnostic
    /// escalation either. The behavioural cases above would catch the codes they name; this
    /// catches an escalation of a code nobody has written a case for yet.
    /// </summary>
    [Fact]
    public void TheCanonicalOptions_EscalateNothing()
    {
        var options = EmitPipelineAccess.CompilationOptions();

        Assert.Equal(ReportDiagnostic.Default, options.GeneralDiagnosticOption);
        Assert.DoesNotContain(options.SpecificDiagnosticOptions,
            kv => kv.Value == ReportDiagnostic.Error);
    }

    /// <summary>
    /// 🚨 <c>NullableContextOptions.Annotations</c>, NEVER <c>Enable</c>. The annotation context is
    /// what stops the platform emitting CS8632 into content that correctly writes <c>string?</c>;
    /// it turns on NO nullable analysis, so it cannot make a compile that used to succeed fail.
    /// <c>Enable</c> would switch the whole CS86xx family on across every in-mesh source in the
    /// fleet at once — a content decision nobody has made, taken by a compiler option.
    /// </summary>
    [Fact]
    public void NullableIsAnnotationsOnly_SoNoNewDiagnosticCanAppear()
    {
        Assert.Equal(
            NullableContextOptions.Annotations,
            EmitPipelineAccess.CompilationOptions().NullableContextOptions);

        // The proof, not the setting: source that would earn a nullable WARNING under Enable
        // produces none here, and still emits.
        var result = Emit(
            "public class P { public string? Maybe; public string Take() => Maybe; }");

        Assert.True(result.Success);
        Assert.Empty(EmitPipelineAccess.Collect(result.Diagnostics)
            .Where(w => w.Id.StartsWith("CS86", System.StringComparison.Ordinal)));
    }

    private static EmitResultView Emit(string source)
    {
        var compilation = CSharpCompilation.Create(
            "DynamicNode_LeniencyProbe",
            [CSharpSyntaxTree.ParseText(source, EmitPipelineAccess.ParseOptions())],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            EmitPipelineAccess.CompilationOptions());
        using var image = new System.IO.MemoryStream();
        var result = compilation.Emit(image);
        return new EmitResultView(result.Success, [.. result.Diagnostics]);
    }

    private sealed record EmitResultView(bool Success, Diagnostic[] Diagnostics);
}
