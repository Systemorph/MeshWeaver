using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using MeshWeaver.Compiler;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The emit canary's leg 2 is a CONTROL, and a control is only a control for what it does not
/// share with the thing it is exonerating (#890).
///
/// <para><b>The bug this pins.</b> Leg 2 built its reference set with
/// <c>MetadataReference.CreateFromFile(typeof(object).Assembly.Location)</c> and the verdict then
/// claimed the shared reference set was excluded — *"nothing about the reference set explains
/// this; the broken state is below Roslyn (CLR heap / JIT / GC) … capture a core dump"*. But
/// <see cref="CompileReferences.Default"/> maps that SAME file, twice over (from
/// <c>TRUSTED_PLATFORM_ASSEMBLIES</c> and again as an explicit <c>typeof(object).Assembly</c>
/// addition). Different <c>PortableExecutableReference</c> instances, same on-disk image, same
/// mmap, same page-cache pages. Since every compilation must reference CoreLib, the overlap was
/// unavoidable by construction: the control shared with the suspect precisely the one input no
/// compile can omit, so a fault in those mapped metadata pages killed both legs in the same frame
/// and was reported as the most expensive verdict the probe can give.</para>
///
/// <para>Nine CI occurrences between 2026-08-23 and 2026-08-28 returned <c>BELOW-ROSLYN</c>
/// unanimously, and that verdict steered triage away from the reference set on a distinction the
/// probe had never actually drawn.</para>
/// </summary>
public class EmitCanaryControlTest
{
    /// <summary>
    /// 🚨 The regression this file exists for. Stated as the two halves that must BOTH hold:
    /// the shared set really does map CoreLib from disk (so the old overlap was real, not
    /// hypothetical), and the control really is not file-backed (so the overlap is now gone).
    /// Asserting only the second half would pass just as well if <see cref="CompileReferences"/>
    /// had stopped mapping CoreLib, which would make this test vacuous.
    /// </summary>
    [Fact]
    public void TheControl_SharesNoFileMapping_WithTheSharedReferenceSet()
    {
        var coreLib = typeof(object).Assembly.Location;
        coreLib.Should().NotBeNullOrEmpty("the premise of the whole test is an on-disk CoreLib");

        var sharedFilePaths = CompileReferences.Default
            .OfType<PortableExecutableReference>()
            .Select(r => r.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        sharedFilePaths.Should().Contain(coreLib,
            "the shared set maps CoreLib from disk — that is WHY a file-backed control was not a "
            + "control, and if this ever stops holding the rest of this test proves nothing");

        var control = EmitCanary_Control(out var unavailable);
        unavailable.Should().BeNull("the control must be buildable on any host with an on-disk CoreLib");

        control.Should().ContainSingle("the control is deliberately minimal — CoreLib and nothing else");
        control.Single().Should().BeAssignableTo<PortableExecutableReference>();
        var only = (PortableExecutableReference)control.Single();

        only.FilePath.Should().BeNull(
            "an image-backed reference carries no file path — that IS the property that makes it a "
            + "control: no mmap and no page-cache pages shared with the set under suspicion");

        sharedFilePaths.Should().NotContain(only.FilePath ?? "\0",
            "and therefore it cannot appear in the shared set's mapped files");
    }

    /// <summary>
    /// A control that cannot compile is not a control — it would turn every occurrence into a
    /// permanent INCONCLUSIVE and silently retire the discriminator. Pins that CoreLib alone is
    /// still sufficient for the canary source AFTER the switch to image-backed bytes.
    /// </summary>
    [Fact]
    public void TheControl_CanStillEmitTheCanarySource()
    {
        var control = EmitCanary_Control(out var unavailable);
        unavailable.Should().BeNull();

        var outcome = EmitPipeline.EmitCanaryForTest(control);

        outcome.Should().StartWith("OK",
            "on a healthy process the control MUST emit — if CoreLib alone no longer satisfies the "
            + "canary source, every future occurrence reports INCONCLUSIVE and #890's "
            + "discriminator is gone without anything going red");
    }

    private static IReadOnlyList<MetadataReference> EmitCanary_Control(out string? unavailable)
        => EmitPipeline.TryBuildPristineControl(out unavailable);
}
