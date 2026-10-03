using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Updates.Test;

/// <summary>
/// 🚨 THE REPRODUCTION (maintainer, 2026-10-03: <i>"after disposerequest, new version must be
/// loaded"</i>). A prebuilt module bundle N+1 is adopted for a type at the SAME store key a build N
/// already occupies — the shape of an adoption at the node version a compile used, of a second
/// platform generation compiling the same version, of any recompile that did not move the version.
///
/// <para><b>On main before the fix</b> the assembly store was first-write-wins per
/// <c>(nodeTypePath, version)</c>: the adoption's Put handed back N's path while the adopter stamped
/// N+1's MVID from the bytes it held. Every activation then resolved N, refused it as "not the
/// published build", rebuilt from the mesh's source — which a bundle-only update does not change,
/// so the rebuild is N again — and bound N. That is #2471's "a recycle re-binds the same local copy",
/// and no number of DisposeRequests could load N+1. The bundle's bytes here are a real compile of
/// N+1's source; the mesh's own source stays at N, so a local rebuild CANNOT produce N+1 (as on a
/// <c>Modules:RequirePrebuilt</c> mesh, where there is no local rebuild at all).</para>
///
/// <para><b>Since the fix</b> the store is content-addressed (different bytes ⇒ their own name) and
/// activation resolves the build the record NAMES (content path + MVID), so the dispose binds
/// N+1 — and the record's assembly path moves with the bytes, which is scenario 3.</para>
/// </summary>
public class BundleUpdateTest(ITestOutputHelper output) : UpdateScenarioBase(output)
{
    /// <summary>
    /// Builds the two bundles. Returns the type (source and binding at N, instance on N) and N+1's
    /// bytes, compiled by the real pipeline from N+1's source.
    /// </summary>
    private async Task<(string TypePath, NodeTypeDefinition N, string Instance, byte[] BundleN1)> Arrange()
    {
        // N+1 is compiled FIRST so its bytes are a genuine build of N+1's source …
        var (typePath, n1Build) = await CreateType("N1");
        var bundleN1 = await File.ReadAllBytesAsync(
            Path.Combine(AssemblyStoreRoot, n1Build.LatestAssemblyPath!),
            TestContext.Current.CancellationToken);
        // … then the mesh's source and binding go to N, and an instance activates on it.
        await EditSource(typePath, Code("N"));
        var n = await Publish(typePath, n1Build);
        n.CompilationStatus.Should().Be(CompilationStatus.Ok);
        var instance = await CreateInstance(typePath, "inst");
        await AssertServes(instance, "N", "the instance activates on build N");
        return (typePath, n, instance, bundleN1);
    }

    /// <summary>Adopts <paramref name="bundle"/> at N's own store key, stamping exactly the
    /// fields PrebuiltAssemblySeeder stamps.</summary>
    private async Task<NodeTypeDefinition> AdoptAtTheSameKey(string typePath, NodeTypeDefinition n, byte[] bundle)
    {
        var key = n.LastCompiledVersion!.Value;
        var location = await Store.PutWithLocation(typePath, key, bundle, null)
            .Take(1).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        var mvid = ServedBuildIdentity.OfBytes(bundle);
        return await StampBuild(typePath,
            d => d with
            {
                CompilationStatus = CompilationStatus.Ok,
                LastCompiledVersion = key,
                LatestAssemblyCollection = location!.Collection,
                LatestAssemblyPath = location.ContentPath,
                LatestAssemblyMvid = mvid,
                BuildProvenance = BuildProvenance.AdoptedVerified,
            },
            d => d.LatestAssemblyMvid == mvid);
    }

    /// <summary>
    /// Scenario 2 — bundle N → N+1: the live hub keeps N; after the dispose it binds N+1.
    /// <para>Falsified on main before the fix (first-write-wins store + version-keyed lookup): the
    /// post-recycle read is MARKER_N.</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ABundleAdoptedAtTheSameKey_IsBound_AfterTheDispose()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, n, instance, bundleN1) = await Arrange();

        await AdoptAtTheSameKey(typePath, n, bundleN1);
        await AssertServes(instance, "N", "a live hub keeps its build until a DisposeRequest");

        await Recycle(instance);
        await AssertServes(instance, "N1",
            "after the dispose the activation binds the bundle the record names — never the older "
            + "copy that shares its store key");
    }

    /// <summary>
    /// Scenario 8, at a SHARED key — roll back from the bundle N+1 to the release of build N, which
    /// sits under the very same store key. The record names N's content path; the newest file under
    /// the key is N+1's.
    /// <para>Falsified by resolving through the version key alone (the pre-fix
    /// <c>TryGetAssemblyPath</c> on the activation path): the rollback serves N1.</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ARollbackToABuildSharingItsKey_BindsTheBuildTheRecordNames()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, n, instance, bundleN1) = await Arrange();
        await AdoptAtTheSameKey(typePath, n, bundleN1);
        await Recycle(instance);
        await AssertServes(instance, "N1", "the bundle N+1 is bound after the dispose");

        n.LatestReleasePath.Should().NotBeNullOrEmpty("build N minted a release to roll back to");
        await StampBuild(typePath,
            d => d with { RequestedReleasePath = n.LatestReleasePath },
            d => d.RequestedReleasePath == n.LatestReleasePath);
        await Recycle(instance);
        await AssertServes(instance, "N",
            "the pinned release names N's bytes; a newer file under the same key must not answer for it");
    }

    /// <summary>
    /// Scenario 3 — a same-key BYTE change counts as new: the record's assembly path moves with the
    /// bytes (content hash), so every path-keyed reader — the stale-build watcher included — sees a
    /// new build, and N's bytes stay untouched for the hub still holding them.
    /// <para>Falsified on main before the fix: the path stays N's (first-write-wins returned it).</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ASameKeyByteChange_MovesThePath_AndLeavesTheOldBytesInPlace()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, n, instance, bundleN1) = await Arrange();
        var nFile = Path.Combine(AssemblyStoreRoot, n.LatestAssemblyPath!);
        var nBytes = await File.ReadAllBytesAsync(nFile, TestContext.Current.CancellationToken);

        var adopted = await AdoptAtTheSameKey(typePath, n, bundleN1);

        adopted.LastCompiledVersion.Should().Be(n.LastCompiledVersion, "the same store key");
        adopted.LatestAssemblyPath.Should().NotBe(n.LatestAssemblyPath,
            "different bytes are a different build, so they carry a different content-hashed path");
        ServedBuildIdentity.OfFile(Path.Combine(AssemblyStoreRoot, adopted.LatestAssemblyPath!))
            .Should().Be(adopted.LatestAssemblyMvid, "the path names exactly the bytes the record published");
        (await File.ReadAllBytesAsync(nFile, TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo(nBytes, System.Text.Json.JsonSerializerOptions.Default);
        await AssertServes(instance, "N", "old version keeps working: the hub holding N still serves it");
    }
}

/// <summary>
/// Scenario 5 — a newer build for ANOTHER framework identity: it cannot be loaded here, the reason
/// is named, and the old build keeps serving — before and after a dispose. Never parked.
/// <para>Falsified by an activation that binds whatever the record names without the identity
/// check (<c>NodeTypeBuildIdentity.Refuses</c>): the foreign bytes are bound and, on a real
/// identity mismatch, fail to load — an overlay instead of MARKER_N.</para>
/// </summary>
public class ForeignIdentityUpdateTest(ITestOutputHelper output) : UpdateScenarioBase(output)
{
    private const string ForeignFramework = "s0000000000000000000000000000000f";

    [Fact(Timeout = 240_000)]
    public async Task ANewerBuildForAnotherFramework_IsNamed_AndTheOldOneKeepsServing()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, n1Build) = await CreateType("N1");
        var bundleN1 = await System.IO.File.ReadAllBytesAsync(
            System.IO.Path.Combine(AssemblyStoreRoot, n1Build.LatestAssemblyPath!),
            TestContext.Current.CancellationToken);
        await EditSource(typePath, Code("N"));
        var n = await Publish(typePath, n1Build);
        var instance = await CreateInstance(typePath, "inst");
        await AssertServes(instance, "N", "the instance activates on build N");

        var key = n.LastCompiledVersion!.Value + 1000;
        var location = await Store.PutWithLocation(typePath, key, bundleN1, null)
            .Take(1).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        var foreign = await StampBuild(typePath,
            d => d with
            {
                LastCompiledVersion = key,
                LatestAssemblyCollection = location!.Collection,
                LatestAssemblyPath = location.ContentPath,
                LatestAssemblyMvid = ServedBuildIdentity.OfBytes(bundleN1),
                CompiledFrameworkVersion = ForeignFramework,
            },
            d => d.CompiledFrameworkVersion == ForeignFramework);
        NodeTypeBuildIdentity.RefusalReason(foreign).Should().NotBeNull(
            "the record names WHY the newer build cannot be loaded here");
        NodeTypeBuildIdentity.RefusalReason(foreign)!.Should().Contain(ForeignFramework[..8],
            "and names the identity it was built for");

        await AssertServes(instance, "N", "the live hub keeps serving N");
        await Recycle(instance);
        await AssertServes(instance, "N",
            "after the dispose the newest LOADABLE build is N — bound, never parked, never an overlay");
        var after = await DefinitionWhere(typePath,
            d => d.CompilationStatus == CompilationStatus.Ok
                 && d.CompiledFrameworkVersion == FrameworkBuildIdentity.FrameworkVersion,
            "the type converges on a build for THIS framework identity");
        NodeTypeBuildIdentity.RefusalReason(after).Should().BeNull();
    }
}
