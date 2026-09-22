using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A LOST RACE is not a tracking failure, and everything else still is (#4912).
///
/// <para>By the time <c>TrackActivity</c>'s observer sees a <see cref="MeshNodeErrorCode.Conflict"/>,
/// the framework has already done what the NACK asks for: <c>MeshNodeStreamExtensions</c> lists
/// Conflict among "the provably-safe NACK cases" and re-enqueues the ORIGINAL update lambda against
/// the freshest state, waiting up to <c>ConflictRebaseBound</c> for that state to arrive. A Conflict
/// reaching the observer therefore means the rebase budget was spent and a concurrent writer STILL
/// won — for <c>{user}/_UserActivity/{path}</c>, whose only competing writers are other
/// TrackActivity calls for the same user and path, that is a normal outcome.</para>
///
/// <para>Reported at Error it was worse than useless: the outer line is the incident fingerprint's
/// text, so every lost race minted a production incident and kept REOPENING the unrelated, already
/// fixed <c>Systemorph/MeshWeaver#1910</c>.</para>
///
/// <para>🚨 Both directions are pinned, and the second matters more. Demoting too much would hide
/// the failures this handler exists to surface — a denial, a validation refusal, an unreachable
/// owner — so every other code stays loud.</para>
/// </summary>
public class ActivityConflictSeverityTest
{
    private static MeshNodeStreamException Nack(MeshNodeErrorCode code, string message) =>
        new(new MeshNodeError(code, "rbuergi/_UserActivity/rbuergi", message));

    // The two refusal sentences verbatim from DataExtensions — the partial one is #2463's, the
    // total one is what #1910 was filed on. Both are the SAME structured code.
    private const string Partial =
        "cross-hub write PARTIALLY refused: 2 field(s) changed on the owner since the writer's "
        + "base. What did not conflict was kept — re-read and re-apply so the refused field(s) converge.";

    private const string Total =
        "cross-hub write refused: 2 field(s) changed on the owner since the writer's base and "
        + "nothing was applied.";

    [Theory]
    [InlineData(Partial)]
    [InlineData(Total)]
    public void EitherRefusalShape_IsAConcurrentWriteConflict(string message)
        // Classified off Error.Code, never the sentence — so rewording either message cannot
        // silently re-promote it, and a new third shape is covered the day it is written.
        => MeshNodeExtensions.IsConcurrentWriteConflict(Nack(MeshNodeErrorCode.Conflict, message))
            .Should().BeTrue();

    [Fact]
    public void ItIsFoundThroughAWrappedChain()
    {
        // The write is DETACHED, so the NACK arrives wrapped by whatever Rx and the post pipeline
        // put around it. A top-level-only check would miss every real occurrence.
        var wrapped = new InvalidOperationException(
            "TrackActivity pipeline faulted",
            new AggregateException(Nack(MeshNodeErrorCode.Conflict, Partial)));

        MeshNodeExtensions.IsConcurrentWriteConflict(wrapped).Should().BeTrue();
    }

    [Theory]
    [InlineData(MeshNodeErrorCode.AccessDenied)]
    [InlineData(MeshNodeErrorCode.Validation)]
    [InlineData(MeshNodeErrorCode.NotFound)]
    [InlineData(MeshNodeErrorCode.OwnerUnreachable)]
    [InlineData(MeshNodeErrorCode.Deserialization)]
    [InlineData(MeshNodeErrorCode.Unknown)]
    public void EveryOtherOwnerVerdict_StaysLoud(MeshNodeErrorCode code)
        // 🚨 THE control. These are terminal verdicts the framework never retries, and each one is
        // a real tracking bug: RLS silently dropping activity, a malformed record, an owner that
        // cannot be reached. Quietening them would turn this fix into the defect it removes.
        => MeshNodeExtensions.IsConcurrentWriteConflict(Nack(code, "refused"))
            .Should().BeFalse();

    [Theory]
    [InlineData(MeshNodeErrorCode.Conflict, true)]
    [InlineData(MeshNodeErrorCode.AccessDenied, false)]
    [InlineData(MeshNodeErrorCode.Validation, false)]
    [InlineData(MeshNodeErrorCode.NotFound, false)]
    [InlineData(MeshNodeErrorCode.OwnerUnreachable, false)]
    [InlineData(MeshNodeErrorCode.Unknown, false)]
    public void UpsertWireResponse_PreservesTheExistingConflictPolicy(MeshNodeErrorCode code, bool conflict)
    {
        var failure = new InvalidOperationException("wrapped", new AggregateException(Nack(code, Total)));
        var reason = NodeUpsertRejection.Classify(failure);
        reason.Should().Be(conflict ? NodeUpsertRejectionReason.Conflict : NodeUpsertRejectionReason.Unknown);
        var json = JsonSerializer.Serialize(CreateOrUpdateNodeResponse.Fail(Total, reason));
        var reply = JsonSerializer.Deserialize<CreateOrUpdateNodeResponse>(json)!;
        var translated = MeshNodeExtensions.ActivityUpsertFailure(reply, "rbuergi/_UserActivity/rbuergi");
        MeshNodeExtensions.IsConcurrentWriteConflict(translated).Should().Be(conflict);
    }

    [Fact]
    public void AStructuredRecyclingRefusal_RemainsAnActivityTeardown()
    {
        var reply = CreateOrUpdateNodeResponse.Fail("recycling", NodeUpsertRejectionReason.AddressRecycling);
        HubDisposingException.IsHubDisposal(MeshNodeExtensions.ActivityUpsertFailure(reply, "rbuergi/activity"))
            .Should().BeTrue();
        HubDisposingException.IsHubDisposal(MeshNodeExtensions.ActivityUpsertFailure(
            CreateOrUpdateNodeResponse.Fail("recycling", NodeUpsertRejectionReason.Unknown), "rbuergi/activity"))
            .Should().BeFalse("text alone is never evidence of a teardown");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InnerTeardown_RetainsItsPolicyThroughTheUpsertResponse(int shape)
    {
        var address = new Address("rbuergi/activity");
        Exception inner = shape switch
        {
            0 => new HubDisposingException(address, "activity write"),
            1 => new HubDisposedBeforeResponseException("activity", address, "update", address.ToString()),
            _ => new ObjectDisposedException("Autofac.LifetimeScope", "nested lifetimes cannot be created")
        };
        var reason = NodeUpsertRejection.Classify(new AggregateException(inner));
        reason.Should().Be(NodeUpsertRejectionReason.HubTeardown,
            "a lost response during teardown is distinct from an intake refusal that applied nothing");
        var reply = JsonSerializer.Deserialize<CreateOrUpdateNodeResponse>(JsonSerializer.Serialize(
            CreateOrUpdateNodeResponse.Fail(inner.Message, reason)))!;
        HubDisposingException.IsHubDisposal(MeshNodeExtensions.ActivityUpsertFailure(reply, address.ToString()))
            .Should().BeTrue();
        NodeUpsertRejection.Classify(new ObjectDisposedException("unrelated resource"))
            .Should().Be(NodeUpsertRejectionReason.Unknown);
    }

    [Fact]
    public void AnUnrelatedFault_StaysLoud()
        // Nothing about the word "conflict" is load-bearing: a plain exception whose text mentions
        // it is not an owner verdict at all.
        => MeshNodeExtensions.IsConcurrentWriteConflict(
                new InvalidOperationException("conflict while writing activity"))
            .Should().BeFalse();
}
