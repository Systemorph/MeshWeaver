using System;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Which failed deliveries the late-verdict registry will take, and which it answers <c>false</c>
/// for — the shape half of #3303, pinned without a mesh because the decision needs none.
///
/// <para><c>RefusedReplyReachesTheWaiterTest</c> proves the message service ASKS on a refused reply.
/// This proves the registry answers correctly when asked — including the two shapes that would be
/// bugs in opposite directions: taking a message nobody armed a watch for (an answer invented for a
/// caller that never asked) and refusing the <see cref="DeliveryFailure"/> arm (the #2661 RLS
/// refusal, which is a NACK that is not a <see cref="PatchDataResponse"/> and carries the same
/// correlation id).</para>
/// </summary>
public class UndeliverableReplyShapesTest
{
    private const string Path = "TestData/salvage-shapes";

    private static readonly Address Sender = new("owner", "1");

    private static readonly Address Waiter = new("client", "1");

    /// <summary>A failed delivery of <paramref name="message"/>, correlated to
    /// <paramref name="requestId"/> when one is given.</summary>
    private static IMessageDelivery Failed<T>(T message, string? requestId)
        where T : notnull
    {
        var options = new PostOptions(Sender).WithTarget(Waiter);
        if (requestId is not null)
            options = options.WithProperty(PostOptions.RequestId, requestId);
        // `Failed` is a default interface method, so the delivery is typed as the interface first.
        IMessageDelivery delivery = new MessageDelivery<T>(message, options, JsonSerializerOptions.Default);
        return delivery.Failed("its parent hub is shutting down", ErrorType.ShuttingDown);
    }

    private static (LatePatchResponseRegistry Registry, string RequestId) Armed(
        Action<PatchDataResponse>? onResponse = null,
        Action<DeliveryFailure>? onFailure = null)
    {
        var registry = new LatePatchResponseRegistry();
        var requestId = $"salvage-{Guid.NewGuid():N}";
        registry.Register(requestId, Path, onResponse ?? (_ => { }), onFailure ?? (_ => { }));
        return (registry, requestId);
    }

    [Fact]
    public void ARefusedPatchResponse_ReachesTheArmedWatch()
    {
        PatchDataResponse? delivered = null;
        var (registry, requestId) = Armed(onResponse: r => delivered = r);
        var verdict = new PatchDataResponse(false, 7L) { Error = "owner disposing" };

        registry.TryDeliver(Failed(verdict, requestId)).Should().BeTrue();

        delivered.Should().BeSameAs(verdict, "the verdict is handed over, never re-minted");
        registry.ArmedCount.Should().Be(0, "the dispatch consumes the entry — exactly once");
    }

    /// <summary>
    /// 🚨 The #2661 arm. An owner's RLS refusal is a <see cref="DeliveryFailure"/>, not a
    /// <see cref="PatchDataResponse"/>, and it carries the SAME correlation id. A salvage that knew
    /// only about the response type would drop a denial and leave the caller with its optimistic
    /// success.
    /// </summary>
    [Fact]
    public void ARefusedDeliveryFailure_ReachesTheArmedWatch()
    {
        DeliveryFailure? delivered = null;
        var (registry, requestId) = Armed(onFailure: f => delivered = f);
        var refusal = new DeliveryFailure(Failed("patch", null), "Access denied")
        {
            ErrorType = ErrorType.Unauthorized,
        };

        registry.TryDeliver(Failed(refusal, requestId)).Should().BeTrue();

        delivered.Should().BeSameAs(refusal);
        registry.ArmedCount.Should().Be(0);
    }

    /// <summary>
    /// A REQUEST carries no correlation id — it IS the correlation. That is the whole test that
    /// keeps requests on the ordinary NACK path, so it must be the reason this returns false.
    /// </summary>
    [Fact]
    public void AFailedDeliveryWithNoCorrelationId_IsNotTaken()
    {
        var (registry, _) = Armed();

        registry.TryDeliver(Failed(new PatchDataResponse(true, 1L), requestId: null))
            .Should().BeFalse();

        registry.ArmedCount.Should().Be(1, "an uncorrelated delivery must not consume somebody "
            + "else's watch — the entry is still waiting for its own verdict");
    }

    /// <summary>
    /// A correlated reply of a shape this registry never armed for — a read's answer, a layout
    /// frame — is <c>false</c>, not a guess. Those callers have their own recovery, and inventing a
    /// verdict here would answer a question nobody asked.
    /// </summary>
    [Fact]
    public void ACorrelatedReplyOfAnUnknownShape_IsNotTaken()
    {
        var (registry, requestId) = Armed();

        registry.TryDeliver(Failed(new GetDataResponse(null, 3L), requestId)).Should().BeFalse();

        registry.ArmedCount.Should().Be(1);
    }

    [Fact]
    public void AReplyNobodyIsWaitingFor_IsNotTaken()
    {
        var registry = new LatePatchResponseRegistry();

        registry.TryDeliver(Failed(new PatchDataResponse(true, 1L), "nobody-armed-this"))
            .Should().BeFalse("a miss is one dictionary lookup, which is what makes it affordable "
                + "to ask on every failed delivery");
    }
}
