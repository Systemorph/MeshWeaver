using MeshWeaver.Compiler;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Leg 3 of the emit canary — the DISSECTION (#890) — and the remedy the verdict hands out.
///
/// <para><b>The two bugs this file pins.</b></para>
///
/// <para><b>1. A remedy nobody can execute.</b> From 2026-08-28 the <c>BELOW-ROSLYN</c> verdict
/// told CI triage to *"capture a core dump and re-run with tiering disabled"*. The dump half is
/// unfollowable by construction: <c>DOTNET_DbgEnableMiniDump</c> fires on a SIGNAL, and a #890
/// process never signals — it throws a managed exception, logs it, keeps running, and is killed by
/// the harness wall-clock cap as <c>exit=124</c> (SIGTERM), which writes no dump. Nine occurrences
/// carried that advice and produced exactly zero dumps. A diagnostic whose remedy cannot be
/// performed is the prose form of a gate that cannot fail.</para>
///
/// <para><b>2. The residual the verdict named and did not measure.</b> <c>BELOW-ROSLYN</c> says in
/// its own text that it *"does not separate a corrupted heap from a miscompiled Roslyn method"*.
/// Legs 1 and 2 both ask "can this process EMIT?"; neither asks the far cheaper question the stack
/// points straight at — <b>can it still perform the READ that the emit dies on?</b> Leg 3 asks it,
/// through the ordinary <c>ContainingType</c> property and through the very
/// <c>Cci.ITypeDefinitionMember.ContainingTypeDefinition</c> frame every #890 stack ends in.</para>
///
/// <para>🚨 <b>The load-bearing test here is the healthy-process one.</b> Leg 3 reaches internal
/// Roslyn shapes by reflection, so a Roslyn rename would turn every future occurrence into
/// <c>dissect=UNAVAILABLE</c> — the discriminator retired, silently, with nothing going red. That
/// is the same failure mode <see cref="EmitCanaryControlTest.TheControl_CanStillEmitTheCanarySource"/>
/// exists to refuse for leg 2.</para>
/// </summary>
public class EmitCanaryDissectionTest
{
    private const string Site = "MetadataWriter.GetConsolidatedTypeParameters";

    private static string Threw(string site = Site)
        => $"THREW NullReferenceException at {site}: Object reference not set to an instance of an object.";

    /// <summary>
    /// 🚨 The non-vacuity guard. On a healthy process BOTH reads must resolve and answer
    /// correctly — if either leg degrades to <c>UNAVAILABLE</c>, leg 3 has stopped measuring
    /// anything and every future #890 occurrence reports a non-answer.
    /// </summary>
    [Fact]
    public void OnAHealthyProcess_BothReadsResolveAndAnswerCorrectly()
    {
        var dissection = EmitPipeline.DissectTheNull(() => CompileReferences.Default);

        dissection.Should().Contain("symbol:OK",
            "the ordinary ContainingType property must read the nested canary type's container on "
            + "a healthy process — a NULL here would be the fault itself, and an UNAVAILABLE means "
            + "the canary source stopped binding");

        dissection.Should().Contain("cci:OK",
            "the Cci explicit interface implementation is the EXACT frame every #890 stack dies "
            + "in, and it is reached by reflection through internal Roslyn shapes. If a Roslyn "
            + "rename breaks that reach, this is the only thing that says so — otherwise leg 3 "
            + "answers UNAVAILABLE for ever and #890's discriminator is gone with nothing red");

        dissection.Should().StartWith("dissect=READS-HEALTHY",
            "every leg resolved and answered, so the verdict must be the one that says the reads "
            + "work — never UNAVAILABLE, which would mean the probe never ran");

        dissection.Should().Contain("TOP-LEVEL",
            "READS-HEALTHY must state that the null-polarity read was made. Leg 3 shipped reading "
            + "only the two containers that must be NON-null, and the first two readings it ever "
            + "produced (2026-09-05, 2026-09-06) were earned on that half alone — while the read "
            + "the metadata writer's guard actually makes is the TOP-LEVEL one, which must come "
            + "back NULL. A verdict that claims health without naming that read is claiming more "
            + "than it measured");
    }

    /// <summary>
    /// 🚨 <b>The branch a healthy process can never produce, and therefore the one only a pure
    /// function can cover.</b>
    ///
    /// <para><c>getConsolidatedTypeParameters</c> recurses up the containing chain calling
    /// <c>AsNestedTypeDefinition</c> (whose guard reads <c>ContainingType != null</c>) and then
    /// <c>ITypeDefinitionMember.ContainingTypeDefinition</c> (which dereferences the same
    /// <c>ContainingType</c>) back to back. At the TOP-LEVEL type the guard must answer FALSE and
    /// stop the walk. If that read answers TRUE, the property is called on a type whose
    /// <c>ContainingType</c> is null <i>correctly</i> — and throws the #890 NRE at the #890 frame,
    /// with no corrupted symbol and no pair of disagreeing reads. That path is invisible to a probe
    /// that only asks whether non-null reads come back non-null.</para>
    /// </summary>
    [Theory]
    // leaf   inner  topLevel      expected token
    [InlineData(true, true, false, "symbol:OK")]
    [InlineData(true, true, true, "symbol:NON-NULL@MwEmitCanary.ContainingType")]
    [InlineData(false, true, false, "symbol:NULL@Leaf.ContainingType")]
    [InlineData(true, false, false, "symbol:NULL@Inner.ContainingType")]
    public void TheClassification_ReadsBOTHPolarities(
        bool leaf, bool inner, bool topLevel, string expected)
        => EmitPipeline.ClassifySymbolReads(leaf, inner, topLevel).Should().Be(expected,
            "a top-level type has no containing type, so a NON-null read there is a fault of the "
            + "opposite polarity to the two above — and it is the polarity #890's own stack sits "
            + "on. Collapsing it into symbol:OK would report a broken process as healthy");

    /// <summary>
    /// The verdict the new polarity produces has to send triage somewhere different from
    /// SYMBOL-GRAPH-BROKEN: nothing about the symbol graph is wrong in that case — the graph is
    /// right and the READ of it is wrong, which is a one-property-call reproduction.
    /// </summary>
    [Fact]
    public void AGuardReadThatAnswersTrueForATopLevelType_IsItsOwnVerdict()
    {
        EmitPipeline.ClassifySymbolReads(true, true, topLevelContainer: true)
            .Should().NotBe("symbol:OK")
            .And.NotStartWith("symbol:NULL",
                "it is not a NULL read — it is a read that should have been null and was not");
    }

    /// <summary>
    /// 🚨 The regression this file exists for. The remedy must be one a CI reader can actually
    /// carry out; the dump route is dead on this defect and cannot be revived by asking louder.
    /// </summary>
    [Fact]
    public void TheBelowRoslynRemedy_IsFollowable_NotACoreDumpThatCannotBeProduced()
    {
        var verdict = EmitPipeline.Verdict(Threw(), Threw(), () => "dissect=READS-HEALTHY");

        verdict.Should().StartWith("canary=BELOW-ROSLYN");

        verdict.Should().NotContain("capture a core dump",
            "DOTNET_DbgEnableMiniDump fires on a SIGNAL and this process never signals — it is "
            + "killed by the wall-clock cap as exit=124/SIGTERM, so that instruction produced zero "
            + "dumps across nine occurrences. A remedy nobody can execute is not a remedy");

        verdict.Should().Contain("DOTNET_TieredPGO=0",
            "the half of the old advice that IS followable has to be named explicitly, or triage "
            + "is left with nothing to do");

        verdict.Should().Contain("SPLIT-ARM",
            "at ~1% per run a single clean arm proves nothing, and an experiment stated without "
            + "that caveat gets read as a fix");
    }

    /// <summary>
    /// The dissection is data, not a claim, so it travels with BOTH verdicts that mean "the control
    /// could not emit either" — including DIVERGENT, which deliberately withholds the strong claim.
    /// </summary>
    [Theory]
    [InlineData("canary=BELOW-ROSLYN", Site)]
    [InlineData("canary=DIVERGENT", "SomewhereElse.Method")]
    public void EveryVerdictThatMeansTheControlFailed_CarriesTheDissection(string expected, string pristineSite)
    {
        var verdict = EmitPipeline.Verdict(Threw(), Threw(pristineSite), () => "dissect=STUB-READING");

        verdict.Should().StartWith(expected);
        verdict.Should().Contain("dissect=STUB-READING",
            "the reading is the measurement the core dump was being asked for; a verdict that "
            + "drops it hands triage the residual and none of the evidence");
    }

    /// <summary>
    /// REFERENCES means the pristine leg EMITTED, so the symbol graph is demonstrably intact and
    /// there is nothing to dissect. Running it anyway would print a reassuring READS-HEALTHY beside
    /// a verdict that is not about the reads at all.
    /// </summary>
    [Fact]
    public void TheReferencesVerdict_DoesNotRunTheDissection()
    {
        var invocations = 0;

        var verdict = EmitPipeline.Verdict(Threw(), "OK", () => { invocations++; return "dissect=STUB"; });

        verdict.Should().StartWith("canary=REFERENCES");
        invocations.Should().Be(0,
            "the pristine leg emitted, so the reads are known good and the probe has nothing to add");
        verdict.Should().NotContain("dissect=");
    }

    /// <summary>
    /// A diagnostic that faults while diagnosing destroys the evidence it exists to preserve. Leg 3
    /// is total by construction, and the verdict is total even if a future leg 3 stops being.
    /// </summary>
    [Fact]
    public void AProbeThatFaults_DegradesToUnavailable_AndNeverEscapes()
    {
        var verdict = EmitPipeline.Verdict(Threw(), Threw(),
            () => throw new InvalidOperationException("probe blew up"));

        verdict.Should().StartWith("canary=BELOW-ROSLYN");
        verdict.Should().Contain("dissect=UNAVAILABLE(InvalidOperationException",
            "'I could not look' and 'I looked and it was broken' must never share a token");
    }

    /// <summary>
    /// The unit-test shape of <c>Verdict</c> takes no probe. An absent reading must be visible as
    /// absent rather than silently omitted — otherwise a wiring mistake that stops passing the
    /// probe looks identical to a probe that ran and found nothing.
    /// </summary>
    [Fact]
    public void NoProbeSupplied_SaysSo_RatherThanOmittingTheReading()
    {
        EmitPipeline.Verdict(Threw(), Threw())
            .Should().Contain("dissect=NOT-RUN");
    }
}
