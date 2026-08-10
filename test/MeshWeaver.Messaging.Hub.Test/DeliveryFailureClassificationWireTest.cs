using System.Text.Json;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 A NACK's CLASSIFICATION must survive the wire, not just its prose.
///
/// <para><see cref="DeliveryFailure.ErrorType"/> and <see cref="DeliveryFailure.NodeTypePath"/>
/// are what a caller ACTS on — the GUI shows the compilation-error overlay because the failure
/// says <see cref="ErrorType.CompilationFailed"/> and names the broken NodeType. The human-readable
/// <c>Message</c> is for a log, not for control flow.</para>
///
/// <para>Over the Orleans path a DeliveryFailure is carried as JSON with the hub's own options
/// (<c>MessageHubGrain</c> → <c>streamCache.GetStream(path, meshHub.JsonSerializerOptions)</c>);
/// <c>DeliveryFailure</c> carries no Orleans <c>[GenerateSerializer]</c>, so this round-trip IS the
/// transport. <c>OrleansBrokenNodeTypeAccessTest</c> observed the message arriving intact while
/// <c>ErrorType</c> came back <see cref="ErrorType.Unknown"/> — a caller cannot tell a broken
/// NodeType from any other failure.</para>
/// </summary>
public class DeliveryFailureClassificationWireTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static IMessageDelivery ADelivery() =>
        new MessageDelivery<RawJson>
        {
            Message = new RawJson("{}"),
            Sender = new Address("test/sender"),
            Target = new Address("type/Broken/instance"),
        };

    /// <summary>
    /// The exact NACK the fallback hub posts for an instance of a non-compiling NodeType
    /// (<c>MessageHub</c>'s <c>UnhandledMessageNack</c> policy branch), round-tripped through the
    /// hub's serializer.
    /// </summary>
    [Fact]
    public void ACompilationNack_KeepsItsErrorTypeAndNodeTypePath_AcrossTheWire()
    {
        var options = GetHost().JsonSerializerOptions;
        var failure = new DeliveryFailure(ADelivery())
        {
            ErrorType = ErrorType.CompilationFailed,
            NodeTypePath = "type/Broken",
            Message = "NodeType 'type/Broken' has no usable hub configuration: Compilation failed",
        };

        var json = JsonSerializer.Serialize(failure, options);
        Output.WriteLine(json);
        var round = JsonSerializer.Deserialize<DeliveryFailure>(json, options)!;

        round.Message.Should().Be(failure.Message, "the prose already survived — that was never the bug");
        round.ErrorType.Should().Be(ErrorType.CompilationFailed,
            "the classification is what the caller acts on; Unknown makes a broken NodeType "
            + "indistinguishable from any other failure");
        round.NodeTypePath.Should().Be("type/Broken",
            "the NACK must still name the broken NodeType after the trip");
    }

    /// <summary>
    /// Every classification, not just the one that happened to be caught. A value that does not
    /// survive is worse than useless: it reads as <see cref="ErrorType.Unknown"/>, which callers
    /// treat as "unclassified" rather than "this failed to transmit".
    /// </summary>
    [Theory]
    [InlineData(ErrorType.Exception)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.CompilationFailed)]
    [InlineData(ErrorType.Failed)]
    public void EveryErrorType_SurvivesTheRoundTrip(ErrorType errorType)
    {
        var options = GetHost().JsonSerializerOptions;
        var failure = new DeliveryFailure(ADelivery()) { ErrorType = errorType, Message = "x" };

        var json = JsonSerializer.Serialize(failure, options);
        var round = JsonSerializer.Deserialize<DeliveryFailure>(json, options)!;

        round.ErrorType.Should().Be(errorType, "serialized as: {0}", json);
    }

    /// <summary>
    /// 🚨 ONE request, ONE failure response.
    ///
    /// <para>A caller's <c>Observe(...)</c> resolves on the FIRST DeliveryFailure it sees, so two
    /// responses for the same delivery make the classification a coin toss. The fallback-hub NACK
    /// path posts a typed failure and then marks the delivery Failed; the Failed-state check on the
    /// way out used to post a SECOND failure carrying the same prose (from
    /// <c>Properties["Error"]</c>) with the default <see cref="ErrorType.Unknown"/>. Same message,
    /// different verdict, whichever landed first — which is why the same code reported
    /// <see cref="ErrorType.CompilationFailed"/> locally and <see cref="ErrorType.Unknown"/> on CI.</para>
    ///
    /// <para>The marker is what suppresses the follow-up; this pins that it is set where the typed
    /// NACK is posted, so the suppression cannot be silently lost by a refactor of either branch.</para>
    /// </summary>
    [Fact]
    public void ADeliveryWhoseTypedFailureWasPosted_IsMarkedSoNoSecondFailureFollows()
    {
        var delivery = ADelivery().Failed("NodeType 'type/Broken' has no usable hub configuration")
            .WithProperty(MessageService.FailureAlreadyReported, true);

        delivery.Properties.ContainsKey(MessageService.FailureAlreadyReported).Should().BeTrue(
            "the marker is the only thing standing between one classified failure and two "
            + "contradictory ones");
        delivery.State.Should().Be(MessageDeliveryState.Failed,
            "the delivery is still a failure — it is the REPORTING that must not happen twice");
    }
}
