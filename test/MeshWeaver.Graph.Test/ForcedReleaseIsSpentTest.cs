using System;
using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A force is SPENT by the compile it dispatches (#2818).
///
/// <para>The compile watcher reads <see cref="NodeTypeDefinition.RequestedReleaseForce"/> off the
/// <c>Pending</c> node to skip on-demand prebuilt adoption — a forced release means "build the
/// live source", not "serve me whatever a bundle still resolves". The flag therefore has to
/// survive the dispatch commit (the release watcher keeps it) and die with the compile's terminal
/// stamp: left standing, the NEXT, unforced trigger on the same type would skip adoption too, and
/// on a <c>Modules:RequirePrebuilt</c> mesh that is a park for a type whose bundle exists. All
/// three terminal writers are pure functions, so the contract is pinned here without a mesh.</para>
/// </summary>
public class ForcedReleaseIsSpentTest
{
    private static NodeTypeDefinition Forced() => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Pending,
        RequestedReleaseAt = DateTimeOffset.UtcNow,
        RequestedReleaseForce = true,
        RequestedReleaseBy = "operator",
        CurrentSourceVersions = ImmutableDictionary<string, long>.Empty.Add("P/T/Source/Model", 42),
    };

    [Fact]
    public void ASuccessfulCompile_SpendsTheForce()
    {
        var compiled = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            Forced(),
            new NodeCompilationResult(
                AssemblyLocation: "/cache/T/T.dll",
                NodeTypeConfigurations: [],
                CompiledSources: ImmutableDictionary<string, long>.Empty.Add("P/T/Source/Model", 42),
                Collection: "assemblies",
                ContentPath: "P_T/v13.dll",
                Version: 13),
            currentNodeVersion: 13, activityPath: null, releasePath: null);

        compiled.CompilationStatus.Should().Be(CompilationStatus.Ok);
        compiled.RequestedReleaseForce.Should().BeFalse(
            "the force dispatched THIS compile and is done; a standing flag would make the next, "
            + "unforced trigger skip prebuilt adoption too");
        compiled.RequestedReleaseBy.Should().BeNull("the requester is consumed alongside the force");
    }

    [Fact]
    public void AFailedCompile_SpendsTheForce()
    {
        var failed = NodeTypeCompilationHelpers.ApplyCompileFailure(
            Forced(), result: null, error: new InvalidOperationException("boom"), activityPath: null);

        failed.CompilationStatus.Should().Be(CompilationStatus.Error);
        failed.RequestedReleaseForce.Should().BeFalse(
            "a forced compile that failed is still a compile that RAN; the force does not roll "
            + "over into the retry a fresh request will ask for");
    }

    [Fact]
    public void AParkedForce_IsSpentToo()
    {
        // Under Modules:RequirePrebuilt a forced compile is refused by design and the type parks
        // with that reason. The bundle that arrives later must be adoptable — a stale force would
        // refuse it and leave the type parked for good.
        var parked = NodeTypeCompilationHelpers.ApplyGateSettle(
            Forced(), reason: "Modules:RequirePrebuilt: no compiler on this mesh",
            formedUnderLiveInputs: true, modulesHash: "m1");

        parked.CompilationStatus.Should().Be(CompilationStatus.Error);
        parked.CompilationError.Should().Contain("RequirePrebuilt");
        parked.RequestedReleaseForce.Should().BeFalse();
    }
}
