using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>A NACK whose sender was RELEASED has nobody to reach: it is dropped, once, at Debug —
/// issue #7001.</b>
///
/// <para>The router reports a delivery's failure back to its sender. When that sender is a
/// pod-process hub (a Blazor circuit's <c>portal/…</c> hub) whose registration was disposed, the
/// directed call is answered by the owner's RELEASE tombstone
/// (<see cref="PodHubNotHereException.Released"/>). Production logged every such NACK at Error with
/// the sentence "the sender will wait out its own request budget", and that sentence was false:
/// the tombstone is stamped only when the owner disposed its registration with no live route left
/// (#5136), a closed circuit id is never reused, and a hub that comes back claims afresh. So there
/// is no sender to wait, and every closed tab with a read in flight raised an Error.</para>
///
/// <para><b>The other side is what makes this safe.</b> An UNRELEASED refusal is the stranded-claim
/// shape (#2938): the sender may be alive and really is burning its budget. That NACK must stay on
/// the loud path. So the test pins both, plus the wrapped shape Orleans actually delivers, plus the
/// absence of any loop: N NACKs to one released sender cost N Debug lines and nothing else.</para>
///
/// <para><b>The decision is the subject.</b> <see cref="RoutingGrain.AnswerNackToPodHubSenderRefused"/>
/// takes the loud report as a thunk, so "was it reported as undeliverable" is asserted directly.
/// The real grain's tombstone itself is pinned on the two-silo cluster by
/// <see cref="PodHubStaleReleaseTest.AReleaseWithNoLiveRoute_StillTombstones"/>.</para>
///
/// <para><b>Fails on unfixed code:</b> the method does not exist, and the call site sent every
/// refusal, released or not, to the Error line.</para>
/// </summary>
public class ANackToAReleasedSenderIsDroppedTest
{
    private static readonly Address ClosedCircuit = new("portal", "closed-circuit");

    private static IMessageDelivery Delivery(string id) =>
        new MessageDelivery<RawJson>(
            ClosedCircuit, new Address("Event/Gone/node"),
            new RawJson("{\"$type\":\"GetDataRequest\"}"),
            JsonSerializerOptions.Default) with
        { Id = id };

    private static PodHubNotHereException Refusal(bool released) =>
        new(ClosedCircuit.ToString(), "10.0.0.2:11111@1", released);

    [Fact]
    public async Task AReleasedSender_IsDroppedAtDebug_AndNeverReportedUndeliverable()
    {
        var logger = new RecordingLogger();
        var loud = ImmutableList<Exception>.Empty;

        await RoutingGrain.AnswerNackToPodHubSenderRefused(
                Refusal(released: true), Delivery("d-7001"), ErrorType.NotFound,
                "No node found at 'Event/Gone/node'.",
                ex => { loud = loud.Add(ex); return Observable.Return(Unit.Default); },
                logger)
            .Should().Within(TimeSpan.FromSeconds(5)).Emit(
                "the drop completes the NACK leg on the spot, so its routing slot is released");

        loud.Should().BeEmpty(
            "the owner released the address: no one is waiting, so 'the sender will wait out its own "
            + "request budget' would be false, and an Error line for it is noise from every closed tab");
        var line = logger.Records.Should().ContainSingle(
            "the drop is recorded exactly once, so the evidence is kept without shipping a line per tab").Subject;
        line.Level.Should().Be(LogLevel.Debug);
        line.Message.Should().Contain("portal/closed-circuit").And.Contain("RELEASED").And.Contain("d-7001");
    }

    /// <summary>
    /// Orleans delivers a grain's exception WRAPPED, so the release must be read through the inner
    /// chain. A test on the bare exception alone would pass while production still hit the Error line.
    /// </summary>
    [Fact]
    public async Task AWrappedRelease_IsRecognised()
    {
        var logger = new RecordingLogger();
        var loud = 0;

        await RoutingGrain.AnswerNackToPodHubSenderRefused(
                new OrleansException("grain call failed", Refusal(released: true)),
                Delivery("d-wrapped"), ErrorType.NotFound, "gone",
                _ => { loud++; return Observable.Return(Unit.Default); },
                logger)
            .Should().Within(TimeSpan.FromSeconds(5)).Emit();

        loud.Should().Be(0);
        logger.Records.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL. An UNRELEASED refusal can be a live sender whose claim was stranded
    /// (#2938). Its NACK must reach the loud, windowed report, never the drop.
    /// </summary>
    [Fact]
    public async Task AnUnreleasedRefusal_StaysOnTheLoudPath()
    {
        var logger = new RecordingLogger();
        var loud = ImmutableList<Exception>.Empty;
        var refusal = Refusal(released: false);

        await RoutingGrain.AnswerNackToPodHubSenderRefused(
                refusal, Delivery("d-stranded"), ErrorType.NotFound, "gone",
                ex => { loud = loud.Add(ex); return Observable.Return(Unit.Default); },
                logger)
            .Should().Within(TimeSpan.FromSeconds(5)).Emit();

        loud.Should().ContainSingle(
            "a sender whose claim merely is not held may be alive and waiting; dropping its NACK quietly "
            + "would hide the stranded-claim fault").Which.Should().BeSameAs(refusal);
        logger.Records.Should().BeEmpty("the loud path does its own logging");
    }

    /// <summary>
    /// NEGATIVE CONTROL, other fault: a call that failed for any reason other than a pod-hub refusal
    /// (the owning silo threw, went away mid-call) is reported as undeliverable, as before.
    /// </summary>
    [Fact]
    public async Task AnyOtherFault_StaysOnTheLoudPath()
    {
        var loud = 0;
        await RoutingGrain.AnswerNackToPodHubSenderRefused(
                new TimeoutException("silo went away"), Delivery("d-fault"), ErrorType.NotFound, "gone",
                _ => { loud++; return Observable.Return(Unit.Default); },
                new RecordingLogger())
            .Should().Within(TimeSpan.FromSeconds(5)).Emit();

        loud.Should().Be(1);
    }

    /// <summary>
    /// NO STORM. Many NACKs to one released sender cost one Debug line each and nothing else:
    /// no retry, no Error, and nothing posted anywhere.
    /// </summary>
    [Fact]
    public async Task ManyNacksToOneReleasedSender_CostOneDebugLineEach()
    {
        var logger = new RecordingLogger();
        var loud = 0;

        for (var i = 0; i < 50; i++)
            await RoutingGrain.AnswerNackToPodHubSenderRefused(
                    Refusal(released: true), Delivery($"d-{i}"), ErrorType.NotFound, "gone",
                    _ => { loud++; return Observable.Return(Unit.Default); },
                    logger)
                .Should().Within(TimeSpan.FromSeconds(5)).Emit();

        loud.Should().Be(0);
        logger.Records.Should().HaveCount(50);
        logger.Records.Should().OnlyContain(r => r.Level == LogLevel.Debug);
    }

    private sealed class RecordingLogger : ILogger
    {
        private ImmutableList<(LogLevel Level, string Message)> records = [];
        public ImmutableList<(LogLevel Level, string Message)> Records => records;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => ImmutableInterlocked.Update(ref records, r => r.Add((logLevel, formatter(state, exception))));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
