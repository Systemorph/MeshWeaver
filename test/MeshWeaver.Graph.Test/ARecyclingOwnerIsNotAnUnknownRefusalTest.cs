using System;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A RECYCLE IS NOT A VERDICT ABOUT THE WRITE</b> — MeshWeaver#4484.
///
/// <para><b>The failure.</b> <c>DanglingNodeTypeUpdateTest.Upsert_WithTheNamedImportEscapeHatch_StillLands</c>
/// was refused on its third step, 62 ms in, with
/// <c>Hub … is shutting down (RunLevel=DisposeHostedHubs) — the address may reactivate</c>. The
/// owner was mid-recycle and fast-refused the intake, correctly and by its own contract.</para>
///
/// <para><b>Why it mattered.</b> Retyping a stranded node is the SANCTIONED repair (#2993 —
/// <c>patch</c> cannot write <c>nodeType</c>, so the upsert is the only route), and that retype is
/// itself what makes the owner's rebind watcher recycle the owner. So a human doing exactly the
/// right thing twice raced their own previous write — and the handler answered
/// <c>success=false reason=Unknown</c>, a sentence naming neither the recycle nor the retry that
/// would have worked. The refusal was accurate and unusable.</para>
///
/// <para>The messaging layer had already classified it: <see cref="DeliveryFailure.ErrorType"/> is
/// <see cref="ErrorType.ShuttingDown"/>, the transient "the address may reactivate" category, and
/// <see cref="DeliveryFailureException"/> carries the failure precisely so callers can map it to a
/// domain reason "without relying on the exception's user-facing message". The upsert handler threw
/// that away by flattening every non-<see cref="UnauthorizedAccessException"/> to
/// <see cref="NodeUpsertRejectionReason.Unknown"/>.</para>
///
/// <para><b>This does not close the race</b>, which is the framework's to fix — it makes the
/// refusal say which condition it is, so a caller can act on it.</para>
/// </summary>
public class ARecyclingOwnerIsNotAnUnknownRefusalTest
{
    /// <summary>A real delivery, built the way <c>DeliveryFailureClassificationWireTest</c> builds
    /// one — no mock: nothing here reads the delivery, but a hand-rolled IMessageDelivery would be a
    /// second definition of a framework type to drift from.</summary>
    private static IMessageDelivery ADelivery() =>
        new MessageDelivery<RawJson>
        {
            Message = new RawJson("{}"),
            Sender = new Address("test/sender"),
            Target = new Address("TestData/dntad82f245"),
        };

    private static DeliveryFailureException Failure(ErrorType type) =>
        new(new DeliveryFailure(
            ADelivery(),
            "Hub TestData/dntad82f245 is shutting down (RunLevel=DisposeHostedHubs)")
        {
            ErrorType = type,
        });

    [Fact]
    public void AnOwnerThatIsRecycling_IsNamedAsSuch_NotAsUnknown()
    {
        NodeUpsertRejection.Classify(Failure(ErrorType.ShuttingDown))
            .Should().Be(NodeUpsertRejectionReason.AddressRecycling,
                "the messaging layer already classified this as transient — flattening it to Unknown "
                + "tells a caller who did everything right that something unnameable went wrong");
    }

    [Fact]
    public void EveryOtherDeliveryFailure_StaysUnknown()
    {
        // The negative control, and the reason the match is on ErrorType rather than on the
        // message: a hub that is GONE, or a handler that threw, is not a retry invitation, and
        // widening this would turn a terminal refusal into advice to try again forever.
        NodeUpsertRejection.Classify(Failure(ErrorType.NotFound))
            .Should().Be(NodeUpsertRejectionReason.Unknown);
        NodeUpsertRejection.Classify(Failure(ErrorType.Exception))
            .Should().Be(NodeUpsertRejectionReason.Unknown);
    }

    [Fact]
    public void AuthorizationStillWins_AndAPlainFaultIsStillUnknown()
    {
        NodeUpsertRejection.Classify(new UnauthorizedAccessException("denied"))
            .Should().Be(NodeUpsertRejectionReason.Unauthorized,
                "the pre-existing classification must survive the new one");
        NodeUpsertRejection.Classify(new InvalidOperationException("boom"))
            .Should().Be(NodeUpsertRejectionReason.Unknown);
        NodeUpsertRejection.Classify(null)
            .Should().Be(NodeUpsertRejectionReason.Unknown);
    }

}
