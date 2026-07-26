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
/// Unit contract for the overlay self-heal watcher
/// (<see cref="NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal"/>) — the fix for
/// the memex 2026-07-25/26 incident where an instance hub enriched while its
/// NodeType's compile was unsettled bound the compilation-error overlay (or
/// the silent default-config fallback) PERMANENTLY, even after the type
/// compiled green seconds later, until an operator posted a manual
/// DisposeRequest per instance.
///
/// <para>The contract pinned here:</para>
/// <list type="bullet">
///   <item>The watcher posts a self-<see cref="DisposeRequest"/> (targeting
///     the instance hub's OWN address — the RecycleLayoutArea idiom)
///     <b>exactly once</b>, on the first emission that is a genuinely usable
///     build (<see cref="NodeTypeCompilationHelpers.HasUsableBuild"/>) past
///     the version-at-overlay gate.</item>
///   <item>The replayed at-overlay state — including the usable-but-
///     bytes-missing shape where HasUsableBuild is ALREADY true — must NOT
///     fire instantly; only a Version ADVANCE may recycle (the anti-hot-loop
///     gate).</item>
///   <item>A null gate (slow-path timeout / probe-missing: no type node in
///     hand) fires on the FIRST usable emission, ignoring unsettled states
///     and non-NodeTypeDefinition content.</item>
///   <item>Disposing the watcher (the hub's RegisterForDisposal hook) stops
///     it — no post after teardown.</item>
/// </list>
/// </summary>
public class OverlaySelfHealWatcherTest
{
    private const string NodeTypePath = "TestData/SelfHealType";
    private const string InstancePath = "TestData/SelfHealType/instance1";

    private static IMessageHub BuildInstanceHub(out Func<Func<PostOptions, PostOptions>?> capturedOptions)
    {
        var hub = Substitute.For<IMessageHub>();
        hub.Address.Returns(new Address(InstancePath));
        Func<PostOptions, PostOptions>? captured = null;
        // Configure() records the Post spec WITHOUT running NSubstitute's
        // auto-value provider — IMessageDelivery carries an INTERNAL member
        // (ChangeState), so auto-substituting Post's return type throws
        // TypeLoadException at proxy generation. Pinning the result to null
        // covers the fire-time call too (the watcher ignores the delivery).
        hub.Configure()
            .Post(Arg.Any<DisposeRequest>(),
                Arg.Do<Func<PostOptions, PostOptions>>(f => captured = f))
            .Returns((IMessageDelivery<DisposeRequest>?)null);
        capturedOptions = () => captured;
        return hub;
    }

    /// <summary>
    /// A NodeType MeshNode emission at <paramref name="version"/>.
    /// <paramref name="usable"/> == the HasUsableBuild shape: assembly fields
    /// populated AND CompiledFrameworkVersion matching the CURRENT framework
    /// (the only state a successful compile write-back produces).
    /// </summary>
    private static MeshNode TypeNode(long version, bool usable) =>
        new MeshNode("SelfHealType", "TestData")
        {
            NodeType = MeshNode.NodeTypePath,
            Version = version,
            Content = new NodeTypeDefinition
            {
                CompilationStatus = usable ? CompilationStatus.Ok : CompilationStatus.Compiling,
                LatestAssemblyCollection = usable ? "assemblies" : null,
                LatestAssemblyPath = usable ? $"{NodeTypePath}/Release/v{version}.dll" : null,
                CompiledFrameworkVersion = usable
                    ? NodeTypeCompilationHelpers.FrameworkVersion
                    : null,
            }
        };

    private static void AssertNoDispose(IMessageHub hub) =>
        hub.DidNotReceive().Post(
            Arg.Any<DisposeRequest>(), Arg.Any<Func<PostOptions, PostOptions>>());

    private static void AssertDisposedExactlyOnce(IMessageHub hub) =>
        hub.Received(1).Post(
            Arg.Any<DisposeRequest>(), Arg.Any<Func<PostOptions, PostOptions>>());

    [Fact]
    public void VersionGated_FiresExactlyOnce_OnVersionAdvancingUsableEmission()
    {
        var hub = BuildInstanceHub(out var capturedOptions);
        var typeStream = new Subject<MeshNode>();
        using var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: 5, logger: null);

        // The replayed at-overlay state: usable but NOT past the gate. This is
        // the pinned-release-missing / bytes-missing shape where HasUsableBuild
        // is already true at overlay time — firing here would be the hot
        // recycle → re-overlay → recycle loop the gate exists to prevent.
        typeStream.OnNext(TypeNode(version: 5, usable: true));
        AssertNoDispose(hub);

        // Version advanced but the state is not usable (a compile in flight) —
        // must not recycle onto a build that isn't there yet.
        typeStream.OnNext(TypeNode(version: 6, usable: false));
        AssertNoDispose(hub);

        // Version-advancing usable emission — THE heal signal: exactly one
        // self-DisposeRequest.
        typeStream.OnNext(TypeNode(version: 7, usable: true));
        AssertDisposedExactlyOnce(hub);

        // The post targets the instance's OWN address (the RecycleLayoutArea
        // idiom). Derive expected from the SAME base options so the
        // auto-generated MessageId doesn't perturb record equality.
        var options = capturedOptions();
        options.Should().NotBeNull("the DisposeRequest must be posted with explicit options");
        var baseOptions = new PostOptions(new Address("TestData/sender"));
        options!(baseOptions).Should().Be(baseOptions.WithTarget(new Address(InstancePath)),
            "the self-heal recycle must target the instance hub's own address");

        // Take(1): a later usable emission must never fire a second recycle.
        typeStream.OnNext(TypeNode(version: 8, usable: true));
        AssertDisposedExactlyOnce(hub);
    }

    /// <summary>
    /// The SECOND sticky class (two-pod memex evidence, 2026-07-26): the
    /// bytes-missing default-config fallback arms the watcher while
    /// HasUsableBuild is ALREADY TRUE (only the byte resolution missed on this
    /// pod). The at-overlay state must NOT fire instantly — the version gate is
    /// mandatory there, not optional — and a version-advancing usable emission
    /// (any later compile write) fires exactly once.
    /// </summary>
    [Fact]
    public void UsableButBytesMissingAtOverlay_DoesNotFireInstantly_VersionAdvanceFiresOnce()
    {
        var hub = BuildInstanceHub(out _);
        var typeStream = new Subject<MeshNode>();
        using var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: 41, logger: null);

        // Replay of the exact at-overlay state: usable metadata, same version.
        typeStream.OnNext(TypeNode(version: 41, usable: true));
        typeStream.OnNext(TypeNode(version: 41, usable: true));
        AssertNoDispose(hub);

        // Any later write to the NodeType node bumps Version; with a usable
        // build that is the heal signal — at most ONE self-recycle per write.
        typeStream.OnNext(TypeNode(version: 42, usable: true));
        AssertDisposedExactlyOnce(hub);

        typeStream.OnNext(TypeNode(version: 43, usable: true));
        AssertDisposedExactlyOnce(hub);
    }

    [Fact]
    public void NullGate_FiresOnFirstUsableEmission_IgnoringUnsettledAndForeignContent()
    {
        var hub = BuildInstanceHub(out _);
        var typeStream = new Subject<MeshNode>();
        using var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: null, logger: null);

        // Non-NodeTypeDefinition content never fires, whatever the version.
        typeStream.OnNext(new MeshNode("SelfHealType", "TestData") { Version = 100 });
        AssertNoDispose(hub);

        // Unsettled compile state never fires.
        typeStream.OnNext(TypeNode(version: 101, usable: false));
        AssertNoDispose(hub);

        // First usable emission fires — with a null gate there is no version
        // floor (the timeout/probe-missing sites hold no type node, and a
        // currently-usable state would have settled instead of timing out).
        typeStream.OnNext(TypeNode(version: 102, usable: true));
        AssertDisposedExactlyOnce(hub);
    }

    [Fact]
    public void DisposedWatcher_NeverFires()
    {
        var hub = BuildInstanceHub(out _);
        var typeStream = new Subject<MeshNode>();
        var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: null, logger: null);

        // The hub's RegisterForDisposal hook: tearing the hub down disposes the
        // watcher — a late heal emission must not post to a dead address.
        watcher.Dispose();
        typeStream.OnNext(TypeNode(version: 7, usable: true));
        AssertNoDispose(hub);
    }
}
