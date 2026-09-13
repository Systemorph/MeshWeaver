// <meshweaver>
// Id: Testing/CompilerPipeline/EmitCanaryPrivateCompilerLegTest
// DisplayName: Testing/CompilerPipeline/EmitCanaryPrivateCompilerLegTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using MeshWeaver.Compiler;

/// <summary>
/// Leg 5 of the #890 canary — the pristine-COMPILER control (<see cref="PrivateRoslynCopy"/>).
///
/// <para>Legs 1–4 all execute the one shared <c>Microsoft.CodeAnalysis*</c>, so none of them can
/// tell "the process cannot emit" from "the shared copy of Roslyn cannot emit"; every occurrence's
/// <c>BELOW-ROSLYN</c> has therefore read "not the references" as "not Roslyn". This leg loads a
/// second Roslyn from fresh bytes into a collectible context and emits the canary through it.
/// On a healthy process the only acceptable reading is <c>PRIVATE-COPY-EMITS</c> — and a control
/// that cannot run at all (<c>UNAVAILABLE</c>) would retire the discriminator silently, turning
/// every future occurrence into a verdict with nothing behind it. That is what this pins.</para>
/// </summary>
public class EmitCanaryPrivateCompilerLegTest(MeshTestContext context)
{
    [MeshFact]
    public void OnAHealthyProcess_ThePrivateCopyEmits_AndIsNotTheSharedAssembly()
    {
        var verdict = PrivateRoslynCopy.Emit(
            "public class MwEmitCanary<T> { public class Inner<U> { public class Leaf<V> "
            + "{ public T A; public U B; public V C; } } }");
        output.WriteLine(verdict);

        verdict.Should().StartWith(PrivateRoslynCopy.Prefix + "PRIVATE-COPY-EMITS",
            "a healthy process must be able to emit through a fresh copy of Roslyn — any other "
            + "reading here means the control cannot run, and a control that cannot run answers "
            + "nothing on the occurrence it exists for");

        // The context is collectible and is unloaded after the emit; a second run must load a
        // fresh copy again rather than fail on a context that was left behind.
        PrivateRoslynCopy.Emit("public class Twice { }")
            .Should().StartWith(PrivateRoslynCopy.Prefix + "PRIVATE-COPY-EMITS",
                "the leg is re-runnable — one occurrence per compile, and there can be many");
    }

    [MeshFact]
    public void TheVerdict_CarriesTheCompilerLeg_OnBothControlFailedShapes()
    {
        // The two verdict shapes leg 5 rides on — BELOW-ROSLYN (both legs died in one frame) and
        // DIVERGENT (they died in different frames) — must both render the compiler= reading, and
        // a verdict rendered without a probe must SAY so rather than omit it.
        var sameFrame = EmitPipeline.Verdict(
            "THREW NullReferenceException at MetadataWriter.GetConsolidatedTypeParameters: x",
            "THREW NullReferenceException at MetadataWriter.GetConsolidatedTypeParameters: x",
            compiler: () => PrivateRoslynCopy.Prefix + "PRIVATE-COPY-EMITS — test stub");
        sameFrame.Should().Contain("canary=BELOW-ROSLYN").And.Contain("compiler=PRIVATE-COPY-EMITS");

        var divergent = EmitPipeline.Verdict(
            "THREW NullReferenceException at MetadataWriter.GetConsolidatedTypeParameters: x",
            "THREW InvalidOperationException at SomethingElse.Entirely: y",
            compiler: () => PrivateRoslynCopy.Prefix + "PRIVATE-COPY-THREW NullReferenceException at MetadataWriter.GetConsolidatedTypeParameters: x");
        divergent.Should().Contain("canary=DIVERGENT").And.Contain("compiler=PRIVATE-COPY-THREW");

        EmitPipeline.Verdict(
                "THREW NullReferenceException at MetadataWriter.GetConsolidatedTypeParameters: x",
                "THREW NullReferenceException at MetadataWriter.GetConsolidatedTypeParameters: x")
            .Should().Contain("compiler=NOT-RUN",
                "a verdict rendered without the probe names the absence — silence would read as a clean leg");

        EmitPipeline.Verdict(
                "THREW NullReferenceException at MetadataWriter.GetConsolidatedTypeParameters: x",
                "THREW NullReferenceException at MetadataWriter.GetConsolidatedTypeParameters: x",
                compiler: () => throw new InvalidOperationException("the probe itself is broken"))
            .Should().Contain("compiler=UNAVAILABLE",
                "a probe that faults must say it said nothing, never become a verdict");
    }
}
