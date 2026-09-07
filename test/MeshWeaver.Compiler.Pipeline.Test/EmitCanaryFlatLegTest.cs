using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MeshWeaver.Compiler;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Contract for the emit canary's <c>flat=</c> leg (#890) — the probe that varies NESTING inside a
/// real <c>Emit</c>, and the only leg that can say whether a poisoned process is emit-dead or only
/// dead on the path <c>GetConsolidatedTypeParameters</c> walks.
///
/// <para><b>The claim this file exists to stop being an assumption.</b> The canary's source comment
/// asserted, from the day it was written, that <i>"a flat class would emit fine even on a poisoned
/// writer"</i>. Nothing ever measured it, and it is load-bearing twice over: it is why the canary
/// uses a nested-generic source, and it is why every one of the ~30 recorded occurrences has been
/// read as <c>PROCESS CANNOT EMIT</c> — a claim about emit as such, on evidence that only ever
/// exercised one shape. Three unanimous <c>dissect=READS-HEALTHY</c> readings (2026-09-05, 09-06,
/// 09-07) then closed the read-probe direction: leg 3 calls the symbol reads from a caller that is
/// not the metadata writer, so a fault that is wrong only at the writer's own call site reads
/// healthy there BY CONSTRUCTION. Leg 4 stays inside a real emit instead.</para>
/// </summary>
public class EmitCanaryFlatLegTest
{
    private const string Frame =
        "NamedTypeSymbol.Microsoft.Cci.ITypeDefinitionMember.get_ContainingTypeDefinition";

    private static string Threw(string site)
        => $"THREW NullReferenceException at {site}: Object reference not set to an instance of an object.";

    /// <summary>
    /// 🚨 The mutation guard, and it is the whole reason this leg can be trusted. The
    /// discriminator is <b>"the writer reached the #890 frame with no nested type in the
    /// compilation"</b> — which holds only while the flat source really has no nested type, no
    /// generic arity, and no members. Any one of those creeping back in (a helper field added
    /// "for realism", a generic parameter copied from the nested source) retires the
    /// discriminator in SILENCE: the leg keeps returning a token, the verdict keeps printing, and
    /// <c>flat=SAME-FRAME</c> stops meaning what it says. Asserted semantically off Roslyn's own
    /// binding rather than by string-matching the literal, so a rewrite that preserves the shape
    /// still passes and one that does not cannot.
    /// </summary>
    [Fact]
    public void TheFlatSource_HasNothingThatCouldReachTheFrameExceptTheGuard()
    {
        var compilation = CSharpCompilation.Create(
            "MeshWeaverFlatCanaryShape",
            syntaxTrees: [CSharpSyntaxTree.ParseText(EmitPipeline.FlatCanarySource)],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // SourceModule, not the merged global namespace: the latter also carries every type the
        // references declare at the root, and the claim here is about what the emit WRITES.
        var declared = compilation.SourceModule.GlobalNamespace.GetTypeMembers();
        declared.Should().ContainSingle(
            "the leg's claim is that the emit walks ONE type — a second one is a second chance to "
            + "reach the frame, and the verdict would no longer name the guard");

        var only = declared[0];
        only.ContainingType.Should().BeNull(
            "the type must be TOP-LEVEL: GetConsolidatedTypeParameters returns immediately when "
            + "AsNestedTypeDefinition answers null, and that early return is the whole probe");
        only.Arity.Should().Be(0,
            "a generic arity puts the type back on the consolidated-type-parameter path this leg "
            + "exists to stay off");
        only.GetTypeMembers().Should().BeEmpty(
            "a nested type — even an unused one — restores exactly the shape the nested canary "
            + "already covers, and the two legs would stop differing");
        only.GetMembers().Where(m => m.Kind is not SymbolKind.Method)
            .Should().BeEmpty(
                "only the implicit constructor may remain: any field/property is an "
                + "ITypeDefinitionMember of its own, i.e. a second caller of a "
                + "ContainingTypeDefinition getter, and the verdict could no longer say the guard "
                + "was the one that reached it");
    }

    /// <summary>
    /// The positive control. A leg whose source cannot emit on a HEALTHY process answers
    /// <c>flat=SAME-FRAME</c> or <c>flat=OTHER-FRAME</c> for a reason that has nothing to do with
    /// #890 — the discriminator would be permanently pinned to its scariest branch, which is the
    /// defect this file's siblings keep removing.
    /// </summary>
    [Fact]
    public void TheFlatSource_EmitsOnAHealthyProcess()
        => EmitPipeline.EmitCanaryForTest(CompileReferences.Default, EmitPipeline.FlatCanarySource)
            .Should().Be("OK",
                "if the flat source cannot emit here, every flat= reading in CI is measuring this "
                + "test's bug rather than the process under it");

    [Fact]
    public void AFlatEmitThatSUCCEEDS_SaysTheProcessIsNotEmitDead()
    {
        var reading = EmitPipeline.ClassifyFlatLeg("OK", Frame);

        reading.Should().StartWith("flat=EMITS");
        reading.Should().Contain("NOT emit-dead",
            "this is the finding: 'PROCESS CANNOT EMIT' would be true of the workload and false "
            + "of emit as such, and the mechanism narrows to the nested/generic walk");
    }

    /// <summary>
    /// 🚨 The branch the whole leg is for. A flat compilation dying in the #890 frame means the
    /// metadata writer reached <c>ITypeDefinitionMember.ContainingTypeDefinition</c> on a type
    /// whose containing type is null by construction — i.e. <c>AsNestedTypeDefinitionImpl</c>'s
    /// guard answered TRUE where it must answer FALSE. No recursion, no generics, no nesting: one
    /// method.
    /// </summary>
    [Fact]
    public void AFlatEmitDyingInTheSameFrame_IsTheOneMethodReproduction()
    {
        var reading = EmitPipeline.ClassifyFlatLeg(Threw(Frame), Frame);

        reading.Should().StartWith($"flat=SAME-FRAME@{Frame}");
        reading.Should().Contain("guard read TRUE where it must read FALSE",
            "the verdict has to say what was observed, not merely that something failed");
        reading.Should().Contain("dotnet/runtime",
            "this is the smallest form the defect can take, and where it has to go next");
    }

    [Fact]
    public void AFlatEmitDyingSOMEWHEREELSE_ConcludesNothing()
    {
        var reading = EmitPipeline.ClassifyFlatLeg(
            Threw("MetadataReference.CreateFromFile"), Frame);

        reading.Should().StartWith("flat=OTHER-FRAME@MetadataReference.CreateFromFile");
        reading.Should().Contain(Frame,
            "both sites are the evidence — the reader compares them, exactly as the DIVERGENT "
            + "branch of the main verdict does");
        reading.Should().NotContain("guard read TRUE",
            "two frames are two faults until shown otherwise; the strong claim is withheld, never "
            + "softened in place");
    }

    [Fact]
    public void ALegWithNoRecordedSite_IsInconclusive_NeverFoldedIntoAConclusion()
    {
        EmitPipeline.ClassifyFlatLeg("DIAGNOSTICS(CS0246)", Frame)
            .Should().StartWith("flat=INCONCLUSIVE",
                "a flat leg that produced compile diagnostics did not reproduce anything, and a "
                + "probe that cannot report 'I could not run' is a gate that passes on no input");

        EmitPipeline.ClassifyFlatLeg(
                "THREW NullReferenceException at (no stack): Object reference not set to an instance of an object.",
                Frame)
            .Should().StartWith("flat=INCONCLUSIVE",
                "'(no stack)' is an absence of evidence and may not be read as a match — the same "
                + "rule the two-leg verdict already applies");
    }

    /// <summary>
    /// The nested leg may itself have recorded no frame (the <c>DIVERGENT</c> branch reaches here
    /// too). A flat leg that threw is then still reported — with the comparison stated as
    /// unavailable — rather than silently upgraded.
    /// </summary>
    [Fact]
    public void AnUnrecordedNestedSite_NeverProducesTheStrongClaim()
        => EmitPipeline.ClassifyFlatLeg(Threw(Frame), nestedSite: null)
            .Should().StartWith("flat=OTHER-FRAME",
                "with no nested site to compare against, 'the same frame' has not been observed");

    /// <summary>
    /// An absent reading has to be visible as absent — the same rule <c>dissect=NOT-RUN</c>
    /// follows. A verdict that simply omitted the leg would read as a clean flat emit.
    /// </summary>
    [Fact]
    public void AVerdictWithNoFlatProbe_SaysSo()
        => EmitPipeline.Verdict(Threw(Frame), Threw(Frame))
            .Should().Contain("flat=NOT-RUN",
                "an omitted leg and a leg that answered must never look alike");

    [Fact]
    public void TheFlatReading_RidesTheVerdictOnBothTerminalBranches()
    {
        EmitPipeline.Verdict(Threw(Frame), Threw(Frame), dissect: null, flat: () => "OK")
            .Should().Contain("flat=EMITS",
                "BELOW-ROSLYN is where the reading is needed — it is the verdict that claims the "
                + "process cannot emit");

        EmitPipeline.Verdict(Threw(Frame), Threw("Other.Site"), dissect: null, flat: () => "OK")
            .Should().Contain("flat=EMITS",
                "DIVERGENT withholds the strong claim but still ran both emits, so the reading is "
                + "just as available and just as informative");

        EmitPipeline.Verdict(Threw(Frame), "OK", dissect: null, flat: () => "OK")
            .Should().NotContain("flat=",
                "REFERENCES means the pristine leg EMITTED — the process is demonstrably able to "
                + "emit, so the nesting question does not arise and running a fourth emit there "
                + "would be cost with no reading");
    }

    /// <summary>
    /// The leg runs on an already-failing path, so it may never become a second fault. A probe
    /// that throws while diagnosing destroys the evidence it exists to preserve.
    /// </summary>
    [Fact]
    public void AProbeThatFaults_ReportsItsOwnFailure_AndNeverEscapes()
        => EmitPipeline.Verdict(Threw(Frame), Threw(Frame), dissect: null,
                flat: () => throw new InvalidOperationException("probe blew up"))
            .Should().Contain("flat=UNAVAILABLE(InvalidOperationException",
                "'I could not look' and 'I looked and it was fine' must never share a token");
}
