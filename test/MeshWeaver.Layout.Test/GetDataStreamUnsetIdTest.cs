using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins what <c>GetDataStream&lt;T&gt;(id)</c> does for an id that was <b>never written</b> — the one
/// semantic of the layout-area data plane that had lived only in a code comment, and the reason the
/// same defect shipped three times.
///
/// <para><b>The behaviour.</b> A <i>written</i> id emits its value immediately on subscribe. A
/// <i>never-set</i> id emits <b>nothing at all</b> — not <c>null</c>, not a default, nothing — and the
/// stream never completes either. Mechanism:
/// <c>EntityStore.ReduceImpl(EntityReference)</c> returns <c>null</c> for an absent id;
/// <c>WorkspaceStreams.CreateReducedStream</c> filters those out (<c>Where(x =&gt; x is { Value: not null })</c>)
/// unless the stream opted into <c>NullReturn</c>; so the reduced stream's replay buffer never receives
/// an <c>OnNext</c> and has nothing to replay; and <c>LayoutAreaHost.GetStream</c> /
/// <c>LayoutExtensions.GetDataStream</c> filter nulls a second time on the way out.</para>
///
/// <para><b>Why the failure direction is counter-intuitive.</b> <c>GetDataStream&lt;T&gt;(id).Take(1)</c>
/// reads like a once-guard ("run this at most once"). It is not. On a never-set id it never fires, so it
/// does not prevent a <i>duplicate</i> run — it prevents the <b>first</b> one. A feature guarded that way
/// silently never happens, with no exception and nothing to grep. That is exactly what shipped three
/// times in MeshWeaver.Plugins (#435 / PR #441), twice on the two doors a standard pack reaches a user
/// through — and one of those doors also never cleared its <c>requestedAction</c>, because the clearing
/// code sat behind the same guard.</para>
///
/// <para><b>The right shapes</b> when you need a value for an id that may not be set:
/// <c>.StartWith(defaultValue)</c> (default-then-react — what <c>EditorExtensions.MapToToggleableControl</c>
/// does for its transient edit-state), or write a seed with <c>host.UpdateData(id, …)</c> before
/// subscribing, or gate on something that is guaranteed to be written.</para>
///
/// <para>🚨 <b>Every negative here is measured against a positive control on the same host.</b> "Nothing
/// arrived" is indistinguishable from "the test raced ahead", so no assertion below ends on a bare clock:
/// each observation window is closed by an <b>observed</b> write landing on a different id of the same
/// <see cref="LayoutAreaHost"/>. When the window closes, the data plane has demonstrably delivered while
/// the never-set subscription was open — so the silence is a measurement, not a gap.</para>
/// </summary>
public class GetDataStreamUnsetIdTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string ProbeView = nameof(ProbeView);

    /// <summary>Written by the view during render. The "a set id emits" side of every comparison.</summary>
    private const string WrittenId = "written_id";
    private const string WrittenValue = "written-at-render";

    /// <summary>Never written — not by the view, not by any test. The subject of this fixture.</summary>
    private const string NeverSetId = "never_set_id";

    /// <summary>Seeded at render, then re-written by the test body to close an observation window.</summary>
    private const string ControlId = "control_id";
    private const string ControlSeed = "seeded-at-render";
    private const string ControlLate = "written-after-subscribe";

    /// <summary>Never written at render; written by the test AFTER subscribing (late-arrival test).</summary>
    private const string LateId = "late_id";
    private const string LateValue = "arrived-late";

    // Instance, never static (NoStaticState.md): publishes the LayoutAreaHost of this test's render
    // pass so the test body can call GetDataStream on the REAL host rather than a stand-in.
    private readonly ReplaySubject<LayoutAreaHost> renderedHost = new(1);

    // Held so the area stream (and with it the LayoutAreaHost) stays rooted for the test's duration.
    private ISynchronizationStream<EntityStore>? areaStream;

    private UiControl RenderProbe(LayoutAreaHost host, RenderingContext ctx)
    {
        host.UpdateData(WrittenId, WrittenValue);
        host.UpdateData(ControlId, ControlSeed);
        // NeverSetId and LateId are deliberately NOT written here.
        renderedHost.OnNext(host);
        return Controls.Html("probe");
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(ProbeView, RenderProbe));

    private async Task<LayoutAreaHost> RenderProbeAreaAsync()
    {
        areaStream = GetHost().GetWorkspace().GetStream(new LayoutAreaReference(ProbeView));
        await areaStream!.GetControlStream(ProbeView)
            .Should().Within(10.Seconds()).Match(c => c is not null);
        return await renderedHost.Should().Within(10.Seconds()).Emit();
    }

    /// <summary>
    /// Opens an observation window that is closed by an OBSERVED event rather than a clock: the window
    /// ends when a fresh write to <see cref="ControlId"/> — issued only after the caller's subscription
    /// is already live — comes back out of the data plane.
    /// </summary>
    private IObservable<string?> ControlLanded(LayoutAreaHost host)
        => host.GetDataStream<string>(ControlId).Where(v => v == ControlLate);

    /// <summary>
    /// Baseline: an id that WAS written emits its value. Without this, every negative below would be
    /// satisfied by a fixture in which nothing works at all.
    /// </summary>
    [HubFact]
    public async Task SetId_EmitsItsValue()
    {
        var host = await RenderProbeAreaAsync();

        var value = await host.GetDataStream<string>(WrittenId)
            .Should().Within(10.Seconds()).Emit();

        value.Should().Be(WrittenValue);
    }

    /// <summary>
    /// The core semantic: a never-set id emits NOTHING, measured over a window in which the same host
    /// demonstrably delivered a different value. The window closes on the control write coming back —
    /// so this cannot pass vacuously by racing ahead of the data plane.
    /// </summary>
    [HubFact]
    public async Task NeverSetId_EmitsNothing_WhileASecondWriteLandsOnTheSameHost()
    {
        var host = await RenderProbeAreaAsync();

        // Collect everything the never-set id produces until the control write lands.
        var window = host.GetDataStream<string>(NeverSetId)
            .TakeUntil(ControlLanded(host))
            .ToArray()
            .Replay(1);

        // Connect subscribes synchronously HERE, before the control write below — so the window is a
        // genuine round trip through the stream's action block, not a zero-length replay.
        using var connection = window.Connect();

        host.UpdateData(ControlId, ControlLate);

        var emissions = await window.Should().Within(10.Seconds()).Emit();

        emissions.Should().BeEmpty(
            "GetDataStream on an id that was never written emits nothing at all — not null, not a "
            + "default. The control write to a DIFFERENT id landed during this exact window, so the "
            + "data plane was live and the never-set stream simply had nothing to say.");
    }

    /// <summary>
    /// The trap, named so nobody has to rediscover it: <c>GetDataStream(neverSetId).Take(1)</c> NEVER
    /// FIRES AND NEVER COMPLETES. Used as a once-guard it does not stop a duplicate run — it stops the
    /// first one, and any <c>await</c> on it hangs forever. The identical idiom on a written id fires
    /// exactly once in the same test, so this is a difference in the id, not in the plumbing.
    /// </summary>
    [HubFact]
    public async Task TakeOneOnNeverSetId_NeverFiresAndNeverCompletes_SoAOnceGuardBlocksTheFIRSTRun()
    {
        var host = await RenderProbeAreaAsync();

        var guardedRunsOnNeverSetId = 0;
        var completionsOnNeverSetId = 0;
        var guardedRunsOnWrittenId = 0;

        // The exact idiom that shipped three times, read as "run this at most once".
        using var neverSetGuard = host.GetDataStream<string>(NeverSetId)
            .Take(1)
            .Subscribe(
                _ => Interlocked.Increment(ref guardedRunsOnNeverSetId),
                () => Interlocked.Increment(ref completionsOnNeverSetId));

        // Positive control: the IDENTICAL idiom against an id that WAS written.
        using var writtenGuard = host.GetDataStream<string>(WrittenId)
            .Take(1)
            .Subscribe(_ => Interlocked.Increment(ref guardedRunsOnWrittenId));

        // Close the window on an observed event, not a clock.
        var landed = ControlLanded(host).Replay(1);
        using var connection = landed.Connect();
        host.UpdateData(ControlId, ControlLate);
        await landed.Should().Within(10.Seconds()).Emit();

        Volatile.Read(ref guardedRunsOnWrittenId).Should().Be(1,
            "the same .Take(1) idiom on a WRITTEN id fires exactly once — this is the positive control "
            + "that makes the two assertions below meaningful");

        Volatile.Read(ref guardedRunsOnNeverSetId).Should().Be(0,
            "a .Take(1) 'once-guard' on a never-set id never runs the guarded action AT ALL. It does "
            + "not prevent a duplicate run — it prevents the first one, silently. See MeshWeaver.Plugins "
            + "#435 / PR #441: three instances, two of them the doors a standard pack reaches a user "
            + "through, and both could stay shut forever.");

        Volatile.Read(ref completionsOnNeverSetId).Should().Be(0,
            "Take(1) completes only after its one emission, so on a never-set id it never completes "
            + "either — awaiting it (FirstAsync/ToTask/ToList) hangs until the enclosing timeout");
    }

    /// <summary>
    /// The complement that keeps the negative honest: a never-set id's stream is not DEAD, it is merely
    /// EMPTY. Subscribe to an id nobody has written, write it afterwards, and the value arrives — so
    /// "emits nothing" is about the absence of a value to replay, not a broken subscription.
    /// </summary>
    [HubFact]
    public async Task ValueSetAfterSubscription_Arrives_SoTheStreamIsEmptyNotDead()
    {
        var host = await RenderProbeAreaAsync();

        // LateId has never been written at this point — this is a subscription to a never-set id.
        var late = host.GetDataStream<string>(LateId).Replay(1);
        using var connection = late.Connect();

        host.UpdateData(LateId, LateValue);

        var value = await late.Should().Within(10.Seconds()).Emit();
        value.Should().Be(LateValue);
    }

    /// <summary>
    /// How a "seeded" id becomes a never-set id by accident: <see cref="LayoutAreaHost.UpdateData"/>
    /// is a <b>silent no-op for null</b>. So <c>host.UpdateData(id, node.Description)</c> on a node
    /// whose Description is null writes nothing, and every downstream <c>.Take(1)</c> guard on that id
    /// is silently dead — while the calling code reads as though the id were seeded.
    /// <para>This is why <c>AgentView</c> seeds all nine of its form ids as
    /// <c>node.X ?? ""</c>: the empty string writes, the null does not. Seed a sentinel, never a
    /// nullable straight from the model.</para>
    /// </summary>
    [HubFact]
    public async Task UpdateDataWithNull_WritesNothing_LeavingTheIdNeverSet()
    {
        var host = await RenderProbeAreaAsync();

        // Looks like a seed, is not one.
        host.UpdateData(LateId, null);

        var window = host.GetDataStream<string>(LateId)
            .TakeUntil(ControlLanded(host))
            .ToArray()
            .Replay(1);

        using var connection = window.Connect();

        host.UpdateData(ControlId, ControlLate);

        var emissions = await window.Should().Within(10.Seconds()).Emit();

        emissions.Should().BeEmpty(
            "UpdateData ignores null, so the id stays never-set and the stream stays silent. Seeding "
            + "with a nullable straight off the model (host.UpdateData(id, node.Description)) is the "
            + "usual way a guard ends up dead — seed a sentinel such as `?? \"\"` instead.");
    }

    /// <summary>
    /// Same semantics on the OTHER overload — <c>host.Stream.GetDataStream&lt;T&gt;(id)</c>
    /// (<see cref="LayoutExtensions"/>), which is the one <c>EditorExtensions</c> actually calls and the
    /// one with no <c>where T : class</c> constraint, so it also covers the <c>bool</c> edit-state ids.
    /// A never-set <c>bool</c> id does NOT emit <c>false</c> — it emits nothing, which is precisely why
    /// <c>MapToToggleableControl</c> has to bolt a <c>.StartWith(...)</c> onto its edit-state stream.
    /// </summary>
    [HubFact]
    public async Task StreamOverload_NeverSetBoolId_EmitsNothing_NotFalse()
    {
        var host = await RenderProbeAreaAsync();

        var window = host.Stream.GetDataStream<bool>(NeverSetId)
            .TakeUntil(host.Stream.GetDataStream<string>(ControlId).Where(v => v == ControlLate))
            .ToArray()
            .Replay(1);

        using var connection = window.Connect();

        host.UpdateData(ControlId, ControlLate);

        var emissions = await window.Should().Within(10.Seconds()).Emit();

        emissions.Should().BeEmpty(
            "a never-set bool id yields no emission at all — NOT default(bool). Any view that binds "
            + "straight to it renders 'awaiting first data' forever; the fix is .StartWith(default) or "
            + "seeding the id, never widening a timeout.");
    }
}
