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

    /// <summary>
    /// 🚨 A discriminator that merely ENDS in the type's name is a SENDER's string, not the
    /// platform's. This reader runs before any hub has read the frame, so it is what decides
    /// whether the router treats a delivery as a recycle — and that answer must come from the two
    /// names <c>TypeRegistry</c> actually serves for <see cref="DisposeRequest"/> (its canonical
    /// <c>Type.Name</c> and its dot-joined full name), never from a suffix.
    ///
    /// <para>What the loose form cost: a frame nobody registered still reached the router's
    /// dispose branch, which answers <c>Ignored()</c> and deliberately builds NO hub — so a sender
    /// could suppress the activation a delivery to a cold address would otherwise cause, by naming
    /// its own type. (It could not reach <c>HandleDispose</c>: that needs the registry to RESOLVE
    /// the discriminator, and an unregistered one is failed in
    /// <c>MessageService.DeserializeDelivery</c> as "not registered in this hub's TypeRegistry".)</para>
    /// </summary>
    [Theory]
    [InlineData("""{"$type":"Attacker.DisposeRequest","reason":"teardown, please"}""")]
    [InlineData("""{"$type":"MyDisposeRequest"}""")]
    [InlineData("""{"$type":"MeshWeaver.Messaging.Evil.DisposeRequest"}""")]
    [InlineData("""{"$type":"Some.Deep.Namespace.DisposeRequest"}""")]
    [InlineData("""{"$type":"disposerequest"}""")]
    public void ATypeNameThatMerelyEndsInTheDiscriminator_IsNotADispose(string content)
    {
        DisposeRequestEnvelope.TryRead(Delivery(new RawJson(content)), out var read).Should().BeFalse(
            "suffix-matching a type name is not authentication — only the discriminators the "
            + "TypeRegistry serves for DisposeRequest name a DisposeRequest");
        read.Should().BeNull();
    }

    /// <summary>
    /// The POSITIVE control for the theory above, and for the two the <c>[Theory]</c> at the top of
    /// this file already reads: narrowing the match to NOTHING would pass every negative case, so
    /// the accepted set is pinned to what <c>TypeRegistry</c> serves — <c>Type.Name</c> as the
    /// canonical map's key, the dot-joined full name as <c>IndexFullNameAlias</c>'s input alias.
    /// </summary>
    [Fact]
    public void TheAcceptedDiscriminators_AreTheOnesTheTypeRegistryServes()
    {
        var canonical = typeof(DisposeRequest).Name;
        var fullName = typeof(DisposeRequest).FullName!;

        canonical.Should().Be("DisposeRequest", "TypeRegistry keys its canonical map by Type.Name");
        fullName.Should().Be("MeshWeaver.Messaging.DisposeRequest",
            "and indexes the dot-joined full name as the input-side alias");

        DisposeRequestEnvelope.TryRead(
                Delivery(new RawJson($$"""{"$type":"{{canonical}}"}""")), out _)
            .Should().BeTrue("the canonical name is the discriminator the registry EMITS");
        DisposeRequestEnvelope.TryRead(
                Delivery(new RawJson($$"""{"$type":"{{fullName}}"}""")), out _)
            .Should().BeTrue("the full name is the alias it accepts on the way IN");
    }

    [Fact]
    public void ATypedNonDispose_IsNotADispose()
    {
        DisposeRequestEnvelope.TryRead(Delivery(new RawJsonPassThrough()), out var read).Should().BeFalse();
        read.Should().BeNull();
    }
}
