using System;
using System.Data.Common;
using System.Net.Sockets;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 MeshWeaver#7066: the owner classifies a store fault's TRANSIENCE where the exception still has
/// its type, and the classification crosses the hub boundary on <see cref="MeshNodeError.Transient"/>.
/// The incident's caller held only <c>MeshNode Unknown at 'Admin/Build': NpgsqlException: Failed to
/// connect</c> — no inner exception — and could not tell a 40-second database restart from a defect.
/// </summary>
public class PatchFaultCarriesTransienceTest
{
    private sealed class DriverException(string message, Exception? inner, bool transient) : DbException(message, inner)
    {
        public override bool IsTransient => transient;
    }

    [Fact]
    public void AStoreThatRefusedTheConnection_IsCarriedAsTransient_AndKeepsItsCode()
    {
        var error = DataExtensions.ClassifyPatchException(
            new DriverException("Failed to connect to 10.0.0.1:5432", new SocketException(111), transient: true),
            "Admin/Build");

        error.Transient.Should().BeTrue("the owner saw a refused connection — the caller must be able to know");
        error.Code.Should().Be(MeshNodeErrorCode.Unknown,
            "the code and its retry contract are unchanged; transience is an additional fact");
        error.Message.Should().StartWith("DriverException: Failed to connect");
    }

    [Fact]
    public void ADefect_IsNotTransient()
    {
        DataExtensions.ClassifyPatchException(new InvalidOperationException("a real defect"), "Admin/Build")
            .Transient.Should().BeFalse();
        DataExtensions.ClassifyPatchException(
                new DriverException("relation \"mesh_nodes\" does not exist", null, transient: false), "Admin/Build")
            .Transient.Should().BeFalse("a query/schema error is the provider's own non-transient verdict");
    }

    /// <summary>
    /// 🚨 An aggregate is transient only when EVERY branch is (review on #7088). The record loses the
    /// aggregate on the wire, so a mixed one flagged transient would have the caller re-run a defect.
    /// </summary>
    [Fact]
    public void AMixedAggregate_IsNotTransient_OnTheWire()
    {
        Exception Refused() =>
            new DriverException("Failed to connect to 10.0.0.1:5432", new SocketException(111), transient: true);

        DataExtensions.ClassifyPatchException(
                new AggregateException(Refused(), new InvalidOperationException("a real defect")), "Admin/Build")
            .Transient.Should().BeFalse("the refused-connection branch must not launder the defect branch");
        DataExtensions.ClassifyPatchException(
                new InvalidOperationException("wrapped",
                    new AggregateException(Refused(), new InvalidOperationException("a real defect"))), "Admin/Build")
            .Transient.Should().BeFalse("a mixed aggregate deeper in the chain is still mixed");
        DataExtensions.ClassifyPatchException(
                new AggregateException(new InvalidOperationException("a real defect"), Refused()), "Admin/Build")
            .Transient.Should().BeFalse("branch order must not matter");
        DataExtensions.ClassifyPatchException(new AggregateException(Refused(), Refused()), "Admin/Build")
            .Transient.Should().BeTrue("every branch transient is transient");
    }

    [Fact]
    public void TheFlag_SurvivesTheWire()
    {
        var options = new System.Text.Json.JsonSerializerOptions();
        var sent = new MeshNodeError(MeshNodeErrorCode.Unknown, "Admin/Build", "NpgsqlException: Failed to connect")
        { Transient = true };

        var received = System.Text.Json.JsonSerializer.Deserialize<MeshNodeError>(
            System.Text.Json.JsonSerializer.Serialize(sent, options), options);

        received!.Transient.Should().BeTrue();
        received.Should().Be(sent);
    }
}
