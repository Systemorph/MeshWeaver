using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The recycling read loop must NAME which of its two failures happened (#2025).
///
/// <para><c>ThreadAgentIntegrationTest</c> flaked on shard 4 with
/// <c>AddressRecyclingException: … still recycling (ShuttingDown) after 110 probes</c>. That
/// sentence is consistent with two failures that have OPPOSITE fixes:</para>
///
/// <list type="bullet">
///   <item><description>ONE hub wedged in teardown — every probe hits the same corpse, the address
///   never reactivates. Look at that hub's disposal.</description></item>
///   <item><description>A recycle STORM — the address reactivates repeatedly and each successor
///   dies before it can answer. Look at whatever is asking for the recycles.</description></item>
/// </list>
///
/// <para>The probe COUNT cannot separate them, which is why the issue could only say "110 probes
/// is not a slow activation" and stop there. The activation id now rides on the ShuttingDown NACK
/// (<c>MessageService</c>), so the loop counts distinct owners and says which shape it saw.</para>
///
/// <para>Pinned as a pure function: the sentence is the deliverable, and it must be assertable
/// without a mesh, a recycle, or the flake.</para>
/// </summary>
public class RecyclingShapeDiagnosticTest
{
    [Fact]
    public void OneOwner_SaysTheAddressNeverReactivated_AndPointsAtDisposal()
    {
        var text = MeshNodeStreamExtensions.RecyclingShape(1);

        text.Should().Contain("SAME owner activation");
        text.Should().Contain("never reactivated");
        text.Should().Contain("DISPOSAL",
            "the reader must be sent to the wedged hub's teardown, which is where the fix is");
        text.Should().NotContain("STORM",
            "naming both shapes at once is the ambiguity this exists to remove");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(37)]
    [InlineData(110)]
    public void ManyOwners_SaysStorm_AndPointsAtWhoeverAsksForTheRecycles(int owners)
    {
        var text = MeshNodeStreamExtensions.RecyclingShape(owners);

        text.Should().Contain($"{owners} DISTINCT owner activations");
        text.Should().Contain("STORM");
        text.Should().Contain("requesting the recycles",
            "a storm is not any single hub's disposal bug — sending the reader there wastes the cycle");
        text.Should().NotContain("never reactivated");
    }

    /// <summary>
    /// Nothing observed must say NOTHING. A read that timed out before any NACK arrived knows
    /// which shape it saw exactly as well as it knows the node exists — not at all — and inventing
    /// a verdict there is how a diagnostic starts costing cycles instead of saving them.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NoOwnerObserved_SaysNothing(int owners) =>
        MeshNodeStreamExtensions.RecyclingShape(owners).Should().BeEmpty();
}
