using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the framework's default busy state for EVERY click (<c>Doc/GUI/ButtonPendingState</c>): the
/// owner writes a <see cref="ClickProgress"/> the moment the click runs; a duplicate click while it runs
/// never re-runs the action; the action's status line and tracked work reach the client; Cancel —
/// a client write to <see cref="ClickProgress.CancelPointerFor"/> — disposes the action, trips its token
/// and runs its cancel handlers; a failure is SHOWN on the click state and re-enables the control.
/// <para>Negative control inside <see cref="ADuplicateClickWhileRunningRunsTheActionOnce"/>: once the
/// first click settled, the same click runs the action again — so the "once" is the in-flight dedupe,
/// not a counter that never moves.</para>
/// </summary>
public class ClickBusyStateTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "Busy";
    private const string RunOnceArea = Area + "/RunOnce";
    private const string ProgressArea = Area + "/Progress";
    private const string CancelArea = Area + "/Cancel";
    private const string FailArea = Area + "/Fail";
    private const string TrackArea = Area + "/Track";

    private int runOnceInvocations;
    private Subject<Unit> runOnceWork = new();
    private readonly Subject<Unit> progressWork = new();
    private readonly AsyncSubject<Unit> cancelHandlerRan = new();
    private readonly AsyncSubject<Unit> tokenTripped = new();
    private readonly Subject<ClickProgress> tracked = new();

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(
                Area,
                Controls.Stack
                    .WithView(Controls.Button("Approve").WithReactiveClickAction(_ =>
                    {
                        Interlocked.Increment(ref runOnceInvocations);
                        return runOnceWork.AsObservable();
                    }), "RunOnce")
                    .WithView(Controls.Button("Import").WithReactiveClickAction(ctx =>
                    {
                        ctx.ReportProgress("Reading 3 files", 0.25);
                        return progressWork.AsObservable();
                    }), "Progress")
                    .WithView(Controls.Button("Long").WithReactiveClickAction(ctx =>
                    {
                        ctx.OnCancel(() => { cancelHandlerRan.OnNext(Unit.Default); cancelHandlerRan.OnCompleted(); });
                        ctx.CancellationToken.Register(() => { tokenTripped.OnNext(Unit.Default); tokenTripped.OnCompleted(); });
                        return Observable.Never<Unit>();
                    }), "Cancel")
                    .WithView(Controls.Button("Fail").WithReactiveClickAction(_ =>
                        Observable.Throw<Unit>(new InvalidOperationException("the approval was rejected"))), "Fail")
                    .WithView(Controls.Button("Start").WithClickAction(ctx =>
                    {
                        // The action itself is done at once; the work it STARTED is tracked.
                        ctx.Track(tracked.AsObservable());
                        return Task.CompletedTask;
                    }), "Track")));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [HubFact]
    public async Task ADuplicateClickWhileRunningRunsTheActionOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = await OpenStream(RunOnceArea);

        var first = Click(stream, RunOnceArea);
        await Progress(stream, RunOnceArea).Should().Within(10.Seconds()).Match(
            p => p is { Running: true }, "the click is running", ct);
        var second = Click(stream, RunOnceArea);
        var third = Click(stream, RunOnceArea);

        await second.Should().NotEmit(300.Milliseconds(), "a joined click is answered with the first click's outcome, not before", ct);
        Volatile.Read(ref runOnceInvocations).Should().Be(1, "duplicate clicks while the first runs must JOIN it, never run the action again");

        runOnceWork.OnCompleted();
        foreach (var receipt in new[] { first, second, third })
            (await receipt.Should().Within(10.Seconds()).Emit("every click is answered", ct)).Should().BeNull("accepted");
        await Progress(stream, RunOnceArea).Should().Within(10.Seconds()).Match(
            p => p is { Running: false, Error: null }, "the settled click re-enables the control", ct);

        // Negative control: settled, the same click is a NEW click and runs the action again.
        runOnceWork = new Subject<Unit>();
        runOnceWork.OnCompleted(); // a Subject replays completion, so the new click settles at once
        var fourth = Click(stream, RunOnceArea);
        (await fourth.Should().Within(10.Seconds()).Emit("the new click is answered", ct)).Should().BeNull();
        Volatile.Read(ref runOnceInvocations).Should().Be(2, "after settling, the dedupe window is closed and the action runs again");
    }

    /// <summary>
    /// Two rows known only by their VALUE share one click state; a click on the second while the
    /// first runs is refused (and does not run), never allowed to overwrite the running click's state.
    /// Control: a row carrying its own <see cref="RowContext.Key"/> gets its own state and runs.
    /// </summary>
    [HubFact]
    public async Task AClickOnAnotherKeylessRowWhileOneRunsIsRefusedNotRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = await OpenStream(RunOnceArea);
        var rowA = new RowContext { Value = new { title = "A" } };
        var rowB = new RowContext { Value = new { title = "B" } };

        var first = Click(stream, RunOnceArea, rowA);
        await Progress(stream, RunOnceArea).Should().Within(10.Seconds()).Match(p => p is { Running: true }, "row A runs", ct);
        var other = Click(stream, RunOnceArea, rowB);
        (await other.Should().Within(10.Seconds()).Emit("refused", ct)).Should().Be("Another action on this control is still running");
        Volatile.Read(ref runOnceInvocations).Should().Be(1, "the refused click must not run");

        var keyed = Click(stream, RunOnceArea, new RowContext { Value = new { title = "C" }, Key = "c" });
        await Progress(stream, ClickProgress.RowArea(RunOnceArea, new RowContext { Key = "c" })).Should().Within(10.Seconds())
            .Match(p => p is { Running: true }, "a keyed row has its own click state and runs", ct);
        Volatile.Read(ref runOnceInvocations).Should().Be(2);

        runOnceWork.OnCompleted();
        await first.Should().Within(10.Seconds()).Emit("row A settles", ct);
        await keyed.Should().Within(10.Seconds()).Emit("row C settles", ct);
    }

    [HubFact]
    public async Task TheBusyStateCarriesTheActionsStatusLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = await OpenStream(ProgressArea);
        var receipt = Click(stream, ProgressArea);

        var busy = await Progress(stream, ProgressArea).Should().Within(10.Seconds()).Match(
            p => p is { Running: true, Status: "Reading 3 files" }, "the action's status line reaches the client", ct);
        busy!.Fraction.Should().Be(0.25);
        busy.Cancellable.Should().BeTrue("every running click offers Cancel");

        progressWork.OnCompleted();
        await receipt.Should().Within(10.Seconds()).Emit("the click is answered", ct);
        await Progress(stream, ProgressArea).Should().Within(10.Seconds()).Match(
            p => p is { Running: false }, "done", ct);
    }

    [HubFact]
    public async Task CancelDisposesTheActionTripsItsTokenAndRunsItsHandlers()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = await OpenStream(CancelArea);
        var receipt = Click(stream, CancelArea);
        await Progress(stream, CancelArea).Should().Within(10.Seconds()).Match(
            p => p is { Running: true }, "the click runs (and would run forever)", ct);
        await cancelHandlerRan.Should().NotEmit(200.Milliseconds(), "nobody pressed Cancel yet", ct);

        // What a client's Cancel button does: one property write, never a verb message.
        await stream.GetDataStream<JsonElement>(new JsonPointerReference(ClickProgress.CancelPointerFor(CancelArea)))
            .Should().Within(10.Seconds()).Match(e => e.ValueKind == JsonValueKind.Object, "the owner seeded the cancel request", ct);
        var running = await Progress(stream, CancelArea).Should().Within(10.Seconds()).Match(p => p is { Running: true }, "running", ct);
        // A stale Cancel for an earlier session is ignored (negative control for the session stamp).
        stream.UpdatePointer(running!.Session - 1, ClickProgress.CancelPointerFor(CancelArea), new JsonPointerReference("requestedSession"));
        await cancelHandlerRan.Should().NotEmit(300.Milliseconds(), "a Cancel naming another session is not this click's Cancel", ct);
        stream.UpdatePointer(running.Session, ClickProgress.CancelPointerFor(CancelArea), new JsonPointerReference("requestedSession"));

        await cancelHandlerRan.Should().Within(10.Seconds()).Emit("Cancel runs the action's cancel handlers", ct);
        await tokenTripped.Should().Within(10.Seconds()).Emit("Cancel trips ctx.CancellationToken", ct);
        (await receipt.Should().Within(10.Seconds()).Emit("a cancelled click is answered", ct))
            .Should().Be("Cancelled", "the never-completing action was cancelled, not accepted");
        await Progress(stream, CancelArea).Should().Within(10.Seconds()).Match(
            p => p is { Running: false, Summary: "Cancelled" }, "the control re-enables and says it was cancelled", ct);
    }

    [HubFact]
    public async Task AFailureIsShownOnTheClickStateAndReEnables()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = await OpenStream(FailArea);
        var receipt = Click(stream, FailArea);
        (await receipt.Should().Within(10.Seconds()).Emit("refused", ct)).Should().Contain("the approval was rejected");
        await Progress(stream, FailArea).Should().Within(10.Seconds()).Match(
            p => p is { Running: false, Error: "the approval was rejected" }, "the error is shown, not swallowed", ct);
    }

    [HubFact]
    public async Task TrackedWorkKeepsTheControlBusyAfterTheActionIsDone()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = await OpenStream(TrackArea);
        var receipt = Click(stream, TrackArea);
        (await receipt.Should().Within(10.Seconds()).Emit("the action itself is done at once", ct)).Should().BeNull();

        tracked.OnNext(new ClickProgress { Running = true, Status = "Step 2 of 4", Fraction = 0.5 });
        await Progress(stream, TrackArea).Should().Within(10.Seconds()).Match(
            p => p is { Running: true, Status: "Step 2 of 4" }, "the tracked work keeps the control busy and says what is happening", ct);

        tracked.OnNext(new ClickProgress { Running = false, Summary = "4 of 4 done" });
        await Progress(stream, TrackArea).Should().Within(10.Seconds()).Match(
            p => p is { Running: false, Summary: "4 of 4 done" }, "the tracked work's end settles the click with its summary", ct);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Submits a click; the subject emits null when accepted, the refusal sentence when refused.</summary>
    private static ReplaySubject<string?> Click(ISynchronizationStream<JsonElement> stream, string area, RowContext? row = null)
    {
        var answer = new ReplaySubject<string?>(1);
        stream.SubmitUserAction(new ClickedEvent(area, stream.StreamId) { Row = row }, actingUser: null,
            onRefused: sentence => answer.OnNext(sentence), onAccepted: () => answer.OnNext(null));
        return answer;
    }

    private static IObservable<ClickProgress?> Progress(ISynchronizationStream<JsonElement> stream, string area)
        => stream.GetDataStream<ClickProgress>(new JsonPointerReference(ClickProgress.PointerFor(area)));

    private async Task<ISynchronizationStream<JsonElement>> OpenStream(string controlArea)
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));
        await stream.GetControlStream(controlArea).Should().Within(10.Seconds()).Match(
            control => control is not null, "the button is rendered",
            cancellationToken: TestContext.Current.CancellationToken);
        return stream;
    }
}
