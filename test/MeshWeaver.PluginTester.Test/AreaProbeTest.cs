using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.PluginTester;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The Tests-area verdict pipeline, driven with synthetic frames through the
/// <see cref="AreaProbe.ClassifyTestsFrames"/> seam.
///
/// <para>The one behaviour these tests exist to pin: <b>an "Area not found" frame is transient,
/// never terminal</b>. Right after a (re)compile the instance hub re-registers its layout, so the
/// sync stream legitimately serves a frame in which the type's custom areas do not exist yet.
/// Latching that frame as a verdict turned the re-registration window into
/// <c>No renderer is registered for area `Tests` on hub `Store`</c> — a gate failure that fired
/// only on loaded CI runners (16 straight local runs, macOS and Linux, could not reproduce it)
/// and redded three unrelated core PRs in one day.</para>
///
/// <para>The frames are pushed synchronously on subscribe (<see cref="FrameStream"/>), never
/// scheduled — see that helper for why an enumerable-backed source made these tests depend on
/// what the REST of the assembly was doing on the shared thread pool.</para>
/// </summary>
public class AreaProbeTest
{
    private static JsonElement Frame(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static readonly JsonElement NotFound = Frame(
        """{"areas":{"Tests":"**Area not found**\n\nNo renderer is registered for area `Tests` on hub `Store`."}}""");

    private static readonly JsonElement GreenTable = Frame(
        """{"areas":{"Tests":"✅ ManifestPaths_AreTopLevelIndexJsonOnly\n✅ PriceLabel_ZeroReadsFree\n2/2 passed"}}""");

    private static readonly JsonElement RedTable = Frame(
        """{"areas":{"Tests":"✅ First_Passes\n❌ Second_Fails: expected 42\n1/2 passed"}}""");

    // A frame carrying ONLY chrome: the node menu the global renderer writes into every area
    // subscription, with the Approvals entry's ✅ emoji icon. Chrome must never classify — the
    // PR #1654 shard-4 red was exactly this frame arriving before the Tests markdown and being
    // latched as "all rendered cases green".
    private static readonly JsonElement MenuChromeOnly = Frame(
        """{"areas":{"$Menu:Node":{"items":[{"label":"Request Approval","icon":"✅"},{"label":"Delete","icon":"🗑️"}]}}}""");

    /// <summary>
    /// The synthetic stand-in for the live sync stream: pushes <paramref name="frames"/> to the
    /// subscriber SYNCHRONOUSLY, during <c>Subscribe</c>, then completes — the same shape
    /// <see cref="AreaProbe.ExecuteTestsArea"/> sees from
    /// <c>GetRemoteStream&lt;JsonElement, LayoutAreaReference&gt;</c>.
    ///
    /// <para>🚨 NOT <c>new[]{ … }.ToObservable()</c>. That overload emits on
    /// <c>SchedulerDefaults.Iteration</c> = <b>CurrentThreadScheduler</b>, and schedules its emit
    /// loop as a SEPARATE work item — which <c>CurrentThreadScheduler</c> runs only if no
    /// trampoline is already installed on the calling thread, and otherwise merely ENQUEUES behind
    /// whatever that trampoline is doing. In this assembly a trampoline routinely IS installed:
    /// <c>PluginGateRunner.RunPackages</c> composes the gate with <c>.ToObservable().Concat()</c>,
    /// <c>PluginGateRunnerTest.RunGate</c> bridges it to a Task with <c>.FirstAsync().ToTask(…)</c>,
    /// and completing that TaskCompletionSource runs the awaiting continuation INLINE on the
    /// trampoline thread — a continuation which is the xUnit runner, which then runs the next test
    /// class synchronously on that same stack. The frames then sit in a foreign queue that cannot
    /// drain until the whole inlined runner stack unwinds, and <c>Timeout</c> wins with ZERO frames
    /// delivered: 35 % of whole-assembly runs (0 % for the class alone, 0 % under 64 CPU hogs),
    /// verdict <c>Tests area reported no verdict within 5s</c> at exactly 5000 ms with
    /// <c>lastTransient</c> still null. Systemorph/MeshWeaver#1826.</para>
    ///
    /// <para>Pushing on subscribe removes the scheduler from the picture entirely, so these tests
    /// assert on the CLASSIFICATION and never on a clock. <see cref="SyncFrames"/> implements
    /// <see cref="IObservable{T}"/> directly rather than going through <c>Observable.Create</c>,
    /// because every Rx <c>Producer</c>/<c>ObservableBase</c> subscribe path consults
    /// <c>CurrentThreadScheduler</c> too — this one cannot.</para>
    /// </summary>
    private static IObservable<JsonElement> FrameStream(params JsonElement[] frames) =>
        new SyncFrames(frames);

    private sealed class SyncFrames(JsonElement[] frames) : IObservable<JsonElement>
    {
        public IDisposable Subscribe(IObserver<JsonElement> observer)
        {
            foreach (var frame in frames)
                observer.OnNext(frame);
            observer.OnCompleted();
            return Disposable.Empty;
        }
    }

    /// <summary>
    /// The regression pin: a not-found frame followed by the real table must be GREEN. Before the
    /// fix, Take(1) latched the not-found frame and the run failed without the tests ever running.
    /// </summary>
    [Fact]
    public async Task NotFoundFrame_ThenGreenTable_IsPassed()
    {
        var frames = FrameStream(NotFound, GreenTable);

        var verdict = await AreaProbe.ClassifyTestsFrames(frames, TimeSpan.FromSeconds(5))
            .FirstAsync().Await();

        Assert.Equal(CheckOutcome.Passed, verdict.Outcome);
        Assert.Equal("2/2 passed", verdict.Detail);
    }

    /// <summary>
    /// The backstop stays a real gate: an area that NEVER appears is red — and the verdict names
    /// the last transient state instead of the generic "no verdict", so a genuinely missing Tests
    /// area is distinguishable from a suite that hung.
    /// </summary>
    [Fact]
    public async Task OnlyNotFoundFrames_TimesOut_ReportingTheLastTransientState()
    {
        var frames = Observable.Return(NotFound).Concat(Observable.Never<JsonElement>());

        var verdict = await AreaProbe.ClassifyTestsFrames(frames, TimeSpan.FromMilliseconds(300))
            .FirstAsync().Await();

        Assert.Equal(CheckOutcome.Failed, verdict.Outcome);
        Assert.Contains("never became available", verdict.Detail);
        Assert.Contains("Area not found", verdict.Detail);
    }

    /// <summary>
    /// Chrome never classifies: a frame carrying only the node menu (whose Approvals entry's
    /// icon IS the ✅ emoji) must be treated as transient, and the verdict must come from the
    /// Tests CONTENT frame that follows — with the real "N/M passed" detail, not the premature
    /// "all rendered cases green".
    /// </summary>
    [Fact]
    public async Task MenuChromeFrame_ThenGreenTable_ReportsThePassSummary()
    {
        var frames = FrameStream(MenuChromeOnly, GreenTable);

        var verdict = await AreaProbe.ClassifyTestsFrames(frames, TimeSpan.FromSeconds(5))
            .FirstAsync().Await();

        Assert.Equal(CheckOutcome.Passed, verdict.Outcome);
        Assert.Equal("2/2 passed", verdict.Detail);
    }

    /// <summary>A red row still fails immediately — transience applies to not-found only.</summary>
    [Fact]
    public async Task RedRow_FailsImmediately()
    {
        var frames = Observable.Return(RedTable).Concat(Observable.Never<JsonElement>());

        var verdict = await AreaProbe.ClassifyTestsFrames(frames, TimeSpan.FromSeconds(5))
            .FirstAsync().Await();

        Assert.Equal(CheckOutcome.Failed, verdict.Outcome);
        Assert.Contains("Second_Fails", verdict.Detail);
    }

    /// <summary>An empty stream (no frames at all) still reports the generic no-verdict red.</summary>
    [Fact]
    public async Task NoFrames_TimesOut_WithTheGenericNoVerdict()
    {
        var verdict = await AreaProbe.ClassifyTestsFrames(
                Observable.Never<JsonElement>(), TimeSpan.FromMilliseconds(300))
            .FirstAsync().Await();

        Assert.Equal(CheckOutcome.Failed, verdict.Outcome);
        Assert.Contains("no verdict", verdict.Detail);
    }

    /// <summary>
    /// The flake pin for Systemorph/MeshWeaver#1826: the synthetic frame source must deliver every
    /// frame DURING <c>Subscribe</c>, even when the calling thread already sits inside somebody
    /// else's <c>CurrentThreadScheduler</c> trampoline — the state xUnit leaves a thread in
    /// whenever a sibling test completes a <c>ToTask()</c> bridge from inside one, because
    /// <c>TrySetResult</c> runs the awaiting continuation (here: the whole runner) inline.
    ///
    /// <para>Runs on a dedicated thread so the trampoline state is KNOWN. This test's own thread
    /// may already be inside a foreign trampoline — that is the very defect — which would defer
    /// the body and leave the pin asserting nothing.</para>
    ///
    /// <para>Fails on the retired <c>new[]{ … }.ToObservable()</c> source, which schedules its
    /// emit loop as a separate work item onto the ambient trampoline: zero frames and no verdict
    /// at the point this asserts. It does no waiting at all, so there is no clock to tune.</para>
    /// </summary>
    [Fact]
    public void Frames_ArriveDuringSubscribe_EvenInsideAForeignTrampoline()
    {
        var insideForeignTrampoline = false;
        var framesSeenOnReturn = -1;
        var verdictsSeenOnReturn = -1;
        var verdicts = new List<AreaVerdict>();

        var pin = new Thread(() =>
            CurrentThreadScheduler.Instance.Schedule(() =>
            {
                // Schedule() on a clean thread INSTALLS a trampoline and runs this body inside it.
                insideForeignTrampoline = !CurrentThreadScheduler.IsScheduleRequired;

                var seen = new List<JsonElement>();
                using (FrameStream(NotFound, GreenTable).Subscribe(seen.Add))
                    framesSeenOnReturn = seen.Count;

                using (AreaProbe
                           .ClassifyTestsFrames(FrameStream(NotFound, GreenTable), TimeSpan.FromSeconds(5))
                           .Subscribe(verdicts.Add))
                    verdictsSeenOnReturn = verdicts.Count;
            }))
        {
            IsBackground = true,
            Name = "areaprobe-trampoline-pin",
        };

        pin.Start();
        Assert.True(pin.Join(TimeSpan.FromSeconds(30)),
            "the pin body performs no waiting at all — not finishing means it was queued, never run");

        Assert.True(insideForeignTrampoline,
            "the body must execute inside a live CurrentThreadScheduler trampoline, or it pins nothing");
        Assert.Equal(2, framesSeenOnReturn);
        Assert.Equal(1, verdictsSeenOnReturn);
        Assert.Equal(CheckOutcome.Passed, verdicts[0].Outcome);
        Assert.Equal("2/2 passed", verdicts[0].Detail);
    }
}
