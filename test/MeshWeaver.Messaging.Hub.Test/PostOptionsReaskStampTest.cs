using System.Text.Json;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Systemorph/MeshWeaver#6717 (item 1). <see cref="PostOptions.ReasksOnShutdown"/> is ONE
/// producer's claim about ITS OWN request: it asks the address's next activation again when this
/// one answers <c>ShuttingDown</c>. <see cref="PostOptions.ResponseFor"/> and
/// <see cref="PostOptions.WithRequestIdFrom"/> copy the request's property bag onto the reply, and
/// used to copy that claim with it. A reply has no producer that asks again, so a reply discarded
/// by a bring-up teardown would then be reported as teardown-normal (event 7301 at Debug) with
/// nobody re-asking for it. Both builders now drop the key and keep everything else.
/// </summary>
public class PostOptionsReaskStampTest
{
    private static readonly Address Requester = new("requester", "1");
    private static readonly Address Owner = new("owner", "1");
    private static readonly JsonSerializerOptions JsonOptions = new();

    private record Ask;

    private const string OtherKey = "carried-correlation";

    private static IMessageDelivery StampedRequest()
        => new MessageDelivery<Ask>(
            new Ask(),
            new PostOptions(Requester).WithTarget(Owner).ReaskingOnShutdown().WithProperty(OtherKey, "kept"),
            JsonOptions);

    [Fact]
    public void TheRequestCarriesTheStamp()
    {
        // Positive control: without this, the two facts below would pass on a request that never
        // carried the key at all.
        StampedRequest().Properties.ContainsKey(PostOptions.ReasksOnShutdown).Should().BeTrue();
    }

    [Fact]
    public void AReplyBuiltWithResponseFor_DoesNotInheritTheReaskStamp()
    {
        var request = StampedRequest();
        var reply = new PostOptions(Owner).ResponseFor(request);

        reply.Properties.ContainsKey(PostOptions.ReasksOnShutdown).Should().BeFalse(
            "the stamp is the requester's claim about its own request; a reply has nobody who asks again");
        reply.Properties.ContainsKey(OtherKey).Should().BeTrue("every other property still rides on the reply");
        reply.Properties[PostOptions.RequestId].Should().Be(request.Id, "the reply is still correlated");
    }

    [Fact]
    public void AMessageBuiltWithRequestIdFrom_DoesNotInheritTheReaskStamp()
    {
        var request = StampedRequest();
        var correlated = new PostOptions(Owner).WithRequestIdFrom(request);

        correlated.Properties.ContainsKey(PostOptions.ReasksOnShutdown).Should().BeFalse(
            "a correlated message is not the producer's request and makes no claim about re-asking");
        correlated.Properties.ContainsKey(OtherKey).Should().BeTrue("every other property still rides along");
        correlated.Properties[PostOptions.RequestId].Should().Be(request.Id, "it is still correlated");
    }
}
