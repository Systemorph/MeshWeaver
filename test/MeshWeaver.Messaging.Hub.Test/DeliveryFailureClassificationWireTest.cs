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
    /// 🚨 The verdict a FAILING SITE recorded on the DELIVERY must survive the wire too.
    ///
    /// <para><c>Failed(message, ErrorType)</c> exists so the classification is decided where the
    /// condition is known and CARRIED, "never reconstructed downstream by pattern-matching the
    /// message text". It is carried in <c>Properties["FailureErrorType"]</c>, an
    /// <c>IReadOnlyDictionary&lt;string, object&gt;</c> — and every consumer reads it back through
    /// <c>GetFailureErrorType</c>, whose test is <c>value is ErrorType</c>.</para>
    ///
    /// <para>That is the trap-door AGENTS.md names: an <c>object</c> that crossed a hub boundary is
    /// no longer the CLR type it was written as. The dictionary converter writes each value by its
    /// runtime type, so an <see cref="ErrorType"/> goes out as the JSON STRING the enum converter
    /// produces and comes back — via <c>ObjectPolymorphicConverter</c>, which materialises a JSON
    /// string as <see cref="string"/> — as a string. The type test then fails and every consumer
    /// silently reads the FALLBACK instead of the verdict.</para>
    ///
    /// <para>Concretely: the disposal race is classified <see cref="ErrorType.ShuttingDown"/> at the
    /// hub (#2350) and read back as the fallback by the router — which is why a delivery that raced
    /// a hub's disposal kept reaching the sender as terminal even after every producing site had
    /// been fixed to classify it (#2346).</para>
    /// </summary>
    [Theory]
    [InlineData(ErrorType.ShuttingDown)]
    [InlineData(ErrorType.Unavailable)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.CompilationFailed)]
    public void ADeliveryCarriedVerdict_SurvivesTheWire(ErrorType errorType)
    {
        var options = GetHost().JsonSerializerOptions;
        var delivery = ADelivery().Failed("Hub is shutting down", errorType);

        var json = JsonSerializer.Serialize(delivery, delivery.GetType(), options);
        Output.WriteLine(json);
        var round = JsonSerializer.Deserialize<IMessageDelivery>(json, options)!;

        round.GetFailureErrorType(ErrorType.Unknown).Should().Be(errorType,
            "the verdict is what the router acts on; falling back silently turns a transient "
            + "race into a terminal answer. Serialized as: {0}", json);
    }

    /// <summary>
    /// 🚨 The reader runs on the error-REPORTING path, so it must never THROW — the one place a
    /// throw is worst, because it replaces a failure that was about to be described with a
    /// different, undescribed one. A junk or out-of-range value is "no verdict was reached", which
    /// is precisely what the caller's fallback is for.
    /// </summary>
    [Theory]
    [InlineData(long.MaxValue)]      // out of int range — Convert.ToInt32 would throw here
    [InlineData(long.MinValue)]
    [InlineData(9999)]               // in range, but no such member
    public void AnOutOfRangeOrUndefinedVerdict_FallsBackInsteadOfThrowing(long recorded)
    {
        var delivery = ADelivery().Failed("boom")
            .WithProperty(IMessageDelivery.FailureErrorTypeProperty, recorded);

        delivery.GetFailureErrorType(ErrorType.Unavailable).Should().Be(ErrorType.Unavailable,
            "an unreadable verdict is an absence of one, never an exception on the path whose whole "
            + "job is to report a failure");
    }

    /// <summary>
    /// A verdict written as text that names no member is equally not a verdict — <c>Enum.TryParse</c>
    /// would also accept a NUMERIC string and mint an undefined member from it.
    /// </summary>
    [Theory]
    [InlineData("NotAnErrorType")]
    [InlineData("9999")]
    [InlineData("")]
    public void AnUnparseableVerdict_FallsBack(string recorded)
    {
        var delivery = ADelivery().Failed("boom")
            .WithProperty(IMessageDelivery.FailureErrorTypeProperty, recorded);

        delivery.GetFailureErrorType(ErrorType.Unavailable).Should().Be(ErrorType.Unavailable);
    }

    /// <summary>
    /// The failure TEXT degrades exactly like the verdict does — options without
    /// <c>ObjectPolymorphicConverter</c> leave it an untyped <see cref="JsonElement"/>. At a routing
    /// site that costs twice: the sender loses its diagnostic, AND the classification fallback loses
    /// the phrase it matches on, so a transient teardown silently reads terminal again.
    /// </summary>
    [Fact]
    public void TheFailureText_IsReadableEvenWhenItArrivedAsUntypedJson()
    {
        using var doc = JsonDocument.Parse("\"Hub is shutting down\"");
        var delivery = ADelivery().Failed("placeholder")
            .WithProperty("Error", doc.RootElement.Clone());

        delivery.GetFailureMessage().Should().Be("Hub is shutting down");
    }

    /// <summary>
    /// And a value under that key which is NOT text is not a message: null, so the caller describes
    /// the failure itself rather than printing a type name at a user.
    /// </summary>
    [Fact]
    public void ANonTextValueUnderTheErrorKey_IsNotAMessage()
    {
        ADelivery().Failed("placeholder").WithProperty("Error", 42)
            .GetFailureMessage().Should().BeNull();
    }

    /// <summary>
    /// The answer-once flag rides the same dictionary, and it is what stops one request being
    /// answered twice with contradictory verdicts. Key PRESENCE is the contract, so it must not
    /// depend on the value's CLR type surviving either.
    /// </summary>
    [Fact]
    public void TheAnswerOnceFlag_SurvivesTheWire()
    {
        var options = GetHost().JsonSerializerOptions;
        var delivery = ADelivery().FailedAndNacked("Hub is shutting down");

        var json = JsonSerializer.Serialize(delivery, delivery.GetType(), options);
        var round = JsonSerializer.Deserialize<IMessageDelivery>(json, options)!;

        round.SenderWasNacked.Should().BeTrue(
            "the owning hub already answered the sender; a second NACK makes the classification "
            + "a coin toss. Serialized as: {0}", json);
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
