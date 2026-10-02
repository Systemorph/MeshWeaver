using System;
using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Hosting.Persistence.PartitionStorage;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 The inventory of what a participant connection may never post, pinned type by type.
///
/// <para>Every ingress forwards any delivery to any address, so each message type whose handler
/// trusts the message — a raw storage read or write, a compile from a payload, a hub's or stream's
/// own plumbing — is reachable by any client unless it is <see cref="InfrastructureOnlyAttribute"/>.
/// A type dropped from the attribute shows up here as the first failure, and a stamped delivery of
/// each is refused by <see cref="ParticipantIngress.Refuses"/> (the predicate the receiving hub runs).
/// The control half pins that the ordinary, permission-checked client requests stay postable — an
/// over-broad filter would break every client while every refusal below still passed.</para>
/// </summary>
public class ParticipantIngressRefusesInfrastructureTest
{
    private static readonly JsonSerializerOptions Options = new();
    private static readonly AccessContext Admin = new() { ObjectId = "admin", Name = "admin" };

    /// <summary>Every infrastructure-only message, one per row.</summary>
    public static TheoryData<Type> InfrastructureTypes => new()
    {
        typeof(SaveMeshNodeRequest),
        typeof(DispatchCompileTrigger),
        typeof(TrackActivityRequest),
        typeof(InitializeHubRequest),
        typeof(SynchronizationStream<EntityStore>.SetCurrentRequest),
        typeof(SynchronizationStream<EntityStore>.UpdateStreamRequest),
        typeof(WriteBatchRequest),
        typeof(DeleteBatchRequest),
        typeof(ReadNodeRequest),
        typeof(ExistsRequest),
        typeof(ListChildPathsRequest),
        typeof(ListDescendantPathsRequest),
    };

    /// <summary>Client requests that are checked under the caller's own identity — postable.</summary>
    public static TheoryData<Type> ClientRequests => new()
    {
        typeof(CreateNodeRequest),
        typeof(CreateOrUpdateNodeRequest),
        typeof(DeleteNodeRequest),
        typeof(MoveNodeRequest),
        typeof(DataChangeRequest),
        typeof(PatchDataRequest),
        typeof(SubscribeRequest),
        typeof(DeleteMeshNodeRequest),
    };

    [Theory]
    [MemberData(nameof(InfrastructureTypes))]
    public void AnInfrastructureType_IsRefusedFromAParticipant_AndOnlyFromAParticipant(Type type)
    {
        ParticipantIngress.IsParticipantPostable(type).Should().BeFalse($"{type.Name} is mesh infrastructure");

        var message = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        var delivery = Delivery(message);

        ParticipantIngress.Refuses(delivery.FromParticipant(Admin, "test"))
            .Should().NotBeNull($"a participant's {type.Name} is refused, even an admin's")
            .And.Contain(type.Name);
        // The negative control: the same delivery the mesh's own hubs post is not refused.
        ParticipantIngress.Refuses(delivery.SetAccessContext(Admin))
            .Should().BeNull($"the mesh's own {type.Name} carries no ingress stamp and must still flow");
    }

    [Theory]
    [MemberData(nameof(ClientRequests))]
    public void ACheckedClientRequest_StaysPostable(Type type)
    {
        ParticipantIngress.IsParticipantPostable(type).Should().BeTrue(
            $"{type.Name} is checked under the caller's own identity, so a client may send it");
        var message = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        ParticipantIngress.Refuses(Delivery(message).FromParticipant(Admin, "test")).Should().BeNull();
    }

    /// <summary>Raw JSON is not judged at the ingress — the receiving hub judges it once typed —
    /// so the ingress-side predicate must let it through rather than guess.</summary>
    [Fact]
    public void RawJson_IsLeftToTheReceivingHub()
        => ParticipantIngress.Refuses(Delivery(new RawJson("{}")).FromParticipant(Admin, "test")).Should().BeNull();

    private static IMessageDelivery Delivery(object message)
        => (IMessageDelivery)Activator.CreateInstance(
            typeof(MessageDelivery<>).MakeGenericType(message.GetType()),
            new Address("client/test"), new Address("TestData/target"), message, Options)!;
}
