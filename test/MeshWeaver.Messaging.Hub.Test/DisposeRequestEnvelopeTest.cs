using System.Text.Json;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// <see cref="DisposeRequestEnvelope"/> recognises a dispose whether it is typed (an in-process post)
/// or still packaged as <see cref="RawJson"/> (a delivery that crossed a hub boundary), and nothing
/// else — the check the router and the grain make before any hub exists to deserialise it.
/// </summary>
public class DisposeRequestEnvelopeTest
{
    private static IMessageDelivery Delivery(object message) =>
        new MessageDelivery<object>(new Address("probe", "1"), new Address("target", "1"), message, new JsonSerializerOptions());

    [Fact]
    public void ATypedDispose_IsRead_AsItself()
    {
        var typed = new DisposeRequest { Reason = "why", CascadedFrom = "Store/Core" };

        DisposeRequestEnvelope.TryRead(Delivery(typed), out var read).Should().BeTrue();
        read.Should().BeSameAs(typed);
    }

    [Theory]
    [InlineData("""{"$type":"DisposeRequest","reason":"the operator asked","cascadedFrom":"Store/Core"}""")]
    [InlineData("""{"$type":"MeshWeaver.Messaging.DisposeRequest","reason":"the operator asked","cascadedFrom":"Store/Core"}""")]
    [InlineData("""{"$type":"DisposeRequest","Reason":"the operator asked","CascadedFrom":"Store/Core"}""")]
    public void APackagedDispose_IsRead_FromItsEnvelope(string content)
    {
        DisposeRequestEnvelope.TryRead(Delivery(new RawJson(content)), out var read).Should().BeTrue(
            "a delivery that crossed a hub boundary is RawJson until its target reads it — and the "
            + "router and the grain have to answer 'is this a dispose?' before there is a target");
        read!.Reason.Should().Be("the operator asked");
        read.CascadedFrom.Should().Be("Store/Core");
    }

    [Fact]
    public void APackagedDispose_WithoutTheOptionalFields_IsStillADispose()
    {
        DisposeRequestEnvelope.TryRead(Delivery(new RawJson("""{"$type":"DisposeRequest"}""")), out var read)
            .Should().BeTrue();
        read!.Reason.Should().BeNull();
        read.CascadedFrom.Should().BeNull("a request without the field is a DIRECT recycle, which cascades");
    }

    [Theory]
    [InlineData("""{"$type":"GetDataRequest","reference":{}}""")]
    [InlineData("""{"$type":"DeliveryFailure","message":"a DisposeRequest failed"}""")]
    [InlineData("""not json at all — DisposeRequest""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("")]
    public void AnythingElse_IsNotADispose(string content)
    {
        DisposeRequestEnvelope.TryRead(Delivery(new RawJson(content)), out var read).Should().BeFalse(
            "a frame that merely MENTIONS the word, or is not an object at all, must not be torn down "
            + "as if it were a recycle");
        read.Should().BeNull();
    }

    [Fact]
    public void ATypedNonDispose_IsNotADispose()
    {
        DisposeRequestEnvelope.TryRead(Delivery(new RawJsonPassThrough()), out var read).Should().BeFalse();
        read.Should().BeNull();
    }
}
