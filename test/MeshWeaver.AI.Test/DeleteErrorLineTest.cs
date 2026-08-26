using MeshWeaver.Mesh;
using System;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the per-path failure line of the agent <c>Delete</c> tool
/// (<see cref="MeshOperations.DeleteErrorLine"/>). The contract the maintainer asked for: a delete
/// BLOCKED by permissions is routine for agent identities (shared and system-synced spaces grant
/// them no Delete), so the tool must not read as a broken op to retry — it must hand the agent the
/// node's Delete page URL (<c>/{path}/Delete</c>) to PRESENT TO THE USER, who reviews and confirms
/// the deletion under their own identity in the GUI. Any other failure keeps the plain error line.
/// </summary>
public class DeleteErrorLineTest
{
    [Fact]
    public void Refusal_NamesTheDeletePageUrl_AndSaysDoNotRetry()
    {
        var line = MeshOperations.DeleteErrorLine(
            "ACME/Shared/Doc", new UnauthorizedAccessException("Access denied"));

        line.Should().StartWith("Refused deleting ACME/Shared/Doc: Access denied");
        line.Should().Contain("/ACME/Shared/Doc/Delete",
            "the agent must be able to hand the user the exact GUI URL");
        line.Should().Contain("do not retry");
        line.Should().Contain("?q=",
            "the query-set form of the URL is offered for deleting a whole result set");
    }

    [Fact]
    public void OtherFailures_KeepThePlainErrorLine()
    {
        var line = MeshOperations.DeleteErrorLine(
            "ACME/Doc", new InvalidOperationException("Node not found: ACME/Doc"));

        line.Should().Be("Error deleting ACME/Doc: Node not found: ACME/Doc");
    }
}
