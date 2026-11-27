using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 One compile attempt renders its diagnostics ONCE (MeshWeaver#8529). The compile pipeline's
/// reporting funnel logs the complete Roslyn verdict and the node keeps it in
/// <c>CompilationDiagnostics</c>; the per-type outcome line of the batch bake used to repeat the whole
/// multi-line text a third time — dozens of CS1701 warning lines per type — so one attempt matched
/// "Compilation failed for" three times and read as three attempts. The line now carries only the
/// verdict's shape. Pure test: no mesh.
/// </summary>
public class ABakeFailureLineCarriesTheVerdictShapeNotItsTextTest
{
    private const string Verdict =
        "Compilation failed: Compilation failed for 'U/T':\n"
        + "CS0246 Error (line 611): The type or namespace name 'BehaviorSubject<>' could not be found\n"
        + "CS0246 Error (line 587): The type or namespace name 'BehaviorSubject<>' could not be found\n"
        + "CS1701 Warning: Assuming assembly reference 'System.Linq.Expressions'";

    private static PreWarmOutcome Outcome(PreWarmStatus status, string? detail) =>
        new("U/T", status, detail);

    [Fact]
    public void ACompileError_IsSummarisedToItsErrorIds_AndNeverRepeatsTheDiagnosticsText()
    {
        var line = NodeTypeBatchBake.LogDetailOf(Outcome(PreWarmStatus.CompileError, Verdict));

        line.Should().Contain("CS0246 x2");
        line.Should().NotContain("Compilation failed for", "the failure record already says it, once");
        line.Should().NotContain("CS1701", "warnings are not part of the verdict's shape");
        line.Should().NotContain("BehaviorSubject", "the text is on the failure record and the node");
    }

    [Fact]
    public void NegativeControl_EveryOtherOutcome_KeepsItsDetailVerbatim()
    {
        NodeTypeBatchBake.LogDetailOf(Outcome(PreWarmStatus.TimedOut, "batch compile did not settle"))
            .Should().Be("batch compile did not settle");
        NodeTypeBatchBake.LogDetailOf(Outcome(PreWarmStatus.DeclaredSourcesMissing, Verdict))
            .Should().Be(Verdict, "only a CompileError verdict is summarised");
        NodeTypeBatchBake.LogDetailOf(Outcome(PreWarmStatus.CompileError, "FatalProtocolException: no source"))
            .Should().Be("FatalProtocolException: no source", "a failure that is not a Roslyn verdict is shown as is");
    }
}
