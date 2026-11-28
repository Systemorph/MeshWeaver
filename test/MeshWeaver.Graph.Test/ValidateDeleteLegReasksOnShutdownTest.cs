using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #8645: a <see cref="ValidateDeleteRequest"/> parked behind a leaf's initialization gates when a
/// retire sweep disposes that leaf was reported by the <c>[DISPOSE-DISCARD]</c> site at Error
/// ("stranded work"). Both delete producers ride a <c>ShuttingDown</c> answer out and ask the next
/// activation (<c>RideOutRecyclingLeaf</c>), so each leg must declare it
/// (<see cref="PostOptions.ReasksOnShutdown"/>), which makes the discard teardown-normal at Debug.
/// </summary>
public class ValidateDeleteLegReasksOnShutdownTest
{
    [Fact]
    public void TheLegIsStampedAndTargetedAtTheLeaf()
    {
        var o = MeshExtensions.ValidateDeleteOptions(new PostOptions(new Address("portal", "nodeops")).WithTarget(new Address("portal", "nodeops")), "A/B/C", null);

        o.Properties.ContainsKey(PostOptions.ReasksOnShutdown).Should().BeTrue(
            "the producer re-asks on ShuttingDown, so the discard has no stranded waiter");
        // Target is internal to the messaging hub, so it is read reflectively.
        var target = typeof(PostOptions).GetProperty("Target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        target.Should().NotBeNull();
        target?.GetValue(o).Should().Be(new Address("A/B/C"),
            "the helper overrides whatever target the pre-set options carried and aims at the deleted path");
    }
}
