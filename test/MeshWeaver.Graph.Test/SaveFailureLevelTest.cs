using System;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #7887: an own-node store write refused because its subtree is being deleted is the designed
/// outcome of a delete racing a save, not a store fault. Production logged it at Error, so an agent
/// thread asked to delete the space it lives in raised a log incident and a ticket every time. The
/// refusal is logged at Information (the guard that raised it already logs it at Warning); a real store
/// fault stays at Error, because nothing else reports an acknowledged state that is not durable.
/// </summary>
public class SaveFailureLevelTest
{
    [Fact]
    public void ARefusalByAnInFlightDeletion_IsInformation()
    {
        var refusal = new SubtreeDeletionInFlightException(
            "Space/_Thread/delete-this-space", "Space");

        MeshDataSourceExtensions.SaveFailureLevel(refusal).Should().Be(LogLevel.Information,
            "the subtree is being deleted on purpose: if the delete commits the state is moot, and if it "
            + "aborts the save is re-attempted and a failing dispose flush is still reported");
    }

    /// <summary>NEGATIVE CONTROL: a store fault keeps the loud level.</summary>
    [Fact]
    public void AStoreFault_StaysError()
    {
        MeshDataSourceExtensions.SaveFailureLevel(new TimeoutException("connect timeout"))
            .Should().Be(LogLevel.Error,
                "an acknowledged own-node state that did not reach the store is reported nowhere else");
        MeshDataSourceExtensions.SaveFailureLevel(new InvalidOperationException("constraint"))
            .Should().Be(LogLevel.Error,
                "SubtreeDeletionInFlightException derives from InvalidOperationException; its base must not "
                + "inherit the lower level");
    }
}
