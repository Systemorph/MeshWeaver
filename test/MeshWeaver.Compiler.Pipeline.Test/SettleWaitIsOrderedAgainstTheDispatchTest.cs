using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🔴 <b>The ordering pin for issue #3265 — "a state older than the dispatch cannot answer a
/// question about the dispatch".</b>
///
/// <para><b>The defect.</b> <c>NodeTypeContractHandler</c> guards against running a second Roslyn
/// compile beside the watcher's by DISPATCHING one (<c>EnsureCompileDispatched</c> flips
/// <see cref="CompilationStatus.Pending"/>) and then WAITING for it to settle. The wait subscribed
/// a freshly derived own-node stream, and a new subscriber's initial replay is not guaranteed to
/// carry the owner's latest commit — the reduced own-node pipeline seeds it from a source that can
/// lag a write the same hub has already applied. So the wait was handed the PRE-dispatch snapshot,
/// whose <see cref="NodeTypeDefinition.CompilationStatus"/> is still <c>null</c>, and
/// <see cref="NodeTypeBuildState.IsCompilationSettled"/> answers TRUE for null — because for a
/// static-only type, or one that already has a usable build, "never compiled" IS a settled answer.
/// <c>Take(1)</c> took it and the handler compiled inline anyway.</para>
///
/// <para><b>What that cost.</b> Measured on 2026-09-04 (monolith,
/// <c>DOTNET_PROCESSOR_COUNT=2</c>, 8 runs out of 8): the dispatch committed v3=Pending, the settle
/// wait was handed v1 with a null status, and TWO Roslyn compiles ran for one NodeType — uploading
/// two assemblies under two store keys. Both then wrote a TERMINAL SUCCESS, 16 ms apart: this
/// handler's first (publishing <c>Status=Ok</c>), <c>RunCompile</c>'s second (re-stamping
/// <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/>,
/// <see cref="NodeTypeDefinition.LastCompiledVersion"/> and the assembly coordinates). So
/// <c>Status=Ok</c> was published while the pipeline still had a write to make, and anything that
/// stamped the node in that window was clobbered by the tail.</para>
///
/// <para>🚨 <b>The rejected fix</b> is pinned here too: making
/// <see cref="NodeTypeBuildState.IsCompilationSettled"/> refuse a null status would look like it
/// closes the hole and would instead hang every type the dispatch deliberately leaves alone. The
/// predicate was never wrong — it was asked about a state the dispatch had already superseded.</para>
/// </summary>
public class SettleWaitIsOrderedAgainstTheDispatchTest
{
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode NodeAt(long version, CompilationStatus? status) =>
        new("T", "type")
        {
            NodeType = MeshNode.NodeTypePath,
            Version = version,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = status,
            },
        };

    /// <summary>
    /// The replay that predates the dispatch must not be able to answer. Pushed synchronously
    /// through a <see cref="Subject{T}"/> so the assertion sits between the two emissions and can
    /// only pass for the right reason.
    /// </summary>
    [Fact]
    public void APreDispatchSnapshotCannotSettleTheDispatchedCompile()
    {
        // What EnsureCompileDispatched committed: the Pending flip, at version 3.
        var dispatched = NodeAt(3, CompilationStatus.Pending);

        var ownStream = new Subject<MeshNode>();
        MeshNode? settled = null;
        using var subscription = NodeTypeContractHandler
            .SettledAtOrAfter(ownStream, dispatched, Options)
            .Subscribe(node => settled = node);

        // The lagging initial replay: the node as it was BEFORE the dispatch, never compiled, so
        // CompilationStatus is null — which IsCompilationSettled accepts as settled.
        ownStream.OnNext(NodeAt(1, status: null));

        settled.Should().BeNull(
            "a snapshot from BEFORE the dispatch cannot answer whether the dispatched compile has "
            + "settled — accepting it is what let this handler run a second Roslyn compile beside "
            + "the watcher's and publish Status=Ok while the pipeline still had its terminal write "
            + "to make (#3265)");

        // The states the dispatch actually produced, arriving late.
        ownStream.OnNext(NodeAt(4, CompilationStatus.Compiling));
        settled.Should().BeNull("Compiling is not settled");

        ownStream.OnNext(NodeAt(5, CompilationStatus.Ok));
        settled.Should().NotBeNull("the dispatched compile reached a terminal state");
        settled!.Version.Should().Be(5,
            "the wait must answer with the state the DISPATCHED compile reached, never with the "
            + "one it superseded");
    }

    /// <summary>
    /// The no-op dispatch — an already-Ok type, a static-only type, one with a usable build —
    /// emits the authoritative unchanged node, so the gate is satisfied at that same version and
    /// the wait answers immediately. A gate written as strictly-greater would have stalled every
    /// one of these for the whole settle budget.
    /// </summary>
    [Fact]
    public void ANoOpDispatchIsAnsweredByItsOwnVersion()
    {
        var dispatched = NodeAt(7, CompilationStatus.Ok);

        var ownStream = new Subject<MeshNode>();
        MeshNode? settled = null;
        using var subscription = NodeTypeContractHandler
            .SettledAtOrAfter(ownStream, dispatched, Options)
            .Subscribe(node => settled = node);

        ownStream.OnNext(NodeAt(7, CompilationStatus.Ok));

        settled.Should().NotBeNull(
            "EnsureCompileDispatched returns the node UNCHANGED for a type it must not touch, so "
            + "the floor is that node's own version — a strictly-greater gate would hold every "
            + "static-only and already-built type for the whole 60 s settle budget");
        settled!.Version.Should().Be(7);
    }

    /// <summary>
    /// 🚨 The rejected alternative, pinned so it stays rejected: a null
    /// <see cref="NodeTypeDefinition.CompilationStatus"/> IS settled. That is not the bug, and
    /// "fixing" it here trades a race for a hang.
    /// </summary>
    [Fact]
    public void ANullCompilationStatusStaysSettled()
    {
        NodeAt(1, status: null).IsCompilationSettled(Options).Should().BeTrue(
            "for a static-only NodeType, or one whose usable build the dispatch deliberately left "
            + "alone, 'never compiled' is a settled answer — refusing it would hold their resolve "
            + "for the whole settle budget. The ordering gate, not the predicate, is what keeps a "
            + "stale null out of the dispatched compile's answer (#3265)");
    }
}
