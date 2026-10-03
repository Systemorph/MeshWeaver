using System;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Updates.Test;

/// <summary>
/// 🚨 The update contract for a NODE-NATIVE NodeType (maintainer, 2026-10-03: <i>"after
/// disposerequest, new version must be loaded"</i> + <i>"old version must continue working"</i>):
/// a live hub keeps serving the build it activated on, and the next activation after a
/// <c>DisposeRequest</c> binds the build the type now publishes. Each test is one scenario of the
/// updates suite (Doc/Architecture/StaleStateUntilRecycle → "Where it is proven"), falsified against the defect it guards.
/// </summary>
public class NodeTypeUpdateTest(ITestOutputHelper output) : UpdateScenarioBase(output)
{
    /// <summary>
    /// Scenario 1 — N → N+1 through the real compile pipeline: the live hub keeps serving N after
    /// N+1 is published, and binds N+1 once a DisposeRequest has reached it.
    /// <para>Falsified by binding the activation to the first build it ever resolved (a hub that
    /// re-reads nothing after its dispose): the post-recycle read stays MARKER_N.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ANewBuild_IsBound_AfterTheDispose_AndNotBefore()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, n) = await CreateType("N");
        var instance = await CreateInstance(typePath, "inst");
        await AssertServes(instance, "N", "the instance activates on build N");

        await EditSource(typePath, Code("N1"));
        var n1 = await Publish(typePath, n);
        n1.CompilationStatus.Should().Be(CompilationStatus.Ok);

        await AssertServes(instance, "N",
            "a live hub never switches builds mid-flight — only a DisposeRequest ends its binding");

        await Recycle(instance);
        await AssertServes(instance, "N1", "the activation after the dispose binds the newest build");
    }

    /// <summary>
    /// Scenario 10 — no recycle, no change. N+1 is published AND a further unrelated write lands on
    /// the type; the live hub still serves N, because nothing but a DisposeRequest re-binds it
    /// (Modules:AutoRecycleOnStaleBuild is off in this mesh, the code default).
    /// <para>Falsified by a hub that re-reads its NodeType on every emission: it would serve N1
    /// here.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task WithoutADispose_ALiveHubNeverSwitchesBuilds()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, n) = await CreateType("N");
        var instance = await CreateInstance(typePath, "inst");
        await AssertServes(instance, "N", "the instance activates on build N");

        await EditSource(typePath, Code("N1"));
        var n1 = await Publish(typePath, n);
        await StampBuild(typePath,
            d => d with { Description = "an unrelated write after the publication" },
            d => d.Description == "an unrelated write after the publication"
                 && d.LatestAssemblyPath == n1.LatestAssemblyPath);

        await AssertServes(instance, "N", "no DisposeRequest reached the hub, so it serves N");
        await AssertServes(instance, "N", "and keeps serving N on every read");
    }

    /// <summary>
    /// Scenario 4 — N+1 FAILS to compile: the type says why on its own record, the live hub keeps
    /// serving N, and so does the activation after a dispose. Never parked, never an overlay.
    /// <para>Falsified by binding the newest RECORD rather than the newest USABLE build: the
    /// re-activation then shows the compile-error overlay instead of MARKER_N.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AFailedNewBuild_KeepsTheOldOneServing_AndSaysWhy()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, n) = await CreateType("N");
        var instance = await CreateInstance(typePath, "inst");
        await AssertServes(instance, "N", "the instance activates on build N");

        await EditSource(typePath, BrokenCode);
        var failed = await Publish(typePath, n);

        failed.CompilationStatus.Should().Be(CompilationStatus.Error, "N+1 does not compile");
        failed.CompilationError.Should().NotBeNullOrWhiteSpace(
            "the record names WHY no newer build can be loaded");
        failed.LatestAssemblyPath.Should().Be(n.LatestAssemblyPath,
            "a failed compile leaves the last good build in place");

        await AssertServes(instance, "N", "the live hub keeps serving N");
        await Recycle(instance);
        await AssertServes(instance, "N",
            "after the dispose the newest USABLE build is still N — it binds, never parks");
    }

    /// <summary>
    /// Scenario 5 — N+1 exists as source but is NOT PUBLISHED: the record says so (IsDirty), and a
    /// dispose re-binds N, the newest build there is.
    /// <para>Falsified by an activation that compiles on demand from unpublished source: it would
    /// serve N1.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnUnpublishedNewBuild_IsNamed_AndTheOldOneKeepsServing()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, _) = await CreateType("N");
        var instance = await CreateInstance(typePath, "inst");
        await AssertServes(instance, "N", "the instance activates on build N");

        await EditSource(typePath, Code("N1"));
        var dirty = await DefinitionWhere(typePath, d => d.IsDirty, "the edit is pending");
        dirty.IsDirty.Should().BeTrue("the record names that a newer source is not yet built");

        await Recycle(instance);
        await AssertServes(instance, "N", "nothing newer is published, so N is the newest build");
    }

    /// <summary>
    /// Scenario 8 + 9 — ROLLBACK and DATA: N → N+1 → pin N again. Each re-binding happens only
    /// after a dispose, and the instance's content written under N is intact under N+1 and after
    /// the rollback.
    /// <para>Falsified by resolving a pinned release through its version key alone while a newer
    /// build shares it, or by an activation that re-saves content in the build's own shape — the
    /// rollback then serves N1, or the content read back differs.</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ARollback_RebindsTheOldBuild_AndTheDataSurvivesBothWays()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, n) = await CreateType("N");
        var instance = await CreateInstance(typePath, "inst");
        await AssertServes(instance, "N", "the instance activates on build N");
        var written = (await ReadNode(instance).Should().Within(Step)
            .Emit(cancellationToken: TestContext.Current.CancellationToken))!.Content?.ToString();

        await EditSource(typePath, Code("N1"));
        await Publish(typePath, n);
        await Recycle(instance);
        await AssertServes(instance, "N1", "N+1 is bound after the dispose");
        (await ReadNode(instance).Should().Within(Step)
                .Emit(cancellationToken: TestContext.Current.CancellationToken))!.Content?.ToString()
            .Should().Be(written, "content written under N survives the N+1 activation");

        n.LatestReleasePath.Should().NotBeNullOrEmpty("build N minted a release to roll back to");
        await StampBuild(typePath,
            d => d with { RequestedReleasePath = n.LatestReleasePath },
            d => d.RequestedReleasePath == n.LatestReleasePath);
        await AssertServes(instance, "N1", "a pin is a record change; the live hub keeps N+1");
        await Recycle(instance);
        await AssertServes(instance, "N", "after the dispose the pinned build N is bound again");
        (await ReadNode(instance).Should().Within(Step)
                .Emit(cancellationToken: TestContext.Current.CancellationToken))!.Content?.ToString()
            .Should().Be(written, "content survives the rollback too");
    }
}
