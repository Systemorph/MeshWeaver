using System;
using System.Reactive.Subjects;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using NSubstitute;
using NSubstitute.Extensions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit contract for the stale-assembly self-heal watcher
/// (<see cref="NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal"/>) — the fix for the memex
/// 2026-08-12 deploy, where a GitSync update recompiled three NodeTypes SUCCESSFULLY and every
/// instance carried on executing the previous assembly until each hub was recycled by hand.
///
/// <para>The case is the one nobody was watching because it is the one that WORKED: an instance
/// that binds a good assembly takes no overlay, so <see cref="NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal"/>
/// — armed only on degraded branches — never applied to it, and the binding is then cached for the
/// grain's lifetime.</para>
///
/// <para>The contract pinned here:</para>
/// <list type="bullet">
///   <item>A newly published assembly path recycles the instance <b>exactly once</b>, via a
///     self-<see cref="DisposeRequest"/> to its OWN address (the RecycleLayoutArea idiom).</item>
///   <item>Republishing the SAME assembly must NOT fire — that is the unrelated-node-write case
///     (a release-request stamp, release notes, a pin edit), and firing on it would bounce every
///     instance of the type for nothing.</item>
///   <item>The gate is the assembly PATH, not the node Version: a new build at the SAME version
///     must heal (the 2026-07-27 version-gate failure, where a recompile that does not advance the
///     version left the watcher waiting for a bump that never came), and an ADVANCING version
///     carrying the same assembly must not.</item>
///   <item>Unsettled builds and non-<see cref="NodeTypeDefinition"/> content are ignored.</item>
///   <item>Disposing the watcher stops it — no post after teardown.</item>
/// </list>
/// </summary>
public class StaleAssemblySelfHealWatcherTest
{
    private const string NodeTypePath = "TestData/StaleAssemblyType";
    private const string InstancePath = "TestData/StaleAssemblyType/instance1";
    private const string BoundAssembly = "TestData_StaleAssemblyType/v10-abc-111111111111.dll";

    private static IMessageHub BuildInstanceHub()
    {
        var hub = Substitute.For<IMessageHub>();
        hub.Address.Returns(new Address(InstancePath));
        // Configure() records the Post spec WITHOUT running NSubstitute's auto-value provider —
        // IMessageDelivery carries an INTERNAL member (ChangeState), so auto-substituting Post's
        // return type throws TypeLoadException at proxy generation.
        hub.Configure()
            .Post(Arg.Any<DisposeRequest>(), Arg.Any<Func<PostOptions, PostOptions>>())
            .Returns((IMessageDelivery<DisposeRequest>?)null);
        return hub;
    }

    /// <summary>
    /// A NodeType emission. <paramref name="assemblyPath"/> null ⇒ the unsettled shape (no usable
    /// build); otherwise the exact shape a successful compile write-back produces.
    /// </summary>
    private static MeshNode TypeNode(long version, string? assemblyPath) =>
        new MeshNode("StaleAssemblyType", "TestData")
        {
            NodeType = MeshNode.NodeTypePath,
            Version = version,
            Content = new NodeTypeDefinition
            {
                CompilationStatus = assemblyPath is null
                    ? CompilationStatus.Compiling
                    : CompilationStatus.Ok,
                LatestAssemblyCollection = assemblyPath is null ? null : "assemblies",
                LatestAssemblyPath = assemblyPath,
                CompiledFrameworkVersion = assemblyPath is null
                    ? null
                    : NodeTypeCompilationHelpers.FrameworkVersion,
            }
        };

    private static void AssertNoDispose(IMessageHub hub) =>
        hub.DidNotReceive().Post(
            Arg.Any<DisposeRequest>(), Arg.Any<Func<PostOptions, PostOptions>>());

    private static void AssertDisposedExactlyOnce(IMessageHub hub) =>
        hub.Received(1).Post(
            Arg.Any<DisposeRequest>(), Arg.Any<Func<PostOptions, PostOptions>>());

    [Fact]
    public void NewlyPublishedAssembly_RecyclesTheInstance_ExactlyOnce()
    {
        var hub = BuildInstanceHub();
        var typeStream = new Subject<MeshNode>();
        using var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null);

        // The replayed at-bind state: the very assembly this instance is running. Firing here
        // would recycle every instance on every emission of its own current state.
        typeStream.OnNext(TypeNode(version: 10, assemblyPath: BoundAssembly));
        AssertNoDispose(hub);

        // A compile in flight — no build to rebind onto yet.
        typeStream.OnNext(TypeNode(version: 11, assemblyPath: null));
        AssertNoDispose(hub);

        // 🚨 THE signal: a DIFFERENT assembly is published. This is the emission that was ignored
        // before the fix, leaving the instance executing the old DLL indefinitely.
        typeStream.OnNext(TypeNode(version: 12, assemblyPath: "TestData_StaleAssemblyType/v12-abc-222222222222.dll"));
        AssertDisposedExactlyOnce(hub);

        // Take(1): further publications do not re-post. The instance is already tearing down, and
        // re-enrichment will bind the newest assembly.
        typeStream.OnNext(TypeNode(version: 13, assemblyPath: "TestData_StaleAssemblyType/v13-abc-333333333333.dll"));
        AssertDisposedExactlyOnce(hub);
    }

    /// <summary>
    /// 🚨 The anti-bounce half. A NodeType node is written for reasons that produce no new
    /// assembly — a <c>RequestedReleaseAt</c> stamp, release notes, a pin edit. Those advance the
    /// Version while republishing the SAME assembly path, and must leave every instance alone.
    /// </summary>
    [Fact]
    public void RepublishingTheSameAssembly_DoesNotRecycle_EvenAsTheVersionAdvances()
    {
        var hub = BuildInstanceHub();
        var typeStream = new Subject<MeshNode>();
        using var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null);

        for (var version = 11; version <= 20; version++)
            typeStream.OnNext(TypeNode(version, assemblyPath: BoundAssembly));

        AssertNoDispose(hub);
    }

    /// <summary>
    /// 🚨 The regression the gate choice exists for. memex 2026-07-27: the overlay watcher was
    /// gated on the node VERSION advancing, and a recompile of an already-<c>Ok</c> type need not
    /// advance it — so the watcher waited for a bump that never came and the instance stayed stuck
    /// until the pods were restarted. A new assembly at an UNCHANGED version must heal.
    /// </summary>
    [Fact]
    public void NewAssemblyAtTheSameVersion_StillHeals()
    {
        var hub = BuildInstanceHub();
        var typeStream = new Subject<MeshNode>();
        using var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null);

        typeStream.OnNext(TypeNode(version: 10, assemblyPath: "TestData_StaleAssemblyType/v10-abc-999999999999.dll"));

        AssertDisposedExactlyOnce(hub);
    }

    /// <summary>Non-definition content and unsettled builds are ignored, never fired on.</summary>
    [Fact]
    public void UnsettledAndForeignContent_AreIgnored()
    {
        var hub = BuildInstanceHub();
        var typeStream = new Subject<MeshNode>();
        using var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null);

        typeStream.OnNext(TypeNode(version: 11, assemblyPath: null));
        typeStream.OnNext(new MeshNode("StaleAssemblyType", "TestData")
        {
            NodeType = MeshNode.NodeTypePath,
            Version = 12,
            Content = "not a definition"
        });
        AssertNoDispose(hub);
    }

    /// <summary>Disposal (the hub's RegisterForDisposal hook) stops the watcher.</summary>
    [Fact]
    public void Disposing_StopsTheWatcher()
    {
        var hub = BuildInstanceHub();
        var typeStream = new Subject<MeshNode>();
        var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null);

        watcher.Dispose();
        typeStream.OnNext(TypeNode(version: 12, assemblyPath: "TestData_StaleAssemblyType/v12-abc-222222222222.dll"));

        AssertNoDispose(hub);
    }
}
