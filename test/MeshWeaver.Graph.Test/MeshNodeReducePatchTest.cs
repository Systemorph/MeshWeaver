using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Deterministic pin for <see cref="MeshDataSourceExtensions.ReduceToMeshNode"/>'s Patch path —
/// the reducer behind every own-node <c>MeshNodeReference</c> stream on a per-node hub.
///
/// <para><b>The defect this pins (FrameworkStaleInstanceRenderTest CI flake, run
/// 29749071939):</b> the per-NodeType hub's InstanceCollection holds the own node
/// side-by-side with its satellites (Source / Release / _Activity). When a Patch
/// change carried updates ONLY for a satellite, the old
/// <c>?? current.Updates.FirstOrDefault()</c> fallback emitted the SIBLING MeshNode as
/// the own-node stream's value with a bumped stream version. UpdateOwn's echo
/// detection accepted that foreign emission as "my write landed" and completed
/// BEFORE the caller's update lambda ran — HandleDispatchCompile then read
/// <c>weTransitioned == false</c>, skipped the compile dispatch, and the NodeType
/// wedged at Compiling forever (the observed CI trace: <c>Status=Compiling fv=''
/// asm=none</c> at +350ms, then silence until the 60s request timeout).</para>
///
/// <para>Correct semantics, asserted here: a patch whose updates all target siblings
/// does not change the referenced node — the reducer must emit a null-Value item
/// (dropped by the reduced pipeline's not-null filter), never a sibling's node.</para>
/// </summary>
public class MeshNodeReducePatchTest
{
    private const string OwnPath = "type/StaleArea";
    private static readonly JsonSerializerOptions Options = new();

    private static readonly MeshNode OwnNode = new("StaleArea", "type")
    {
        Name = "Own",
        NodeType = MeshNode.NodeTypePath,
        Version = 5
    };

    private static readonly MeshNode SiblingSource = new("Source", OwnPath)
    {
        Name = "code",
        NodeType = "Code",
        Version = 3
    };

    private static InstanceCollection Collection(params MeshNode[] nodes)
        => new()
        {
            Instances = nodes.ToImmutableDictionary(n => (object)n.Id, n => (object)n)
        };

    private static ChangeItem<InstanceCollection> Patch(
        InstanceCollection collection, params EntityUpdate[] updates)
        => new(collection, ChangedBy: null, StreamId: "test",
            ChangeType.Patch, Version: 42, updates);

    [Fact]
    public void SiblingOnlyPatch_DoesNotEmitTheSiblingAsTheOwnNode()
    {
        var change = Patch(
            Collection(OwnNode, SiblingSource),
            new EntityUpdate(nameof(MeshNode), SiblingSource.Id, SiblingSource));

        var reduced = MeshDataSourceExtensions.ReduceToMeshNode(
            change, new MeshNodeReference(OwnPath), initial: false, Options);

        // The patch does not concern the referenced node: the reducer must not
        // surface the sibling (the old fallback returned SiblingSource here) and
        // must not re-assert the own node with a bumped version either — it emits
        // a null Value the reduced pipeline's not-null filter drops.
        reduced.Value.Should().BeNull(
            "a patch whose updates all target siblings does not change the referenced node");
    }

    [Fact]
    public void OwnNodePatch_EmitsTheOwnNode()
    {
        var updatedOwn = OwnNode with { Name = "Renamed", Version = 6 };
        var change = Patch(
            Collection(updatedOwn, SiblingSource),
            new EntityUpdate(nameof(MeshNode), updatedOwn.Id, updatedOwn) { OldValue = OwnNode });

        var reduced = MeshDataSourceExtensions.ReduceToMeshNode(
            change, new MeshNodeReference(OwnPath), initial: false, Options);

        reduced.Value.Should().NotBeNull();
        reduced.Value!.Path.Should().Be(OwnPath);
        reduced.Value.Name.Should().Be("Renamed");
    }

    [Fact]
    public void OwnNodePatch_AsJsonElement_StillResolvesTheOwnNode()
    {
        // Cross-hub / mirror patches carry the update payload as JsonElement. The
        // old code's typed-only match missed it and only resolved the own node BY
        // LUCK through the sibling fallback; the fix must resolve it by identity.
        var updatedOwn = OwnNode with { Name = "FromMirror", Version = 7 };
        var json = JsonSerializer.SerializeToElement(updatedOwn, Options);
        var change = Patch(
            Collection(updatedOwn, SiblingSource),
            new EntityUpdate(nameof(MeshNode), updatedOwn.Id, json));

        var reduced = MeshDataSourceExtensions.ReduceToMeshNode(
            change, new MeshNodeReference(OwnPath), initial: false, Options);

        reduced.Value.Should().NotBeNull();
        reduced.Value!.Path.Should().Be(OwnPath);
        reduced.Value.Name.Should().Be("FromMirror");
    }

    [Fact]
    public void MixedPatch_PicksTheOwnNodeUpdate_NotTheSibling()
    {
        var updatedOwn = OwnNode with { Name = "Mixed", Version = 8 };
        var change = Patch(
            Collection(updatedOwn, SiblingSource),
            new EntityUpdate(nameof(MeshNode), SiblingSource.Id, SiblingSource),
            new EntityUpdate(nameof(MeshNode), updatedOwn.Id, updatedOwn));

        var reduced = MeshDataSourceExtensions.ReduceToMeshNode(
            change, new MeshNodeReference(OwnPath), initial: false, Options);

        reduced.Value.Should().NotBeNull();
        reduced.Value!.Path.Should().Be(OwnPath);
        reduced.Value.Name.Should().Be("Mixed");
    }

    [Fact]
    public void PatchWithNoUpdates_FallsBackToFullValue()
    {
        // Legacy live-update guard: a Patch carrying no updates must still emit the
        // full reduced value rather than silently dropping the emission.
        var change = Patch(Collection(OwnNode, SiblingSource));

        var reduced = MeshDataSourceExtensions.ReduceToMeshNode(
            change, new MeshNodeReference(OwnPath), initial: false, Options);

        reduced.Value.Should().NotBeNull();
        reduced.Value!.Path.Should().Be(OwnPath);
    }
}
