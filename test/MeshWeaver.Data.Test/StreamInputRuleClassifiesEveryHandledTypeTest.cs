using System.Linq;
using System.Text.Json;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the input rule's classification (<see cref="StreamInputRule"/>): every message type a
/// synchronization hub handles has a role that says which party may send it, and the set is
/// explicit. A handler added for a new type fails here — and fails the stream's construction —
/// until the type is classified, so no input can reach a stream that takes its subscriber's input
/// only without the rule deciding on it.
/// </summary>
public class StreamInputRuleClassifiesEveryHandledTypeTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(src => src
                .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))));

    /// <summary>The classification itself: each type, and the party it must come from.</summary>
    [Fact]
    public void TheClassificationIsExplicit()
    {
        StreamInputRule.Roles.Select(r => $"{r.Type.Name}={r.Role}").Should().Equal(
            "IUserAction=SubscriberInput",
            "PatchDataChangeRequest=SubscriberInput",
            "DataChangeRequest=SubscriberInput",
            "DataChangedEvent=SubscriberInput",
            "StreamErrorEvent=SubscriberInput",
            "UpdateStreamRequest=OwnWrite",
            "SetCurrentRequest=OwnWrite",
            "UnsubscribeRequest=ReleaseOrAnswer",
            "GetDataResponse=ReleaseOrAnswer",
            "DeliveryFailure=ReleaseOrAnswer");
    }

    /// <summary>
    /// The types a real stream's hub handles are exactly the classified concrete types: adding a
    /// handler for a type the rule does not name fails this test.
    /// </summary>
    [Fact]
    public void EveryTypeAStreamHandlesIsClassified()
    {
        var workspace = GetHost().ServiceProvider.GetRequiredService<IWorkspace>();
        var stream = workspace.GetStream(typeof(BusinessUnit));
        var concrete = Assert.IsType<SynchronizationStream<EntityStore>>(stream, exactMatch: false);

        concrete.HandledMessageTypes.Select(t => t.Name).Order().Should().Equal(
            nameof(DataChangedEvent),
            nameof(DataChangeRequest),
            nameof(DeliveryFailure),
            nameof(GetDataResponse),
            nameof(PatchDataChangeRequest),
            "SetCurrentRequest",
            nameof(StreamErrorEvent),
            nameof(UnsubscribeRequest),
            "UpdateStreamRequest");
        Assert.All(concrete.HandledMessageTypes, t =>
            Assert.True(StreamInputRule.RoleOf(t) is not null, $"{t.Name} is handled, so the rule must classify it"));
    }

    /// <summary>
    /// The stream's nested generic messages are matched for every stream type, and a type the rule
    /// does not name has no role.
    /// </summary>
    [Fact]
    public void NestedStreamMessagesAreMatchedForEveryStreamType()
    {
        StreamInputRule.RoleOf(typeof(SynchronizationStream<JsonElement>.UpdateStreamRequest))
            .Should().Be(StreamDeliveryRole.OwnWrite);
        StreamInputRule.RoleOf(typeof(SynchronizationStream<EntityStore>.SetCurrentRequest))
            .Should().Be(StreamDeliveryRole.OwnWrite);
        StreamInputRule.RoleOf(typeof(SubscribeRequest)).Should().BeNull();
    }
}
