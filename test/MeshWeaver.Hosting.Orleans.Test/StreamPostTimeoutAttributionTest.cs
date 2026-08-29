using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>Issue #2322 — the router must not report its OWN budget for a failure the TRANSPORT
/// reported.</b>
///
/// <para>The memory-stream leg's only await is a grain call to
/// <c>IMemoryStreamQueueGrain.Enqueue</c>, which Orleans bounds at its own 30 s
/// <c>ResponseTimeout</c>. <c>RoutingGrain.PostToStreamCore</c> wraps that in a 60 s Rx guard using
/// the bare <c>Observable.Timeout(dueTime, scheduler)</c> overload — which raises the same plain
/// <see cref="TimeoutException"/> the transport does. So the catch arm could not tell the two apart
/// and printed the GUARD's value for both.</para>
///
/// <para>Production consequently logged, twice, for a wedged <c>memorystreamqueue</c>
/// activation:</para>
/// <code>
/// [ROUTE] Stream-routed forward to cache/… did not complete within 00:01:00 …
/// System.TimeoutException: Response did not arrive on time in 00:00:30 for message: Request
///   […] Orleans.Providers.IMemoryStreamQueueGrain.Enqueue(…)
/// </code>
///
/// <para>Both numbers are in the same record and they disagree. The leg died at ~30 s and reported
/// PROMPTLY — <c>post().ToObservable()</c> is the Task overload, so a faulted post propagates the
/// instant the task faults and the Rx guard never fired at all. The sentence is simply false, and
/// the same wrong number rides to the sender in the <c>DeliveryFailure</c>'s reason string. That is
/// what sent the original triage looking for a double publish and "30 s of avoidable latency",
/// neither of which exists.</para>
///
/// <para>The cure is a guard exception with a type of its own, so "our bound" and "the transport's
/// bound" are distinguishable by construction rather than by hope. It still derives from
/// <see cref="TimeoutException"/>, so every classifier above this leg reads it exactly as before —
/// which <see cref="TheGuardsExceptionIsStillATimeoutException"/> pins.</para>
///
/// <para>Deterministic — <see cref="TestScheduler"/> for the guard, no cluster, no wall clock.</para>
/// </summary>
public class StreamPostTimeoutAttributionTest
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(60);
    private static readonly Address Sender = new("client", "sender-1");
    private const string Address = "cache/cLhEcQcDAUa-W9zPmBOQWw";

    /// <summary>Verbatim from the #2322 production log — Orleans' own 30 s response timeout.</summary>
    private const string OrleansEnqueueTimeoutText =
        "Response did not arrive on time in 00:00:30 for message: Request "
        + "[S10.244.4.207:11111:146709268 sys.client/hosted-10.244.4.207:11111@146709268]->"
        + "[S10.244.2.28:11111:146709309 memorystreamqueue/be8eb06fb48875590000000000000000] "
        + "Orleans.Providers.IMemoryStreamQueueGrain.Enqueue(Orleans.Providers.MemoryMessageData) "
        + "#59B543F946E76231.";

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
            Sender, new Address("cache", "cLhEcQcDAUa-W9zPmBOQWw"), new RawJson("{\"$type\":\"Ping\"}"),
            JsonSerializerOptions.Default) with
        { Id = id };

    /// <summary>
    /// 🚨 THE REGRESSION. The transport's OWN timeout must be reported as the transport's — with its
    /// message, which names the real budget, the wedged queue-grain activation and the correlation
    /// id. Pre-fix, both the log line and the sender's NACK claimed the router's 60 s guard.
    /// </summary>
    [Fact]
    public async Task TransportTimeout_IsNotReportedAsTheRoutersOwnBudget()
    {
        var logger = new RecordingLogger();
        var nacks = new List<(string Message, ErrorType Type)>();

        await RoutingGrain.PostToStream(
                delivery: Delivery("d-2322"),
                post: () => Task.FromException(new TimeoutException(OrleansEnqueueTimeoutText)),
                addressPath: Address,
                sender: Sender,
                postFailureToSender: (m, t) => nacks.Add((m, t)),
                logger: logger,
                timeout: Guard)
            .ToTask();

        var line = logger.Records.Should().ContainSingle().Subject;
        line.Message.Should().NotContain("did not complete within 00:01:00",
            "the leg did NOT run for 60 s — it faulted promptly at Orleans' own 30 s bound, and "
            + "post().ToObservable() propagates a faulted task the instant it faults. Printing the "
            + "guard's value here is what sent triage looking for a double publish (#2322)");
        line.Message.Should().Contain("00:00:30",
            "the transport's own message names the real budget, the wedged memorystreamqueue "
            + "activation and the correlation id — that IS the diagnostic");

        var nack = nacks.Should().ContainSingle().Subject;
        nack.Type.Should().Be(ErrorType.Failed, "the classification is unchanged");
        nack.Message.Should().NotContain("00:01:00",
            "the sender's DeliveryFailure carried the same wrong number as the log line");
        nack.Message.Should().Contain("00:00:30");
    }

    /// <summary>
    /// The other direction, unchanged: when the router's OWN guard fires — the post neither
    /// completes nor faults — it must still say so, with ITS budget. This is the #1028 contract and
    /// it must not be traded away while fixing the attribution.
    /// </summary>
    [Fact]
    public void GuardTimeout_StillReportsTheRoutersBudget_AndStillNacks()
    {
        var never = new TaskCompletionSource();
        var logger = new RecordingLogger();
        var nacks = new List<(string Message, ErrorType Type)>();
        var scheduler = new TestScheduler();
        var terminated = false;

        RoutingGrain.PostToStream(
                delivery: Delivery("d-guard"),
                post: () => never.Task,
                addressPath: Address,
                sender: Sender,
                postFailureToSender: (m, t) => nacks.Add((m, t)),
                logger: logger,
                timeout: Guard,
                scheduler: scheduler)
            .Subscribe(_ => { }, () => terminated = true);

        scheduler.AdvanceBy(Guard.Ticks + 1);

        terminated.Should().BeTrue("the leg must always terminate — #1028");
        logger.Records.Should().ContainSingle().Which
            .Message.Should().Contain("00:01:00",
                "when the ROUTER's bound is what fired, the router's budget is the true number");
        nacks.Should().ContainSingle().Which
            .Message.Should().Contain("did not complete within 00:01:00");
    }

    /// <summary>
    /// 🚨 The guard's exception must stay a <see cref="TimeoutException"/>. Every classifier above
    /// this leg matches that type — narrowing it would silently change how a never-completing post
    /// is retried and reported, which is a behaviour change hiding inside a diagnostic fix.
    /// </summary>
    [Fact]
    public void TheGuardsExceptionIsStillATimeoutException()
    {
        var ex = new StreamPostGuardTimeoutException(Address, Guard);

        ex.Should().BeAssignableTo<TimeoutException>();
        RoutingGrain.IsTransientFailure(ex).Should().BeTrue(
            "the router's transient classifier matched a guard timeout before this change and must "
            + "still match it");
        RoutingGrain.ClassifyDeliveryException(ex).Should().Be(ErrorType.Failed,
            "a bare TimeoutException stays TERMINAL by design — a target that did not answer across "
            + "the whole budget is plausibly wedged, and telling a consumer to resubscribe is the "
            + "2026-06-08 storm shape");
        ex.Budget.Should().Be(Guard);
        ex.AddressPath.Should().Be(Address);
    }
}
