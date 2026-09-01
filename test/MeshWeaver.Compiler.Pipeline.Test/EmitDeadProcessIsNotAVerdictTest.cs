using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A compile that aborted because the PROCESS can no longer emit has formed no verdict about
/// the code, and must not record one (#890).
///
/// <para><b>What actually happens.</b> Roslyn's <c>Emit</c> throws
/// <see cref="NullReferenceException"/> from <c>Cci.MetadataWriter</c>, and from that instant the
/// process cannot emit ANY assembly. Measured on run <c>33322993649</c> shard 1: the first throw
/// landed at 16:42:42.375, and over the remaining 6 m 15 s <b>7 of 7</b> compiles that reached the
/// metadata writer failed identically while <b>none</b> succeeded — yet every compile that needed
/// only DIAGNOSTICS still returned correct <c>CS####</c> codes, so parse and bind were healthy and
/// only the emit was dead. The canary
/// (<see cref="EmitPipeline.ProbeSharedEmitState"/>) says so at the FIRST throw: its control
/// compilation is trivial, freshly parsed and known-good, and it could not emit either.</para>
///
/// <para><b>What was recorded instead.</b> <see cref="NodeTypeCompilationHelpers.ApplyCompileFailure"/>
/// stamped <see cref="CompilationStatus.Error"/> — "Roslyn's verdict" — plus
/// <see cref="NodeTypeDefinition.FailedBuildInputs"/>, the durable claim *"this failure was formed
/// under these compile inputs"*. <see cref="NodeTypeCompilationHelpers.HasStaleFailureVerdict"/>
/// then reads that claim back, finds it equal to the live inputs, and the automatic re-drive
/// declines — for a fault the framework, the modules and the sources had nothing to do with, and
/// which a healthy process would not reproduce. The type is left saying *"your code is broken"*
/// about code nothing ever evaluated, and nothing retries it until a human presses Compile or an
/// input genuinely moves.</para>
///
/// <para><b>The other half — the park budget — was already right</b> and that is exactly why this
/// mattered: <c>RunCompile</c>'s terminal handler classifies a non-<c>CompilationException</c>
/// abort as non-deterministic, so the two consumers of one classification disagreed. This file
/// pins them back together on the predicate the file itself insists there be only one of.</para>
///
/// <para>🚨 <b>The non-vacuity half is the point.</b> Only two of the canary's five verdicts prove
/// the process is at fault. A predicate keyed on "an emit-phase throw carries a canary" would be
/// true for all five and would hand the bake gate the blind spot
/// <c>SourceSnapshotEstablishmentTest.EveryOtherCompileFailure_StillStampsError</c> exists to
/// refuse. Every withholding verdict is pinned below.</para>
/// </summary>
public class EmitDeadProcessIsNotAVerdictTest
{
    private const string Site = "NamedTypeSymbol.Microsoft.Cci.ITypeDefinitionMember.get_ContainingTypeDefinition";

    /// <summary>The real thing, built the way the pipeline builds it: the ORIGINAL exception with
    /// the canary verdict stamped on <see cref="Exception.Data"/> — never a wrapper, because the
    /// exception TYPE is what CI triage keys on.</summary>
    private static Exception EmitThrewWith(string verdict)
    {
        var error = new NullReferenceException("Object reference not set to an instance of an object.");
        error.Data[EmitPipeline.EmitCanaryDataKey] = verdict;
        return error;
    }

    private static string Threw(string site = Site)
        => $"THREW NullReferenceException at {site}: Object reference not set to an instance of an object.";

    // The verdicts as EmitPipeline.Verdict actually produces them — derived, never hand-typed, so
    // a change to the verdict wording cannot leave this file asserting against strings that no
    // longer exist.
    private static string BelowRoslyn => EmitPipeline.Verdict(Threw(), Threw());
    private static string References => EmitPipeline.Verdict(Threw(), "OK");
    private static string Divergent => EmitPipeline.Verdict(Threw(), Threw("SomethingElse.Unrelated"));

    /// <summary>
    /// The defect, at the durable stamp. A process that cannot emit is an availability fact, so
    /// the status is <see cref="CompilationStatus.Unavailable"/> — "the compile state could not be
    /// determined; nothing is known to be wrong with the source" — exactly as an unestablished
    /// source set already is.
    /// </summary>
    [Fact]
    public void AnEmitTheProcessCouldNotDo_IsNotAVerdictAboutTheCode()
    {
        NodeTypeCompilationHelpers.ApplyCompileFailure(
                new NodeTypeDefinition(),
                result: null,
                error: EmitThrewWith(BelowRoslyn),
                activityPath: null)
            .CompilationStatus.Should().Be(CompilationStatus.Unavailable,
                "the canary's control compilation — trivial, freshly parsed, known-good — could "
                + "not emit either, so this compile learned nothing about the code it was handed; "
                + "recording Error states a verdict that was never formed");
    }

    /// <summary>
    /// …and the durable consequence, which is the one that actually costs: an <c>Error</c> stamped
    /// with <see cref="NodeTypeDefinition.FailedBuildInputs"/> equal to the live inputs is the
    /// state in which the automatic re-drive declines. <see cref="CompilationStatus.Unavailable"/>
    /// is stale on its own, so a later, healthy process re-drives the type instead of leaving it
    /// bricked on a fault its inputs never caused.
    /// </summary>
    [Fact]
    public void TheTypeStaysReDrivable_SoAHealthyProcessRecoversIt()
    {
        var sources = ImmutableDictionary.CreateRange(
            new Dictionary<string, long> { ["Acme/Type/Source/code"] = 3 });
        var def = new NodeTypeDefinition { CurrentSourceVersions = sources };

        var afterEmitDeath = NodeTypeCompilationHelpers.ApplyCompileFailure(
            def, result: null, error: EmitThrewWith(BelowRoslyn),
            activityPath: null, modulesHash: "mod-1");

        NodeTypeCompilationHelpers.HasStaleFailureVerdict(afterEmitDeath, "mod-1")
            .Should().BeTrue(
                "nothing about the framework, the modules or the sources caused this, so the "
                + "verdict-inputs token cannot express what has to change for a retry to be worth "
                + "making — a fresh process is what changes, and Unavailable is stale on its own");

        // The control: a REAL compile error, formed under exactly these inputs, still stops the
        // re-drive. Without this, "always re-drive" would pass the assertion above and turn a
        // permanently-broken type into an unbounded recompile.
        var afterRealCompileError = NodeTypeCompilationHelpers.ApplyCompileFailure(
            def, result: null,
            error: new CompilationException("Acme/Type", "CS0246: The type or namespace name 'X' could not be found"),
            activityPath: null, modulesHash: "mod-1");

        afterRealCompileError.CompilationStatus.Should().Be(CompilationStatus.Error);
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(afterRealCompileError, "mod-1")
            .Should().BeFalse(
                "a genuine Roslyn diagnostic IS a verdict about the code, formed under these very "
                + "inputs — re-running it would change nothing and the park exists to stop exactly "
                + "that");
    }

    /// <summary>
    /// 🚨 Non-vacuity, verdict by verdict. Three of the canary's five outcomes deliberately
    /// WITHHOLD the claim that the process is the broken thing, and each one is a case where
    /// treating the abort as a non-verdict would hide a real fault:
    /// <list type="bullet">
    ///   <item><c>OK</c> — the control emitted fine against the SAME references, so the fault IS a
    ///     property of this compilation's inputs. That is a genuine <c>Error</c>.</item>
    ///   <item><c>INCONCLUSIVE</c> — the control could not be BUILT, so leg 2 never ran. Reading
    ///     "I could not run" as "the process is dead" is the defect that branch exists to avoid.</item>
    ///   <item><c>DIVERGENT</c> — both legs failed, in DIFFERENT frames, which the verdict already
    ///     refuses to call one process-wide fault.</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("OK")]
    [InlineData("INCONCLUSIVE")]
    [InlineData("DIVERGENT")]
    public void EveryWithholdingVerdict_StillStampsError(string which)
    {
        var verdict = which switch
        {
            "OK" => EmitPipeline.ProbeSharedEmitState(
                Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                    "probe", references: CompileReferences.Default)),
            "INCONCLUSIVE" => "canary=INCONCLUSIVE shared:THREW … pristine:UNAVAILABLE(no on-disk "
                + "System.Private.CoreLib to reference) — the shared reference set cannot emit, but "
                + "the pristine control could not be BUILT",
            _ => Divergent,
        };

        // The OK leg is produced by the real probe against the real reference set, so this also
        // proves the canary can still emit in a HEALTHY process — a control that had quietly
        // stopped compiling would make every assertion here vacuous.
        if (which == "OK")
            verdict.Should().StartWith("canary=OK");
        else if (which == "DIVERGENT")
            verdict.Should().StartWith("canary=DIVERGENT");

        EmitPipeline.IsProcessEmitFailure(verdict).Should().BeFalse();

        NodeTypeCompilationHelpers.ApplyCompileFailure(
                new NodeTypeDefinition(), result: null,
                error: EmitThrewWith(verdict), activityPath: null)
            .CompilationStatus.Should().Be(CompilationStatus.Error,
                $"'{which}' does not show the process to be at fault, and filing an unproven "
                + "process fault as 'not evaluated' is the blind spot that would let a real code "
                + "regression through the bake gate");
    }

    /// <summary>
    /// The two verdicts that DO prove it, plus the shapes a value read out of an untyped
    /// <see cref="Exception.Data"/> can really take. The predicate is total: it never throws and
    /// answers false for anything it does not recognise.
    /// </summary>
    [Fact]
    public void TheClassifierReadsTheVerdict_NotThePresenceOfACanary()
    {
        EmitPipeline.IsProcessEmitFailure(BelowRoslyn).Should().BeTrue();
        EmitPipeline.IsProcessEmitFailure(References).Should().BeTrue(
            "a control that emits only against BRAND-NEW references still means this process "
            + "cannot compile what it is being asked to compile");

        EmitPipeline.IsProcessEmitFailure(null).Should().BeFalse();
        EmitPipeline.IsProcessEmitFailure(42).Should().BeFalse(
            "Exception.Data is untyped, so a non-string value must degrade to 'not proven'");
        EmitPipeline.IsProcessEmitFailure("canary=BELOW-ROSLYN").Should().BeTrue();
        EmitPipeline.IsProcessEmitFailure("BELOW-ROSLYN mentioned in prose").Should().BeFalse(
            "the verdict is a prefix, not a substring — an error message that happens to quote a "
            + "previous verdict must not become one");

        // An exception with no canary at all — every infrastructure fault that never reached
        // Roslyn's emit — is untouched.
        NodeTypeCompilationHelpers.IsAvailabilityNonVerdict(
                new InvalidOperationException("something else went wrong"))
            .Should().BeFalse();
    }
}
