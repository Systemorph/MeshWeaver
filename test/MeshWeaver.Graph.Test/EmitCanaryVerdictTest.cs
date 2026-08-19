using MeshWeaver.Compiler;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit contract for <see cref="EmitPipeline.Verdict"/> — the one line the emit canary hands CI
/// triage when a Roslyn <c>Emit</c> THROWS instead of returning diagnostics (#890).
///
/// <para><b>The bug this pins.</b> The verdict was decided on whether each leg's token started
/// with <c>OK</c>, and nothing else. So <c>BELOW-ROSLYN</c> — *"the broken state is below Roslyn
/// (CLR heap / JIT / GC) … capture a core dump and re-run with tiering disabled"*, the most
/// expensive answer this probe can give — was claimed whenever the pristine leg ALSO threw
/// anything at all. Both legs reduced their outcome to <c>THREW {Type}: {Message}</c>, and every
/// <see cref="NullReferenceException"/> in .NET carries the identical message, so two throws from
/// completely unrelated code were indistinguishable from one process-wide fault. That is the same
/// defect the <c>INCONCLUSIVE</c> branch already exists to avoid — a probe answering its scariest
/// branch on evidence it never checked.</para>
///
/// <para>The legs run IDENTICAL source and differ only in their reference set, so "the same fault"
/// is a checkable claim once the throw SITE is recorded: same frame ⇒ BELOW-ROSLYN, different
/// frames ⇒ <c>DIVERGENT</c>, which names both sites and withholds the strong claim.</para>
/// </summary>
public class EmitCanaryVerdictTest
{
    private const string Nre = "NullReferenceException";
    private const string SameMessage = "Object reference not set to an instance of an object.";

    private static string Threw(string site, string type = Nre, string message = SameMessage)
        => $"THREW {type} at {site}: {message}";

    [Fact]
    public void PristineEmits_IsAlwaysTheReferenceSetVerdict()
    {
        var verdict = EmitPipeline.Verdict(Threw("MetadataWriter.GetConsolidatedTypeParameters"), "OK");

        verdict.Should().StartWith("canary=REFERENCES",
            "the same source emits against brand-new references and not against the shared set — "
            + "that is the whole discriminator, and it does not depend on any site");
    }

    [Fact]
    public void BothLegsDieInTheSameFrame_IsBelowRoslyn()
    {
        const string site = "MetadataWriter.GetConsolidatedTypeParameters";

        var verdict = EmitPipeline.Verdict(Threw(site), Threw(site));

        verdict.Should().StartWith("canary=BELOW-ROSLYN",
            "identical source, brand-new references, and the SAME throwing frame is what makes "
            + "'this process is broken under Roslyn' an observation rather than a guess");
        verdict.Should().Contain(site,
            "triage acts on the frame, so the verdict has to name it");
    }

    /// <summary>
    /// 🚨 The regression this file exists for. Two unrelated NREs read as one fault because their
    /// messages are byte-identical, and the probe answered BELOW-ROSLYN on that coincidence.
    /// </summary>
    [Fact]
    public void TwoDifferentFaults_AreNotOneProcessWideFault()
    {
        var verdict = EmitPipeline.Verdict(
            Threw("MetadataWriter.GetConsolidatedTypeParameters"),
            Threw("MetadataReference.CreateFromFile"));

        verdict.Should().StartWith("canary=DIVERGENT",
            "the two legs run identical source, so failing in DIFFERENT frames is evidence of two "
            + "separate faults — sending triage after a CLR heap bug here costs a core-dump hunt "
            + "for a fault that is sitting in the second site");
        verdict.Should().Contain("MetadataWriter.GetConsolidatedTypeParameters",
            "both sites are the evidence and both must be printed");
        verdict.Should().Contain("MetadataReference.CreateFromFile",
            "…including the one that is NOT the emit");
        verdict.Should().NotContain("BELOW-ROSLYN",
            "the strong claim is withheld, never softened in place");
    }

    [Fact]
    public void AnUnrecordedSite_WithholdsTheStrongClaimToo()
    {
        // A leg whose throw carried no usable frame, and a leg that failed with diagnostics rather
        // than a throw: in neither case has "the same frame" been observed.
        EmitPipeline.Verdict(Threw("MetadataWriter.GetConsolidatedTypeParameters"),
                $"THREW {Nre} at (no stack): {SameMessage}")
            .Should().StartWith("canary=DIVERGENT",
                "'(no stack)' is an absence of evidence, and absence of evidence may not be read "
                + "as a match");

        EmitPipeline.Verdict(Threw("MetadataWriter.GetConsolidatedTypeParameters"),
                "DIAGNOSTICS(CS0246)")
            .Should().StartWith("canary=DIVERGENT",
                "a pristine leg that produced compile DIAGNOSTICS did not reproduce the shared "
                + "leg's throw at all");
    }

    [Fact]
    public void ThrowSite_NamesTheThrowingMethod_AndNeverThrowsItself()
    {
        Exception captured;
        try
        {
            ThrowFromHere();
            throw new InvalidOperationException("unreachable");
        }
        catch (InvalidOperationException ex)
        {
            captured = ex;
        }

        EmitPipeline.ThrowSite(captured).Should().Be(
            $"{nameof(EmitCanaryVerdictTest)}.{nameof(ThrowFromHere)}",
            "the site is Type.Method of the frame that threw — the discriminator the verdict "
            + "compares the two legs on");

        // An exception that was never thrown has neither TargetSite nor a stack. The probe runs on
        // an already-failing path, so it must degrade rather than fault.
        EmitPipeline.ThrowSite(new InvalidOperationException("never thrown"))
            .Should().Be("(no stack)",
                "a diagnostic that throws while diagnosing destroys the evidence it exists to keep");
    }

    private static void ThrowFromHere()
        => throw new InvalidOperationException("probe");
}
