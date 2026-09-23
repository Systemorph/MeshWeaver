using System;
using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Issue #1105: the adopted source-stamp watcher counts a request as COMMITTED only when a
/// confirmed write consumed it, never merely because a pass ran.
///
/// <para><b>The defect.</b> <c>InstallAdoptedSourceStampWatcher</c> advanced its high-water mark
/// INSIDE the write's lambda, before the stamp was applied. Any later emission still carrying that
/// request with <c>IsDirty=true</c> was then logged at Error: "the adopted build's source stamp was
/// committed … the write did not converge". Two ordinary outcomes leave the request standing after
/// the lambda ran:</para>
/// <list type="bullet">
///   <item>the write faults after the lambda ran;</item>
///   <item><see cref="NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp"/> DEFERS the judgement.
///     It returns the definition untouched, request standing, while the adoption cannot be judged
///     because the live source set is still short of the paths the bundle was built from
///     (#4280).</item>
/// </list>
/// <para>In both cases the next emission is exactly the one that should fulfil the request, and
/// the mark reported it as a failure instead. Measured on memex.meshweaver.cloud: `Store/Order`
/// logged the Error at 2026-09-22 22:54:24Z, and 74 s later read `requestedSourceStampAt` absent,
/// `currentSourceFingerprint == adoptedSourceFingerprint`, `buildProvenance: AdoptedVerified`.
/// The request had converged; only the log line said otherwise.</para>
///
/// <para><b>Negative control.</b> Make <see cref="NodeTypeCompilationHelpers.AdoptedSourceStampLedger.RecordCommit"/>
/// advance unconditionally, which is the "a pass ran" meaning the lambda placement had.
/// <see cref="ADeferredJudgement_IsNotACommit_SoTheNextEmissionStampsInsteadOfReportingNonConvergence"/>
/// then fails.</para>
/// </summary>
public class AdoptedSourceStampCommitIsConfirmedTest
{
    private static readonly DateTimeOffset Requested = new(2026, 9, 22, 17, 35, 29, TimeSpan.Zero);

    private static ImmutableDictionary<string, long> Live(params string[] paths)
    {
        var snap = ImmutableDictionary<string, long>.Empty;
        foreach (var path in paths)
            snap = snap.SetItem(path, 1);
        return snap;
    }

    /// <summary>An adoption whose bundle was built from two sources and whose live fingerprint
    /// differs, with only ONE of the two sources landed yet: the #4280 "still installing" state.</summary>
    private static NodeTypeDefinition AdoptedWhileSourcesAreLanding(ImmutableDictionary<string, long> live) => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Ok,
        LastCompileSucceededAt = DateTimeOffset.UtcNow,
        LatestAssemblyCollection = "assemblies",
        LatestAssemblyPath = "Store_Order/v2752-abc.dll",
        CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
        RequestedSourceStampAt = Requested,
        CompiledSources = null,
        AdoptedSourceFingerprint = "b06c6eeee4daab3d",
        AdoptedSourcePaths = ["Store/Order/Source/OrderContent", "Store/Order/Source/OrderCheckout"],
        CurrentSourceFingerprint = "0000000000000000",
        CurrentSourceVersions = live,
    };

    private static MeshNode NodeOf(NodeTypeDefinition def) =>
        MeshNode.FromPath("Store/Order") with { NodeType = MeshNode.NodeTypePath, Content = def };

    private static NodeTypeDefinition Pass(NodeTypeDefinition def) =>
        (NodeTypeDefinition)NodeTypeCompilationHelpers.StampOnce(
                NodeOf(def),
                (d, live) => NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(d, live, canCompileLocally: true),
                () => false)
            .Content!;

    [Fact]
    public void ADeferredJudgement_IsNotACommit_SoTheNextEmissionStampsInsteadOfReportingNonConvergence()
    {
        var ledger = new NodeTypeCompilationHelpers.AdoptedSourceStampLedger();
        var landing = AdoptedWhileSourcesAreLanding(Live("Store/Order/Source/OrderContent"));

        // The production pass, run the way the write runs it. The stamp DEFERS: the live set is
        // short of the bundle's paths, so the definition comes back with the request standing.
        var written = Pass(landing);
        written.RequestedSourceStampAt.Should().Be(Requested,
            "the precondition of this case: the stamp deferred its judgement and left the request open");
        written.IsDirty.Should().BeTrue("and the node still reads dirty, exactly as the next emission will");

        // The write is confirmed with that definition.
        ledger.RecordCommit(written, Requested).Should().BeFalse(
            "a write that left the request standing did not consume it");

        // The next emission carries the SAME request, still dirty. The watcher's first question is
        // whether this request was already committed. Answering yes is the Error line on #1105.
        ledger.IsCommitted(Requested).Should().BeFalse(
            "the request was never consumed, so the next emission must STAMP it, not report that a "
            + "committed write 'did not converge'");
    }

    [Fact]
    public void AnUnreadableConfirmation_IsNotACommit()
    {
        var ledger = new NodeTypeCompilationHelpers.AdoptedSourceStampLedger();
        ledger.RecordCommit(null, Requested).Should().BeFalse();
        ledger.IsCommitted(Requested).Should().BeFalse(
            "only a committed definition that no longer carries the request proves it was consumed");
    }

    [Fact]
    public void AConsumingWrite_IsACommit_SoARealNonConvergenceStaysLoud()
    {
        var ledger = new NodeTypeCompilationHelpers.AdoptedSourceStampLedger();
        // Every source the bundle was built from has landed, and the fingerprints agree.
        var landed = AdoptedWhileSourcesAreLanding(
                Live("Store/Order/Source/OrderContent", "Store/Order/Source/OrderCheckout"))
            with { CurrentSourceFingerprint = "b06c6eeee4daab3d" };

        var written = Pass(landed);
        written.RequestedSourceStampAt.Should().BeNull("the judgement ran and consumed the request");
        written.IsDirty.Should().BeFalse();

        ledger.RecordCommit(written, Requested).Should().BeTrue();
        ledger.IsCommitted(Requested).Should().BeTrue(
            "after a confirmed consuming write, a later emission still carrying this request IS "
            + "non-convergence, and must stay an Error");
        ledger.IsCommitted(Requested.AddSeconds(1)).Should().BeFalse(
            "a NEWER request is a new trigger, never mistaken for the committed one");
    }

    [Fact]
    public void AStandingRequestOnALeavingHub_IsANoOpWrite()
    {
        var landing = AdoptedWhileSourcesAreLanding(
            Live("Store/Order/Source/OrderContent", "Store/Order/Source/OrderCheckout"));
        var node = NodeOf(landing);
        var written = NodeTypeCompilationHelpers.StampOnce(
            node,
            (_, _) => throw new InvalidOperationException("a leaving hub must not judge the adoption (#3129)"),
            () => true);
        written.Should().BeSameAs(node, "an unchanged node is a no-op write");
    }
}
