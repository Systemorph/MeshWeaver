using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// 🚨 <b>#2255 — a render that raced a hub's DEACTIVATION is TRANSIENT, not a failure.</b>
///
/// <para><b>What production reported.</b> A layout area's reduce pipeline touched a per-node hub
/// that was mid-teardown, so <c>SynchronizationStream</c>'s constructor refused to exist:</para>
/// <code>
/// fail: MeshWeaver.Layout.Composition.LayoutAreaHost[0]
///       Rendering failed for area Preview
///       System.Reflection.TargetInvocationException: …
///        ---&gt; MeshWeaver.Messaging.HubDisposingException: Hub Posts/DigitalTwinWrittenTrail is
///             shutting down — cannot create '/data/'postAdvanceStatus''. The address may
///             reactivate (recycle / restart); retry to get the authoritative answer.
/// </code>
///
/// <para><b>Why it was mis-reported.</b> The exception's own contract says the condition is
/// recoverable, and hub deactivation is ordinary grain lifecycle — any area whose reduce path
/// crosses a hub being collected can lose that race. But the render treated it like any other
/// fault: <c>LogError</c> (the level the red-log filer turns into a ticket — this very issue was
/// auto-filed) plus the generic ⚠ error panel carrying the raw framework text. A temporary
/// condition presented as a permanent one.</para>
///
/// <para>It also could not have been caught by wording: <c>SynchronizationStream.Reduce</c> is
/// invoked REFLECTIVELY, so the fault arrives wrapped in a <see cref="TargetInvocationException"/>
/// whose own message is the useless "Exception has been thrown by the target of an invocation".
/// Every text-matching predicate in <c>AreaErrorClassifier</c> looked straight past it. The fixture
/// below throws that exact wrapping rather than a bare <c>HubDisposingException</c>, because
/// unwrapping is half of what is under test.</para>
///
/// <para><b>What the fix is NOT.</b> No retry loop, no widened timeout, no resubscribe watchdog —
/// the render is running ON the hub that is going away, so this host cannot outlive the condition
/// it is reporting, and a server-side retry would be the resubscribe storm this codebase forbids.
/// The honest answer is a NAMED transient frame: <c>AreaFrameClassifier.HubRecyclingId</c>, which
/// is part of <c>IsTransientFrame</c> ("keep waiting, this is not the answer"). What actually
/// recovers the area is the client's own subscription re-attaching to the reactivated address.</para>
/// </summary>
public class HubDisposalRenderTransientTest : HubTestBase
{
    private const string RecyclingView = nameof(RecyclingView);
    private const string BrokenView = nameof(BrokenView);

    /// <summary>The hub named in the production capture.</summary>
    private const string DisposingHub = "Posts/DigitalTwinWrittenTrail";

    private const string EngineeringFault = "BOOM_an_ordinary_defect";

    private readonly RenderFailureCapture capture = new();

    public HubDisposalRenderTransientTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(capture));
    }

    /// <summary>
    /// The production fault, reconstructed exactly: the typed refusal, wrapped by the reflective
    /// <c>Reduce</c> hop. <c>EntityReference</c>'s <c>ToString</c> is its own JSON pointer, which
    /// already quotes the id — the shape that produced the unbalanced
    /// <c>'/data/'postAdvanceStatus''</c> in the capture.
    /// </summary>
    private static Exception WrappedDisposal() =>
        new TargetInvocationException(
            new HubDisposingException(
                new Address(DisposingHub),
                new EntityReference("data", "postAdvanceStatus")));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(RecyclingView, (LayoutAreaHost _, RenderingContext _)
                    => Observable.Throw<UiControl?>(WrappedDisposal()))
                .WithView(BrokenView, (LayoutAreaHost _, RenderingContext _)
                    => Observable.Throw<UiControl?>(new InvalidOperationException(EngineeringFault))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient(d => d);

    /// <summary>
    /// The area serves the NAMED TRANSIENT frame — not the generic error panel — and the render
    /// leaves nothing at Warning or above behind.
    ///
    /// <para>Waiting for the rendered control first is the barrier, not a sleep: the host writes
    /// its log line BEFORE it renders the frame, so a frame that has reached the client proves
    /// whatever record the host was going to emit has already been emitted.</para>
    /// </summary>
    [HubFact]
    public async Task ARenderThatRacedAHubDisposal_ServesTheTransientFrame_AndDoesNotPage()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(RecyclingView));

        var control = await stream.GetControlStream(RecyclingView)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);

        AreaFrameClassifier.IsHubRecycling(control as UiControl).Should().BeTrue(
            "the frame must carry the well-known transient id so a consumer can tell 'this comes "
            + "back' from the three states it already distinguishes — the id round-trips through "
            + "the sync stream, the localized prose does not");
        AreaFrameClassifier.IsTransientFrame(control as UiControl).Should().BeTrue(
            "a waiter must keep waiting: the address reactivates and the real content renders");

        var text = (control as MarkdownControl)?.Markdown?.ToString() ?? string.Empty;
        Output.WriteLine($"frame: {text}");
        text.Should().NotContain("shutting down",
            "the raw framework diagnostic is internal — it must not reach an end user");
        text.Should().NotContain("target of an invocation",
            "least of all the reflective wrapper's own message, which says nothing at all");

        Records().Should().BeEmpty(
            "a hub deactivating is routine grain lifecycle. At Error the red-log filer turns it "
            + "into a production incident (this issue was filed exactly that way) and Warning "
            + "still pages an error dashboard — SynchronizationStream.OnError already classifies "
            + "this same exception as benign teardown at Debug");
    }

    /// <summary>
    /// The guard that keeps the fix honest: everything ELSE must still report as a failure. A
    /// downgrade that swallowed ordinary faults would be a far worse defect than the one being
    /// fixed — and this is also what makes the empty-Records assertion above non-vacuous, since a
    /// capture that recorded nothing at all would fail here.
    /// </summary>
    [HubFact]
    public async Task AnOrdinaryRenderFault_StillPages_AndServesTheHardErrorFrame()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(BrokenView));

        var control = await stream.GetControlStream(BrokenView)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);

        AreaFrameClassifier.IsHubRecycling(control as UiControl).Should().BeFalse(
            "an engineering fault is not a recycle — nothing is coming back on its own");
        AreaFrameClassifier.IsTransientFrame(control as UiControl).Should().BeFalse(
            "a waiter must NOT keep waiting on a defect");

        var record = Records().Should().ContainSingle().Subject;
        record.Area.Should().Be(BrokenView);
        record.Level.Should().Be(LogLevel.Error,
            "only the transient teardown race is downgraded; a view generator that threw is a "
            + "defect and must keep paging");
    }

    /// <summary>
    /// The unwrap itself, stated as an executable fact. This is the half no wording-based predicate
    /// could ever have covered, and the half that decides both behaviours above.
    /// </summary>
    [Fact]
    public void TheClassifier_SeesThroughTheReflectiveWrapper_AndOnlyMatchesADisposal()
    {
        AreaErrorClassifier.IsHubDisposalRace(WrappedDisposal()).Should().BeTrue(
            "the reduce pipeline is invoked reflectively, so the typed refusal never arrives bare");
        AreaErrorClassifier.IsHubDisposalRace(
                new HubDisposingException(new Address(DisposingHub), "anything"))
            .Should().BeTrue("…and it must still match when it does arrive bare");

        AreaErrorClassifier.IsHubDisposalRace(new InvalidOperationException(EngineeringFault))
            .Should().BeFalse("an ordinary defect is not a teardown race");
        AreaErrorClassifier.IsHubDisposalRace(new TargetInvocationException(
                new InvalidOperationException(EngineeringFault)))
            .Should().BeFalse("nor is a WRAPPED ordinary defect — the wrapper is not the signal");
        AreaErrorClassifier.IsHubDisposalRace(null).Should().BeFalse();
    }

    /// <summary>
    /// The cosmetic half of #2255: the message quoted a reference that already quotes itself,
    /// rendering <c>cannot create '/data/'postAdvanceStatus''</c>. A reference's <c>ToString</c> IS
    /// its JSON pointer; a plain name still gets quoted.
    /// </summary>
    [Fact]
    public void TheDisposalMessage_DoesNotQuoteAnAlreadyQuotedReference()
    {
        var reference = new HubDisposingException(
            new Address(DisposingHub), new EntityReference("data", "postAdvanceStatus")).Message;
        Output.WriteLine(reference);
        reference.Should().Contain("cannot create /data/'postAdvanceStatus'.");
        // The reference self-delimits; a second pair of quotes only nests.
        reference.Should().NotContain("''");

        // A plain name carries no quotes of its own, so it still gets them.
        new HubDisposingException(new Address(DisposingHub), "MeshNode")
            .Message.Should().Contain("cannot create 'MeshNode'.");
    }

    private RenderFailureRecord[] Records()
    {
        var all = capture.Records;
        foreach (var record in all)
            Output.WriteLine($"LayoutAreaHost captured: {record}");
        return all;
    }

    private sealed record RenderFailureRecord(LogLevel Level, string? Area, Exception? Exception);

    /// <summary>
    /// Reads <c>LayoutAreaHost</c>'s render-failure report out of the logging pipeline, at Warning
    /// and above — i.e. exactly the levels that reach an error dashboard. Structured state, never
    /// the formatted prose.
    /// </summary>
    private sealed class RenderFailureCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<RenderFailureRecord> records = new();

        internal RenderFailureRecord[] Records => records.ToArray();

        public ILogger CreateLogger(string categoryName)
            => categoryName == typeof(LayoutAreaHost).FullName
                ? new CapturingLogger(records)
                : Silent.Instance;

        public void Dispose() { }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }

        private sealed class Silent : ILogger
        {
            internal static readonly Silent Instance = new();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => false;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) { }
        }

        private sealed class CapturingLogger(ConcurrentQueue<RenderFailureRecord> sink) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            // Warning and above only — the levels that page. A Debug line is deliberately invisible
            // here: that IS the downgrade under test.
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning)
                    return;
                if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                    return;
                if (exception is null)
                    return;
                var area = values.FirstOrDefault(v => v.Key == "Area");
                if (area.Key is null)
                    return;
                sink.Enqueue(new RenderFailureRecord(logLevel, area.Value?.ToString(), exception));
            }
        }
    }
}
