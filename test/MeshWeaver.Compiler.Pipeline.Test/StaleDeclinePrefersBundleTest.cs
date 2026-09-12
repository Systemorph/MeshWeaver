using MeshWeaver.Compiler;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;
using static MeshWeaver.Graph.Configuration.PrebuiltAssemblySeeder;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 The rule for a bundle DECLINED on its source fingerprint, as a table
/// (<see cref="PrebuiltAssemblySeeder.DecideAfterStaleDecline"/>), pinned so the ordering cannot
/// drift back to "compile first".
///
/// <para>Maintainer directive, 2026-09-12 (relayed): <i>"it should all be tolerant"</i>. The
/// bundle-before-compile row ALSO leans on two UNATTRIBUTED, unverified statements received
/// mid-session (<i>"compile must happen on CI — at runtime you must resolve a package version,
/// not try to compile"</i>) — flagged in the PR, not claimed as the maintainer's. When the
/// build the record names does not load on this process and a version-compatible bundle for THIS
/// framework identity is in hand, the bundle is adopted — a page renders on CI-built bytes now —
/// instead of clearing the record and dispatching Roslyn. On memex 2026-09-12 the compile path is
/// what every instance of <c>Store/Plugin</c> sat behind, on the "did not settle" overlay, while
/// the CI bundle for the running identity was on the prebuilt volume the whole time.</para>
/// </summary>
public class StaleDeclinePrefersBundleTest
{
    private static NodeTypeDefinition ClaimingABuild(CompilationStatus status = CompilationStatus.Ok) => new()
    {
        CompilationStatus = status,
        LastCompiledVersion = 5,
        LatestAssemblyCollection = FileSystemAssemblyStore.FileSystemCollectionName,
        LatestAssemblyPath = "Type_Stale/v5-dead.dll",
        CompiledFrameworkVersion = "s0000000000000000000000000000000f",
    };

    private static NodeTypeDefinition ClaimingNoBuild() => new()
    {
        CompilationStatus = CompilationStatus.Ok,
    };

    /// <summary>THE CHANGE: a compatible bundle beats a local compile.</summary>
    [Fact]
    public void ACompatibleBundle_IsAdopted_BeforeALocalCompileIsDispatched()
        => Assert.Equal(StaleDeclineAction.AdoptBundle,
            DecideAfterStaleDecline(ClaimingABuild(),
                liveBuildResolvesHere: false, canCompileLocally: true, bundleAdoptable: true));

    [Fact]
    public void ABuildThatResolvesHere_IsKept_WhateverTheBundleSays()
    {
        Assert.Equal(StaleDeclineAction.KeepLiveBuild,
            DecideAfterStaleDecline(ClaimingABuild(),
                liveBuildResolvesHere: true, canCompileLocally: true, bundleAdoptable: true));
        Assert.Equal(StaleDeclineAction.KeepLiveBuild,
            DecideAfterStaleDecline(ClaimingABuild(),
                liveBuildResolvesHere: true, canCompileLocally: false, bundleAdoptable: false));
    }

    [Fact]
    public void NoCompatibleBundle_OnACompilingMesh_DispatchesTheCompile()
        => Assert.Equal(StaleDeclineAction.DispatchCompile,
            DecideAfterStaleDecline(ClaimingABuild(),
                liveBuildResolvesHere: false, canCompileLocally: true, bundleAdoptable: false));

    [Fact]
    public void NoCompatibleBundle_OnANonCompilingMesh_IsUnservable()
        => Assert.Equal(StaleDeclineAction.Unservable,
            DecideAfterStaleDecline(ClaimingABuild(),
                liveBuildResolvesHere: false, canCompileLocally: false, bundleAdoptable: false));

    [Fact]
    public void ACompatibleBundle_OnANonCompilingMesh_IsAdopted_AsBefore()
        => Assert.Equal(StaleDeclineAction.AdoptBundle,
            DecideAfterStaleDecline(ClaimingABuild(),
                liveBuildResolvesHere: false, canCompileLocally: false, bundleAdoptable: true));

    [Theory]
    [InlineData(CompilationStatus.Pending)]
    [InlineData(CompilationStatus.Compiling)]
    public void ACompileInFlight_OnACompilingMesh_IsNeverStampedOver(CompilationStatus inFlight)
        => Assert.Equal(StaleDeclineAction.LeaveRecord,
            DecideAfterStaleDecline(ClaimingABuild(inFlight),
                liveBuildResolvesHere: false, canCompileLocally: true, bundleAdoptable: true));

    [Theory]
    [InlineData(CompilationStatus.Pending)]
    [InlineData(CompilationStatus.Compiling)]
    public void ACompileInFlight_OnANonCompilingMesh_StillAdopts_AsBefore(CompilationStatus inFlight)
        => Assert.Equal(StaleDeclineAction.AdoptBundle,
            DecideAfterStaleDecline(ClaimingABuild(inFlight),
                liveBuildResolvesHere: false, canCompileLocally: false, bundleAdoptable: true));

    [Fact]
    public void ARecordClaimingNoBuild_HasNothingToClear()
        => Assert.Equal(StaleDeclineAction.LeaveRecord,
            DecideAfterStaleDecline(ClaimingNoBuild(),
                liveBuildResolvesHere: false, canCompileLocally: true, bundleAdoptable: false));
}
