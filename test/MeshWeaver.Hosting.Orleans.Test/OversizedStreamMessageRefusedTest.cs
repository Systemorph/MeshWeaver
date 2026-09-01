using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Orleans.Providers.Streams.Common;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 An oversized memory-stream message is REFUSED at the producer, loudly — issue #1890.
///
/// <para><b>The incident.</b> A 37,483,597-byte delivery went onto <c>memory-7-0xE0000007</c>. The
/// publish succeeded. On the consuming side <c>MemoryPooledCache.AddToCache</c> threw
/// <c>ArgumentOutOfRangeException: Message size is too big</c> inside
/// <c>PersistentStreamPullingAgent.ReadFromQueue</c>'s retry loop — a retry that can never
/// converge, because the size is a property of the message. The message was never delivered, and
/// the only artefact was a queue id: no target, no message type, no delivery id, no sender. The
/// ticket had to guess at the producer.</para>
///
/// <para><b>The three options, and why refusal is the answer.</b> <i>Raising the cap</i> is not
/// available — Orleans hard-codes a 1 MiB block (<c>MemoryAdapterFactory</c>:
/// <c>var oneMb = 1 &lt;&lt; 20; new ObjectPool&lt;FixedSizeBuffer&gt;(() =&gt; new
/// FixedSizeBuffer(oneMb))</c>) with no configuration surface, and
/// <see cref="The_orleans_block_size_is_what_the_guard_is_calibrated_against"/> pins that against
/// the real type rather than against a comment. <i>Chunking</i> is a new wire protocol for a case
/// that has happened once and is far likelier to be a producer defect. So: do not put on the queue
/// what cannot come off it, and say exactly what was dropped — which is what turns "a queue id"
/// into "this delivery, this size, this target, this sender".</para>
///
/// <para><b>This can only convert a silent loss into a loud one.</b> The bound is Orleans' own, so
/// nothing that is delivered today is newly refused —
/// <see cref="A_delivery_that_fits_is_posted_unchanged"/> is the control.</para>
/// </summary>
public class OversizedStreamMessageRefusedTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly Address Sender = new("client", "producer-1");
    private const string Target = "portal/user-1";

    /// <summary>The incident's payload size, to the byte.</summary>
    private const int IncidentPayloadBytes = 37_483_597;

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
    /// before <c>DeliverMessage</c>), so the guard measures the same thing production does.
    /// </summary>
    private static IMessageDelivery DeliveryOf(int payloadBytes, string id = "d-oversized")
    {
        const string head = "{\"$type\":\"BigAnalyticsPayload\",\"rows\":\"";
        const string tail = "\"}";
        var filler = new string('x', Math.Max(0, payloadBytes - head.Length - tail.Length));
        var json = head + filler + tail;
        Encoding.UTF8.GetByteCount(json).Should().Be(payloadBytes, "the fixture is exact ASCII");
        return new MessageDelivery<RawJson>(
            Sender, new Address("portal", "user-1"), new RawJson(json),
            JsonSerializerOptions.Default) with
        { Id = id };
    }

    /// <summary>
    /// 🚨 THE regression test. The 37 MB delivery from the incident must NOT reach the stream, and
    /// the drop must be attributable: the log names the target, the byte count, the limit, the
    /// delivery id and the sender — every fact the Orleans-side exception could not supply.
    ///
    /// <para>Against <c>origin/main</c> the post is issued and the leg reports success, because
    /// publishing an undeliverable message to a memory stream succeeds by construction. That is the
    /// silent failure.</para>
    /// </summary>
    [Fact]
    public async Task The_37mb_delivery_from_the_incident_is_never_posted()
    {
        var posted = 0;
        var nacks = new List<(string Message, ErrorType Type)>();
        var logger = new RecordingLogger();

        await RoutingGrain.PostToStream(
                delivery: DeliveryOf(IncidentPayloadBytes),
                post: () => { posted++; return Task.CompletedTask; },
                addressPath: Target,
                sender: Sender,
                postFailureToSender: (m, t) => nacks.Add((m, t)),
                logger: logger,
                timeout: Timeout)
            .Await();

        posted.Should().Be(0,
            "a message Orleans' pooled cache provably cannot accept must never be published — "
            + "publishing it SUCCEEDS here and fails on the consuming side, where the only "
            + "identifying fact left is a queue id");

        var error = logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Error,
            "a rejected message that vanishes is a stream delivery failure nobody can trace — the "
            + "refusal must be reported").Which;
        error.Message.Should().Contain(IncidentPayloadBytes.ToString("N0"),
            "the report says HOW BIG the thing it dropped was");
        error.Message.Should().Contain("1,048,576",
            "…and what limit it was measured against, so the reader can tell 37x-over from 1%-over");
        error.Message.Should().Contain(Target, "…and where it was going");
        error.Message.Should().Contain("d-oversized", "…which delivery it was");
        error.Message.Should().Contain("BigAnalyticsPayload",
            "…and enough of the payload head to recognise WHAT was too big — the $type sits at the "
            + "front of the JSON, and identifying the producer is the question #1890 could not answer");

        // 🚨 The payload is attacker-influenced content going into a log line, and a red burst is
        // re-assembled from its indented continuation lines — an embedded newline in the preview
        // would forge a second log record and split one incident into two.
        error.Message.Split('\n').Should().HaveCount(1,
            "the refusal must stay ONE log record: the payload head is JSON-quoted, so a newline "
            + "inside it cannot break the burst apart");
    }

    /// <summary>
    /// The preview is quoted, so a payload carrying newlines and quotes cannot forge log records
    /// or break the refusal into pieces the watcher would parse as separate faults.
    /// </summary>
    [Fact]
    public void A_payload_carrying_newlines_cannot_break_the_refusal_into_two_log_records()
    {
        var hostile = "{\"$type\":\"Evil\",\"x\":\"\nfail: Forged.Category[0]\n      forged\n"
            + new string('y', MessageSizeGuard.MemoryStreamBlockBytes) + "\"}";
        var delivery = new MessageDelivery<RawJson>(
            Sender, new Address("portal", "user-1"), new RawJson(hostile),
            JsonSerializerOptions.Default) with
        { Id = "d-hostile" };

        var refusal = MessageSizeGuard.Describe(
            delivery, Target,
            Encoding.UTF8.GetByteCount(hostile), MessageSizeGuard.MemoryStreamBlockBytes);

        refusal.Split('\n').Should().HaveCount(1,
            "an unescaped newline in the payload would open what reads as a fresh `fail:` burst");
        refusal.Should().Contain("\\n", "the newline is escaped, not dropped — the head stays legible");
        refusal.Should().Contain("Evil", "…and the $type is still identifiable");
    }

    /// <summary>
    /// The sender must be told, and told terminally. Without a NACK the producer's
    /// <c>Observe(...)</c> parks until its own timeout on a message that was never going anywhere —
    /// which is precisely how this defect stayed invisible for a whole delivery cycle.
    /// </summary>
    [Fact]
    public async Task The_sender_is_nacked_instead_of_waiting_for_a_message_that_can_never_arrive()
    {
        var nacks = new List<(string Message, ErrorType Type)>();

        await RoutingGrain.PostToStream(
                delivery: DeliveryOf(IncidentPayloadBytes),
                post: () => Task.CompletedTask,
                addressPath: Target,
                sender: Sender,
                postFailureToSender: (m, t) => nacks.Add((m, t)),
                logger: new RecordingLogger(),
                timeout: Timeout)
            .Await();

        var nack = nacks.Should().ContainSingle(
            "the producer must fail fast rather than wait out a timeout on an undeliverable "
            + "message").Subject;
        nack.Type.Should().Be(ErrorType.Rejected,
            "this is an explicit refusal by the router, not a processing fault — the sender can "
            + "act on the difference");
        nack.Message.Should().Contain("37,483,597");
        nack.Message.Should().Contain(Target);
    }

    /// <summary>
    /// 🚨 THE CONTROL that makes the fix safe: the bound is Orleans' own, so a delivery that fits
    /// is posted exactly as before, with no NACK and no log. A guard that also refused working
    /// traffic would trade one outage for a bigger one.
    /// </summary>
    [Fact]
    public async Task A_delivery_that_fits_is_posted_unchanged()
    {
        var posted = 0;
        var nacks = new List<(string Message, ErrorType Type)>();
        var logger = new RecordingLogger();

        await RoutingGrain.PostToStream(
                delivery: DeliveryOf(MessageSizeGuard.MemoryStreamBlockBytes - 1, "d-fits"),
                post: () => { posted++; return Task.CompletedTask; },
                addressPath: Target,
                sender: Sender,
                postFailureToSender: (m, t) => nacks.Add((m, t)),
                logger: logger,
                timeout: Timeout)
            .Await();

        posted.Should().Be(1, "everything under Orleans' block size is delivered exactly as before");
        nacks.Should().BeEmpty("a delivered post must never NACK the sender");
        logger.Records.Should().BeEmpty("nor say anything about it");
    }

    /// <summary>
    /// 🚨 The NACK must not BE the thing it reports. <see cref="DeliveryFailure"/> embeds the
    /// ORIGINAL delivery and travels the SAME memory stream, so a failure report about a 37 MB
    /// message is itself a 37 MB message and dies at exactly the wall it is describing — leaving
    /// the producer with neither the message nor the report.
    ///
    /// <para>This is a second-order defect of the same shape, and it is the reason a "just NACK it"
    /// fix is not enough on its own.</para>
    /// </summary>
    [Fact]
    public void The_failure_report_about_an_oversized_delivery_is_itself_deliverable()
    {
        var oversized = DeliveryOf(IncidentPayloadBytes);

        var echoed = MessageSizeGuard.WithoutOversizedPayload(oversized);

        MessageSizeGuard.IsOversized(
                echoed, MessageSizeGuard.MemoryStreamBlockBytes, out var echoedBytes)
            .Should().BeFalse(
                "the DeliveryFailure that carries this echo goes back over the same memory stream — "
                + "if the echo is still oversized, the report about the lost message is lost the "
                + "same way and the producer learns nothing at all");
        echoedBytes.Should().Be(0, "the fast path proved it fits without measuring");

        // Bounded before asserting: under a neutered guard this string is the 37 MB payload, and a
        // failed assertion prints its subject.
        var replacement = ((RawJson)echoed.Message).Content;
        replacement.Length.Should().BeLessThan(4096,
            "the echo the DeliveryFailure carries must be a description, not the payload");
        replacement.Should().Contain("payloadOmitted",
            "a stripped echo must say it was stripped, or it reads as an empty message");
        replacement.Should().Contain("37483597", "…and how big the thing it replaced was");
        replacement.Should().Contain("BigAnalyticsPayload",
            "…and keep enough head to identify it");

        echoed.Id.Should().Be(oversized.Id,
            "the envelope is untouched — the sender correlates a DeliveryFailure on RequestId, and "
            + "stripping the payload must not disturb that");

        // …and a report about an ordinary failure is unchanged: stripping happens only when
        // carrying the payload is what would lose the report.
        var small = DeliveryOf(1024, "d-small");
        MessageSizeGuard.WithoutOversizedPayload(small).Should().BeSameAs(small);
    }

    /// <summary>
    /// 🚨 CALIBRATION. The guard's constant is only meaningful if it IS Orleans' block size — set
    /// it too low and working traffic is refused; too high and the guard never fires. Rather than
    /// trust the comment in <c>MemoryAdapterFactory</c>, this asks the real
    /// <see cref="FixedSizeBuffer"/> Orleans allocates: a block of exactly
    /// <see cref="MessageSizeGuard.MemoryStreamBlockBytes"/> holds a segment of that size and
    /// not one byte more. An Orleans upgrade that moved the block size therefore fails HERE, rather
    /// than silently mis-tuning the guard in production.
    /// </summary>
    [Fact]
    public void The_orleans_block_size_is_what_the_guard_is_calibrated_against()
    {
        var block = new FixedSizeBuffer(MessageSizeGuard.MemoryStreamBlockBytes);

        block.TryGetSegment(MessageSizeGuard.MemoryStreamBlockBytes + 1, out _)
            .Should().BeFalse(
                "Orleans allocates one fixed block per message and a cached message must fit it "
                + "whole — this is the ceiling MemoryPooledCache.AddToCache reports as 'Message "
                + "size is too big', and the guard's constant must be exactly it");

        new FixedSizeBuffer(MessageSizeGuard.MemoryStreamBlockBytes)
            .TryGetSegment(MessageSizeGuard.MemoryStreamBlockBytes, out _)
            .Should().BeTrue(
                "a full block is the most a clean buffer can hold — so refusing AT the limit (not "
                + "just above it) is the correct side to err on: the on-queue form also carries a "
                + "stream id and metadata in that same block");
    }

    /// <summary>
    /// The measurement itself: exact where it matters, and free where it does not. The fast path
    /// is a sound proof, not an approximation — a UTF-16 char never contributes more than 3 UTF-8
    /// bytes — so a multi-byte payload is still measured correctly rather than waved through.
    /// </summary>
    [Fact]
    public void The_size_check_is_exact_for_multibyte_payloads_and_free_for_small_ones()
    {
        // 400k characters of a 3-byte-in-UTF-8 rune: 1.2 MB on the wire, well over the block,
        // while its UTF-16 LENGTH (400,000) is comfortably under it. A char-count check would
        // have waved this through.
        var multiByte = new MessageDelivery<RawJson>(
            Sender, new Address("portal", "user-1"),
            new RawJson(string.Concat(Enumerable.Repeat("€", 400_000))),
            JsonSerializerOptions.Default);

        MessageSizeGuard.IsOversized(
                multiByte, MessageSizeGuard.MemoryStreamBlockBytes, out var bytes)
            .Should().BeTrue("1.2 MB of UTF-8 is over the block regardless of how few chars it took");
        bytes.Should().Be(1_200_000);

        // A payload that is not RawJson is never measured — by the time a delivery reaches the
        // router it has been packaged, so anything else would mean serialising twice on the hot
        // path to answer a question that is almost always "no".
        MessageSizeGuard.IsOversized(
                new MessageDelivery<string>(Sender, new Address("portal", "user-1"),
                    new string('x', 4_000_000), JsonSerializerOptions.Default),
                MessageSizeGuard.MemoryStreamBlockBytes, out _)
            .Should().BeFalse("an unpackaged payload is not the routed shape and is not guessed at");
    }
}
