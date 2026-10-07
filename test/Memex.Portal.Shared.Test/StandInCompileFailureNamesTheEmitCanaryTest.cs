using System;
using MeshWeaver.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #5212 / #890: a stand-in compile that fails must say what the shared-emit canary
/// (<see cref="EmitPipeline.ProbeSharedEmitState"/>) found at that moment, so the next red on
/// Linux CI names the process defect instead of reading as a defect of whichever test compiled
/// next.
///
/// <para>The compile here fails for an ordinary reason — a type error — in a healthy process, so
/// the canary's first leg (a trivial nested-generic emit against the SAME reference set) succeeds
/// and the verdict is <c>canary=OK</c>. That is the reading that matters on a red: <c>OK</c> says
/// "the test's own source is wrong", anything else says "this process cannot emit". Asserting the
/// <c>OK</c> verdict, not just the marker, pins that the probe actually RAN rather than a constant
/// being printed.</para>
/// </summary>
public class StandInCompileFailureNamesTheEmitCanaryTest
{
    [Fact]
    public void AFailedStandInCompile_CarriesTheCanaryVerdict_AndStillFails()
    {
        var compilation = CSharpCompilation.Create(
            "MeshWeaver.Test.StandInThatDoesNotCompile",
            [CSharpSyntaxTree.ParseText("public static class Broken { public static int X() => \"not an int\"; }")],
            PlatformReferences.Platform(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var failure = Assert.Throws<StandInCompileFailedException>(() => StandInCompile.Emit(compilation));

        // The compile error itself is still the headline — the diagnostic only adds to it.
        Assert.Contains("CS0029", failure.Message, StringComparison.Ordinal);
        // …and the canary's verdict follows, as a real reading of this (healthy) process.
        Assert.Contains($"{StandInCompile.CanaryMarker} canary=OK", failure.Message, StringComparison.Ordinal);
    }
}
