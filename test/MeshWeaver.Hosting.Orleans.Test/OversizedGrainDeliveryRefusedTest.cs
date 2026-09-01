using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 An oversized GRAIN-routed delivery is refused at the producer, loudly — issue #2897.
///
/// <para><b>The incident.</b> A 149,199,409-byte (~142 MB) delivery was dispatched to
/// <c>messagehub/AgenticEngineering</c> through <c>IMessageHubGrain.DeliverMessage</c>. Orleans
/// refuses the FRAME, not the message: <c>MessageSerializer.ThrowInvalidBodyLength</c> threw
/// <c>InvalidMessageFrameException: Invalid body size: 149199409 (max configured value is
/// 104857600, see MaxMessageBodySize)</c> from inside <c>Connection.ProcessOutgoing</c> — the
/// silo-to-silo connection's write loop. All five observed occurrences carry the IDENTICAL body
/// size, ~5 s apart, each from a NEW local port: one undeliverable message in a
/// reconnect-and-retry loop, not five incidents.</para>
///
/// <para><b>Why this is worse than losing one message.</b> A serializer fault in the connection's
/// write loop is not recoverable per-message, so Orleans tears the whole connection down. Every
/// unrelated delivery queued on that connection is collateral — which is how one oversized payload
/// presents as a partition that stops answering rather than as one request that times out. The
/// memory-stream twin of this defect (#1890) could only lose itself; this one takes a shared
/// connection with it.</para>
///
/// <para><b>Why refusal, and not a bigger limit.</b> <c>MaxMessageBodySize</c> IS configurable,
/// which is exactly the trap — #2897 says raising it "would mask the symptom and make 142 MB frames
/// normal traffic". The guard instead measures against the LIVE configured value, so a deployment
/// that legitimately tuned the limit is never falsely refused, and refuses only what that
/// deployment's own transport would have thrown away.</para>
///
/// <para><b>This can only convert a silent loss into a loud one.</b> The bound is the transport's
/// own, so nothing that is delivered today is newly refused —
/// <see cref="A_delivery_that_fits_is_dispatched_unchanged"/> is the control.</para>
/// </summary>
public class OversizedGrainDeliveryRefusedTest
{
    private static readonly Address Sender = new("client", "producer-1");
    private const string Target = "messagehub/AgenticEngineering";

    /// <summary>The incident's payload size, to the byte.</summary>
    private const int IncidentPayloadBytes = 149_199_409;

    /// <summary>The incident's reported limit, to the byte.</summary>
    private const int IncidentLimitBytes = 104_857_600;

    /// <summary>
    /// The behavioural bound the tests drive the guard at. The guard takes its limit as a
    /// parameter — the same seam <c>PostToStream</c> uses — so the decision path is exercised
    /// exactly as production runs it without allocating a 142 MB string (284 MB of UTF-16) on a
    /// shared build machine. The incident's real numbers are pinned arithmetically in
    /// <see cref="The_incident_payload_is_on_the_refusing_side_of_the_orleans_default"/>.
    /// </summary>
    private const int TestLimitBytes = 4096;

    /// <summary>Captures every record so a test can assert the refusal was REPORTED, not just made.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> records = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Records => records;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => records.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// A routed delivery whose packaged payload is <paramref name="payloadBytes"/> of ASCII JSON —
    /// the shape the router sees (MeshBuilder packages every delivery to <see cref="RawJson"/>
    /// before it reaches <c>RouteMessage</c>), so the guard measures the same thing production does.
    /// </summary>
    private static IMessageDelivery DeliveryOf(int payloadBytes, string id = "d-oversized")
    {
        const string head = "{\"$type\":\"CourseImportPayload\",\"nodes\":\"";
        const string tail = "\"}";
        var filler = new string('x', Math.Max(0, payloadBytes - head.Length - tail.Length));
        var json = head + filler + tail;
        Encoding.UTF8.GetByteCount(json).Should().Be(payloadBytes, "the fixture is exact ASCII");
        return new MessageDelivery<RawJson>(
            Sender, new Address("messagehub", "AgenticEngineering"), new RawJson(json),
            JsonSerializerOptions.Default) with
        { Id = id };
    }

    /// <summary>
    /// 🚨 THE regression test. A body Orleans cannot frame must never be handed to the grain call,
    /// and the drop must be attributable: the log names the target, the byte count, the limit, the
    /// delivery id and the sender — every fact <c>InvalidMessageFrameException</c> could not supply,
    /// since it names a body length and an endpoint pair and nothing about the message.
    ///
    /// <para>Against <c>origin/main</c> the dispatch is issued, Orleans throws inside the
    /// connection's write loop, the connection is destroyed, and the reconnect re-sends the same
    /// body. That is the loop the incident recorded.</para>
    /// </summary>
    [Fact]
    public void The_oversized_delivery_is_never_dispatched_to_the_grain()
    {
        var nacks = new List<(string Message, ErrorType Type)>();
        var logger = new RecordingLogger();

        var refused = RoutingGrain.RefuseOversizedGrainDispatch(
            delivery: DeliveryOf(TestLimitBytes + 1),
            addressPath: Target,
            grainKey: Target,
            postFailureToSender: (m, t) => nacks.Add((m, t)),
            logger: logger,
            limitBytes: TestLimitBytes);

        (refused is not null).Should().BeTrue(
            "a body over MaxMessageBodySize must terminate the leg HERE — dispatching it does not "
            + "fail this delivery politely, it tears down the silo-to-silo connection and takes "
            + "every unrelated message queued on it along, then retries the same undeliverable body");

        var nack = nacks.Should().ContainSingle(
            "the sender must be told terminally instead of waiting out its budget on a message "
            + "that can never land").Which;
        nack.Type.Should().Be(ErrorType.Rejected,
            "the size is a property of the message, not of the attempt — a transient verdict would "
            + "arm the caller's recovery machinery to retry something that cannot converge");

        var error = logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Error,
            "a refused delivery nobody can trace is the same dead end as a silent one").Which;
        error.Message.Should().Contain((TestLimitBytes + 1).ToString("N0"),
            "the report says HOW BIG the thing it dropped was");
        error.Message.Should().Contain(TestLimitBytes.ToString("N0"),
            "…and what limit it was measured against, so the reader can tell gross from marginal");
        error.Message.Should().Contain(Target, "…and where it was going");
        error.Message.Should().Contain("d-oversized", "…which delivery it was");
        error.Message.Should().Contain("producer-1", "…and who produced it");
        error.Message.Should().Contain("CourseImportPayload",
            "…and enough of the payload head to recognise WHAT was too big — the $type sits at the "
            + "front of the JSON, and identifying the producer is the question #2897 could not answer");
        error.Message.Should().Contain("connection",
            "…and the fact that separates this from a lost request: the frame is refused inside the "
            + "CONNECTION's write loop, so unrelated traffic dies with it");

        // 🚨 The payload is attacker-influenced content going into a log line, and a red burst is
        // re-assembled from its indented continuation lines — an embedded newline in the preview
        // would forge a second log record and split one incident into two.
        error.Message.Split('\n').Should().HaveCount(1,
            "the refusal must stay ONE log record: the payload head is JSON-quoted, so a newline "
            + "inside it cannot break the burst apart");
    }

    /// <summary>
    /// 🚨 THE CONTROL that makes the fix safe: the bound is Orleans' own, so a delivery that fits is
    /// dispatched exactly as before, with no NACK and no log. A guard that also refused working
    /// traffic would trade one incident for a far bigger one — every mesh message takes this path.
    /// </summary>
    [Fact]
    public void A_delivery_that_fits_is_dispatched_unchanged()
    {
        var nacks = new List<(string Message, ErrorType Type)>();
        var logger = new RecordingLogger();

        var refused = RoutingGrain.RefuseOversizedGrainDispatch(
            delivery: DeliveryOf(TestLimitBytes - 1, "d-fits"),
            addressPath: Target,
            grainKey: Target,
            postFailureToSender: (m, t) => nacks.Add((m, t)),
            logger: logger,
            limitBytes: TestLimitBytes);

        (refused is null).Should().BeTrue(
            "everything under the transport's own bound must reach the grain call exactly as before");
        nacks.Should().BeEmpty("a dispatched delivery must never NACK the sender");
        logger.Records.Should().BeEmpty("nor say anything about it");
    }

    /// <summary>
    /// The boundary is inclusive, and it is inclusive on the REFUSING side: Orleans compares the
    /// body length against the limit and throws at <c>&gt;=</c>, so a payload of exactly the limit
    /// is already undeliverable. An off-by-one the other way would let precisely the frames that
    /// tear the connection down through.
    /// </summary>
    [Fact]
    public void A_payload_of_exactly_the_limit_is_refused()
    {
        var nacks = new List<(string Message, ErrorType Type)>();

        var refused = RoutingGrain.RefuseOversizedGrainDispatch(
            delivery: DeliveryOf(TestLimitBytes, "d-exact"),
            addressPath: Target,
            grainKey: Target,
            postFailureToSender: (m, t) => nacks.Add((m, t)),
            logger: new RecordingLogger(),
            limitBytes: TestLimitBytes);

        (refused is not null).Should().BeTrue(
            "the frame carries an envelope on top of the body, so a payload AT the limit is "
            + "already over it on the wire");

        nacks.Should().ContainSingle();
    }

    /// <summary>
    /// 🚨 CALIBRATION — the incident's own numbers. The guard is only meaningful if the payload that
    /// destroyed the connection lands on the refusing side of the bound the router applies. Pinned
    /// arithmetically rather than by allocating 284 MB of UTF-16 on a shared machine; the decision
    /// rule itself is exercised behaviourally by the tests above.
    /// </summary>
    [Fact]
    public void The_incident_payload_is_on_the_refusing_side_of_the_orleans_default()
    {
        IncidentPayloadBytes.Should().BeGreaterThanOrEqualTo(
            MessageSizeGuard.DefaultGrainTransportBodyBytes,
            "the 142 MB body from #2897 must be refused by a router running Orleans' default "
            + "MaxMessageBodySize — if it were not, this guard would not have caught the incident "
            + "it was written for");

        MessageSizeGuard.DefaultGrainTransportBodyBytes.Should().Be(IncidentLimitBytes,
            "the fallback must be the same number the incident reported as 'max configured value', "
            + "or the guard is calibrated against a limit no deployment enforces");
    }

    /// <summary>
    /// 🚨 CALIBRATION against the REAL Orleans type, not against a comment. The fallback constant is
    /// only correct while it IS Orleans' default: set it too low and working traffic is refused, too
    /// high and the guard never fires on a default deployment. An Orleans upgrade that moves the
    /// default must fail HERE, loudly, rather than silently mis-tune the router.
    ///
    /// <para>The router prefers the live <c>IOptions&lt;SiloMessagingOptions&gt;</c> value, so this
    /// constant is the fallback only — but a fallback nothing pins is a constant that drifts.</para>
    /// </summary>
    [Fact]
    public void The_orleans_body_size_default_is_what_the_fallback_is_calibrated_against()
    {
        new SiloMessagingOptions().MaxMessageBodySize
            .Should().Be(MessageSizeGuard.DefaultGrainTransportBodyBytes,
                "the guard's fallback is Orleans' own default MaxMessageBodySize — asked of the "
                + "real options type, so an upgrade that changed it cannot pass unnoticed");
    }

    /// <summary>
    /// The preview is quoted, so a payload carrying newlines and quotes cannot forge log records or
    /// break the refusal into pieces a log watcher would parse as separate faults.
    /// </summary>
    [Fact]
    public void A_hostile_payload_cannot_forge_log_records()
    {
        var hostile = "{\"$type\":\"Evil\",\"x\":\"" + new string('\n', 8) + "\"}"
            + new string('y', TestLimitBytes);
        var delivery = new MessageDelivery<RawJson>(
            Sender, new Address("messagehub", "AgenticEngineering"), new RawJson(hostile),
            JsonSerializerOptions.Default) with
        { Id = "d-hostile" };

        var refusal = MessageSizeGuard.DescribeGrainDispatch(
            delivery, Target, Encoding.UTF8.GetByteCount(hostile), TestLimitBytes);

        refusal.Split('\n').Should().HaveCount(1,
            "the payload head is JSON-quoted, so newlines inside it cannot split one refusal into "
            + "what a log pipeline would read as several records");
        refusal.Should().Contain("\\n", "…the newlines survive, escaped, so the payload is still identifiable");
    }
}
