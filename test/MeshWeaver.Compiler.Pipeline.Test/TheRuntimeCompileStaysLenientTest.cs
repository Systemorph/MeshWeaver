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
        // …and it is not REPORTED either: doc completeness is centrally suppressed for in-mesh C#
        // exactly as core suppresses it for src/ (CompileWarning.NotReported — CS1591;CS1573;CS1712
        // there, CS1591;CS1573;CS1712 in Directory.Build.props here).
        Assert.DoesNotContain(EmitPipelineAccess.Collect(result.Diagnostics),
            w => w.Id == CompileWarning.MissingDocComment);
    }

    /// <summary>
    /// 🚨 The suppression is a PARITY list, not a blanket. The two families the fleet's own
    /// <c>NoWarn</c>s carry are dropped; every other warning still reaches the gate, or the
    /// standard would be a tick over nothing.
    /// </summary>
    [Theory]
    [InlineData("CS1591", "public class P { public int V; }")]
    [InlineData("CS1573", "/// <summary>S.</summary>\n/// <param name=\"a\">A.</param>\n"
                          + "public class P { /// <summary>M.</summary>\n"
                          + "/// <param name=\"a\">A.</param>\npublic void M(int a, int b) { } }")]
    // 🚨 CS1712 was keyed into the policy by #4591 and exercised by nothing until this line. Its
    // shape is exactly CS1573's one level up and is easy to get wrong: the compiler says "…but
    // other type parameters do", so ONE documented <typeparam> beside an undocumented one is
    // required. A type with no <typeparam> at all produces CS1591 instead — measured, as an empty
    // diagnostic set on the first attempt at this case.
    [InlineData("CS1712", "/// <summary>S.</summary>\n/// <typeparam name=\"T\">T.</typeparam>\n"
                          + "public class P<T, U> { }")]
    public void ACentrallySuppressedCode_IsNotReported(string code, string source)
    {
        var result = Emit(source);

        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Id == code);          // the compiler produced it
        Assert.DoesNotContain(EmitPipelineAccess.Collect(result.Diagnostics), w => w.Id == code);
    }

    /// <summary>
    /// 🚨 THE SET ITSELF, member by member — the one assertion a compiler probe cannot make.
    ///
    /// <para>The cases above prove the FILTER for every id that a one-reference
    /// <see cref="CSharpCompilation"/> can actually produce. <c>CS1701</c>/<c>CS1702</c> are
    /// reference-set SKEW: they need two assemblies disagreeing about a third's version, which is
    /// precisely what a hand-assembled reference set presents and a probe here cannot. So they were
    /// keyed into <see cref="CompileWarning.NotReported"/> and reachable by no test — a typo
    /// (<c>CS17O2</c>), a dropped entry or a sixth id added by mistake would all have been silent,
    /// and CS1701 was the 95-entry root cause the whole change existed to retire.</para>
    ///
    /// <para>This pins the membership literally, in both directions, and goes through
    /// <see cref="CompileWarning.IsNotReported"/> rather than the collection so the accessor the
    /// pipeline actually calls (<c>EmitPipeline.Collect</c>) is the one under test. Changing the
    /// policy means changing this list in the same commit — which is the point.</para>
    /// </summary>
    [Fact]
    public void TheNotReportedSet_IsExactlyTheParityList()
    {
        Assert.Equal(
            ["CS1573", "CS1591", "CS1701", "CS1702", "CS1712"],
            CompileWarning.NotReported.OrderBy(id => id, System.StringComparer.Ordinal));

        foreach (var id in new[] { "CS1573", "CS1591", "CS1701", "CS1702", "CS1712" })
            Assert.True(CompileWarning.IsNotReported(id), $"{id} must stay centrally suppressed");

        // The control: everything the ratchets measure must NOT be on the list, or the standard
        // would be a tick over nothing. CS1572/CS1574 in particular are one character from the
        // suppressed CS1573/CS1712 and mean the opposite — a tag that is WRONG, not one missing.
        foreach (var id in new[] { "CS0219", "CS1570", "CS1571", "CS1572", "CS1574", "CS1584", "CS1587", "CS0419" })
            Assert.False(CompileWarning.IsNotReported(id), $"{id} must keep reaching the gate");
    }

    /// <summary>The control for the case above: a code NOT on the parity list still reaches the
    /// gate, so "nothing was reported" can never mean "nothing is reported".</summary>
    [Theory]
    [InlineData("CS0219", "public class P { public int Go() { int unused = 42; return 1; } }")]
    [InlineData("CS1574", "/// <summary>See <see cref=\"Nope\"/>.</summary>\npublic class P { }")]
    [InlineData("CS1570", "/// <summary>A &euro; entity.</summary>\npublic class P { }")]
    public void ACodeThatIsNotSuppressed_StillReachesTheGate(string code, string source)
    {
        Assert.Contains(EmitPipelineAccess.Collect(Emit(source).Diagnostics), w => w.Id == code);
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
