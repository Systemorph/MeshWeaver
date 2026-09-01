using MeshWeaver.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A SUCCESSFUL in-mesh compile used to discard every warning it produced.
///
/// <para><c>emitResult.Diagnostics</c> was read only when <c>Success</c> was false, so green
/// compiles reported nothing — measured from the outside before this landed: a deliberate
/// <c>CS0219</c> (an unused local) added to an in-mesh source compiled <c>ok</c> with
/// "0 warning(s)". That is the ABSENCE OF A REPORT, not a clean build, and it is why in-mesh C#
/// was not held to the standard the compiled half is held to under <c>-warnaserror</c>: no
/// unused-code warnings, and therefore no doc-comment or cref ones either — even though the parse
/// options have always asked for <see cref="DocumentationMode.Diagnose"/>.</para>
///
/// <para>These cases pin the collection. That the collected warnings then reach the compile
/// ACTIVITY is the caller's half (<c>MeshNodeCompilationService</c> appends each through
/// <c>AppendWarning</c> before the outcome line), and it is a Warning severity on purpose:
/// <c>ActivityLog.Finish</c> rolls Error into Failed but leaves Warning alone, so surfacing them
/// cannot turn a green compile red on its own.</para>
/// </summary>
public class CompileWarningsReachTheActivityTest
{
    private static IReadOnlyList<string> WarningsOf(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, EmitPipelineAccess.ParseOptions());
        var compilation = CSharpCompilation.Create(
            "DynamicNode_WarningProbe",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            EmitPipelineAccess.CompilationOptions());
        return EmitPipelineAccess.Warnings(compilation.GetDiagnostics());
    }

    /// <summary>The exact diagnostic the outside-in measurement used.</summary>
    [Fact]
    public void AnUnusedLocal_IsReported_WithItsIdAndLine()
    {
        var warnings = WarningsOf("public class P { public static int Go() { int unused = 42; return 1; } }");

        var unused = Assert.Single(warnings, w => w.StartsWith("CS0219", StringComparison.Ordinal));
        // The generated source is ONE concatenated tree, so the line is the only locator a reader
        // gets — a warning without it cannot be acted on.
        Assert.Contains("line 1", unused);
    }

    /// <summary>
    /// 🚨 DOC AND CREF DIAGNOSTICS, which are the ones a cross-repo refactor breaks. The parse
    /// options ask for DocumentationMode.Diagnose; nothing was reading the result.
    /// </summary>
    [Fact]
    public void AnUnresolvableCref_IsReported()
    {
        var warnings = WarningsOf(
            "/// <summary>See <see cref=\"NoSuchTypeAnywhere\"/>.</summary>\npublic class P { }");

        // A cref that resolves to nothing is exactly the breakage an assembly move causes, and it
        // must be visible on the compile that produced it.
        Assert.Contains(warnings, w => w.StartsWith("CS1574", StringComparison.Ordinal)
                                    || w.StartsWith("CS1584", StringComparison.Ordinal));
    }

    /// <summary>A clean compile reports nothing — empty is an ANSWER here, and it only means
    /// something now that a non-empty one is possible.</summary>
    [Fact]
    public void ACleanCompile_ReportsNoWarnings()
        => Assert.Empty(WarningsOf("/// <summary>Clean.</summary>\npublic class P { }"));

    /// <summary>
    /// Deduped, ordered and CAPPED. One bad using-directive can produce hundreds of identical
    /// diagnostics, and an activity log that is 400 lines of the same warning is one nobody reads.
    /// The cap is NAMED in the last entry rather than applied silently — a truncation a reader
    /// cannot see is a lie about how much was wrong.
    /// </summary>
    [Fact]
    public void ManyWarnings_AreCappedAndTheRemainderIsCounted()
    {
        var body = string.Join("\n", Enumerable.Range(0, EmitPipelineAccess.MaxReported + 20)
            .Select(i => $"    public static int Go{i}() {{ int unused{i} = {i}; return 1; }}"));
        var warnings = WarningsOf($"public class P {{\n{body}\n}}");

        Assert.Equal(EmitPipelineAccess.MaxReported + 1, warnings.Count);
        // The reader is told the list was cut, and by how much.
        Assert.Contains("more warning(s) not listed", warnings[^1]);
    }
}
