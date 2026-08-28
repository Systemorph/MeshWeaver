using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 The dead-target delivery storms, at the refusal seam — issues #2426 / #2546.
///
/// <para><b>The incidents.</b> memex-cloud logged 20,718 <c>fail: RoutingGrain</c> lines in 3 h
/// (~2/s) for fan-outs to dead <c>portal/{circuit}</c> and <c>cache/…</c> addresses (#2426), and
/// later ~36/s for three never-activated <c>node/…</c> addresses (#2546) — the SAME line, per
/// delivery, for hours, for a condition already known. The refusal itself is CORRECT (a publish to
/// a subscriber-less stream succeeds and discards, so it must be refused and the sender NACKed);
/// the defect at this seam is only that a KNOWN-dead address bought a full Error line per
/// delivery.</para>
///
/// <para><b>What this test pins</b> — the brief's three properties, driven deterministically
/// against the exact internal seam the router runs:
/// (a) <b>bounded attempts</b>: a refusal is terminal per delivery — one emission, completes, no
/// retry machinery anywhere on the path; (b) <b>a terminal answer to the requester</b>: EVERY
/// refused delivery NACKs its sender <see cref="ErrorType.NotFound"/>, windowed or not — the NACK
/// is also the owner-side eviction signal, so suppressing it would re-open the leak; (c) <b>no
/// per-delivery error spam</b>: the first refusal of an address logs the full Error line, repeats
/// inside the window log at Debug and are COUNTED into the next full line, so the storm's volume
/// stays on the record while Loki stops paying per delivery.</para>
/// </summary>
public class DeadTargetRefusalWindowTest
{
    private static readonly Address Sender = new("client", "sender-1");

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

    private static IMessageDelivery Delivery(string id) =>
        new MessageDelivery<RawJson>(
            Sender, new Address("portal", "dead-circuit"), new RawJson("{\"$type\":\"DataChangedEvent\"}"),
            JsonSerializerOptions.Default) with
        { Id = id };

    /// <summary>
    /// The storm, replayed: 100 deliveries to one dead address. Every one is refused terminally
    /// and NACKed NotFound; exactly ONE Error line ships, the other 99 land at Debug. Against the
    /// unwindowed code this fails with 100 Error lines — the measured #2546 shape (64,627 in 30
    /// minutes) in miniature.
    /// </summary>
    [Fact]
    public async Task A_hundred_refusals_of_one_dead_address_ship_one_error_line_and_a_hundred_nacks()
    {
        var logger = new RecordingLogger();
        var refusalLog = new DeadTargetRefusalLog(TimeSpan.FromSeconds(60));
        var nacks = new List<(string Message, ErrorType Type)>();
        const string address = "node/eapdieia6urs9qd0hbbej"; // the #2546 storm's own first victim

        for (var i = 0; i < 100; i++)
            await RoutingGrain.RefuseNoSubscriber(
                    Delivery($"d{i}"), address, (m, t) => nacks.Add((m, t)), logger, refusalLog)
                .ToTask();

        // (b) EVERY sender got its terminal answer — the NACK is never windowed. It is both the
        // requester's fast OnError and the owner-side eviction signal (TargetUnserved).
        nacks.Should().HaveCount(100,
            "every refused delivery must NACK its sender — the window bounds the LOG, never the answer");
        nacks.Should().OnlyContain(n => n.Type == ErrorType.NotFound,
            "'gone' must stay distinguishable from 'broke'");

        // (c) ONE full Error line for the whole storm; the volume is still on the record at Debug.
        logger.Records.Count(r => r.Level == LogLevel.Error).Should().Be(1,
            "a KNOWN-dead address must not buy an Error line per delivery — that is the 20,718-line "
            + "Loki burn of #2426");
        logger.Records.Count(r => r.Level == LogLevel.Debug).Should().Be(99,
            "suppressed refusals still leave per-delivery evidence at Debug");
        logger.Records.Single(r => r.Level == LogLevel.Error).Message.Should().Contain(address);
    }

    /// <summary>
    /// The suppressed volume is not lost: once the window elapses, the NEXT full Error line
    /// carries the count of everything it absorbed — so a Loki reader can still size the storm
    /// from Error-level lines alone.
    /// </summary>
    [Fact]
    public async Task The_next_full_line_after_the_window_carries_the_suppressed_count()
    {
        var logger = new RecordingLogger();
        var clock = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        var refusalLog = new DeadTargetRefusalLog(TimeSpan.FromSeconds(60), () => clock);
        var nacks = new List<(string Message, ErrorType Type)>();
        const string address = "portal/dead-circuit";

        for (var i = 0; i < 5; i++)
            await RoutingGrain.RefuseNoSubscriber(
                    Delivery($"w{i}"), address, (m, t) => nacks.Add((m, t)), logger, refusalLog)
                .ToTask();
        clock = clock.AddSeconds(61);
        await RoutingGrain.RefuseNoSubscriber(
                Delivery("w5"), address, (m, t) => nacks.Add((m, t)), logger, refusalLog)
            .ToTask();

        nacks.Should().HaveCount(6);
        var errors = logger.Records.Where(r => r.Level == LogLevel.Error).ToList();
        errors.Should().HaveCount(2, "one full line per window");
        errors[0].Message.Should().Contain("0 earlier refusal(s)");
        errors[1].Message.Should().Contain("4 earlier refusal(s)",
            "the reopened window's full line must fold in what was suppressed, or the storm's "
            + "true volume disappears from the Error channel");
    }

    /// <summary>
    /// (a) Bounded attempts: one refusal is one emission that COMPLETES — there is no retry, no
    /// timer and no watchdog anywhere on this path, so the refusal can never become its own storm.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_terminal_per_delivery()
    {
        var completed = false;
        var emissions = 0;
        RoutingGrain.RefuseNoSubscriber(
                Delivery("t1"), "portal/dead", (_, _) => { }, new RecordingLogger(),
                new DeadTargetRefusalLog(TimeSpan.FromSeconds(60)))
            .Subscribe(_ => emissions++, () => completed = true);
        await Task.Yield();
        emissions.Should().Be(1);
        completed.Should().BeTrue("the leg must terminate so the routing pool slot is released");
    }

    // ---- DeadTargetRefusalLog mechanics, with a fake clock ------------------------------------

    [Fact]
    public void The_first_refusal_of_an_address_always_reports()
    {
        var log = new DeadTargetRefusalLog(TimeSpan.FromSeconds(60), () => new DateTime(1, DateTimeKind.Utc));
        log.ShouldReport("portal/a", out var suppressed).Should().BeTrue(
            "the first evidence of a dead address must ship at Error, whatever the clock reads — "
            + "including a test clock at tick 0, which a sentinel-tick implementation gets wrong");
        suppressed.Should().Be(0);
    }

    [Fact]
    public void Repeats_inside_the_window_are_suppressed_and_counted_per_address()
    {
        var clock = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        var log = new DeadTargetRefusalLog(TimeSpan.FromSeconds(60), () => clock);

        log.ShouldReport("portal/a", out _).Should().BeTrue();
        log.ShouldReport("portal/a", out _).Should().BeFalse();
        // A DIFFERENT dead address opens its own window — per-address, never global.
        log.ShouldReport("portal/b", out _).Should().BeTrue();
        clock = clock.AddSeconds(59);
        log.ShouldReport("portal/a", out _).Should().BeFalse("still inside the window");
        clock = clock.AddSeconds(2);
        log.ShouldReport("portal/a", out var suppressed).Should().BeTrue("the window elapsed");
        suppressed.Should().Be(2, "both suppressed refusals fold into the reopened line");
    }

    /// <summary>
    /// A delivered address closes its window: the NEXT death earns a fresh full line immediately,
    /// so the window can never hide a NEW incident behind an old one's suppression.
    /// </summary>
    [Fact]
    public void A_live_answer_clears_the_window()
    {
        var clock = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        var log = new DeadTargetRefusalLog(TimeSpan.FromSeconds(60), () => clock);

        log.ShouldReport("portal/a", out _).Should().BeTrue();
        log.ShouldReport("portal/a", out _).Should().BeFalse();
        log.Clear("portal/a"); // the subscriber probe answered "alive" — e.g. the circuit came back
        clock = clock.AddSeconds(1);
        log.ShouldReport("portal/a", out var suppressed).Should().BeTrue(
            "an address that delivered and died AGAIN is a new incident, not a windowed repeat");
        suppressed.Should().Be(0, "the suppressed count belongs to the closed incident");
    }
}
