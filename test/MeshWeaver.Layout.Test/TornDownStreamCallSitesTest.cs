using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// THE CALL SITES SURVIVE A DEAD STREAM — Systemorph/MeshWeaver#3321, step 2 of 3.
///
/// <para><b>What these tests are about.</b> <see cref="ISynchronizationStream.Hub"/> is declared
/// NON-NULLABLE, so a stream whose owner has been torn down keeps answering it with a corpse and
/// every <c>stream.Hub.Something</c> reads as safe. It is not: the production incident behind this
/// issue was an NRE inside <c>LayoutAreaHost</c>'s constructor during a recycle window, which
/// reached the subscriber as a TERMINAL <c>DeliveryFailure</c> — "this failed forever" for what was
/// a transient recycle. Step 1 made the question askable (<c>stream.TryGetHub()</c>); this step
/// moves the consumer sites onto it, each answering with the "no" its own signature already
/// models.</para>
///
/// <para>🚨 <b>Every test here has to be able to FAIL, and it was MEASURED that they do.</b> Run
/// against the pre-change sources with the tests unchanged: <b>4 failed, 1 passed</b> — the four
/// negatives red, the live control green. Post-change: 5 passed. That measurement is the point,
/// because the obvious version of these tests would NOT discriminate today. Step 3 has not landed,
/// so a disposed stream still answers <see cref="ISynchronizationStream.Hub"/> with a non-null
/// corpse and nothing NREs YET — an assertion of "did not throw" would have passed on the defect.
/// What the pre-change code did instead is USE the corpse, and each failure names a different way
/// that is wrong: a subscriber got the dead stream's replayed last frame as though it were live
/// (<c>found 1 item(s)</c>), a synchronous read handed back a value off a frozen frame
/// (<c>found "rendered"</c>), and a submit into a disposed hub reported no refusal at all
/// (<c>found &lt;null&gt;</c> where the log key belongs). So the assertions pin the SHAPE of the
/// answer, never merely its absence.</para>
///
/// <para>The live control is not decoration either: a guard that answered "dead" unconditionally
/// would satisfy all four negatives at once. It is the half that pins step 2's acceptance bar —
/// for a LIVE stream every migrated site does exactly what it did before.</para>
/// </summary>
public class TornDownStreamCallSitesTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string TestArea = nameof(TestArea);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(TestArea, (_, _) => Controls.Html("rendered")));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private ISynchronizationStream<JsonElement> OpenStream()
        => GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(TestArea));

    /// <summary>
    /// Opens a stream, waits until it has actually served the area, then disposes it — so what the
    /// assertions below face is a stream that WAS alive and is now a corpse, not one that never
    /// started. That distinction is the whole point: an unstarted stream would fail these paths for
    /// reasons that have nothing to do with #3321.
    /// </summary>
    private async Task<ISynchronizationStream<JsonElement>> OpenThenTearDownStream()
    {
        var stream = OpenStream();
        await stream.GetControlStream(TestArea)
            .Should().Within(10.Seconds()).Match(x => x is HtmlControl);

        stream.IsUsable().Should().BeTrue("precondition: the stream served the area before we kill it");
        stream.Dispose();
        stream.TryGetHub().Should().BeNull("precondition: the stream is a corpse now");
        // 🚨 This assertion INVERTED at step 3 (#3321). Through step 2 it read
        // `Hub.Should().NotBeNull()` — "the non-nullable contract still holds, which is exactly why
        // every site below looked safe while it was not". Step 3 is the change that makes it stop
        // holding: a disposed stream now RELEASES the hub, which is what reclaims the leaked
        // `stream → dead hub → resolved state` graph. Keeping the assertion (flipped) rather than
        // deleting it is deliberate — it is the line that pins WHICH of the two contracts is in
        // force, and a silent revert of step 3 turns it red here as well as in
        // StreamReleasesItsHubTest.
        stream.Hub.Should().BeNull(
            "step 3 releases the reference on disposal — the declaration stays non-nullable for the "
            + "packaged interface, and what makes that safe is that every site below can now ask");

        return stream;
    }

    /// <summary>
    /// The flagship. <c>SubmitModel</c> already models failure as an <see cref="ActivityLog"/>
    /// carrying an error message (the no-route branch), so a torn-down stream gets the SAME shape
    /// rather than an NRE on the way into <c>Hub.Post</c>. A form whose page was recycled mid-edit
    /// tells the user "submit again"; before this it took the circuit down.
    /// </summary>
    [HubFact]
    public async Task SubmitModel_OnATornDownStream_AnswersInTheActivityLog()
    {
        var stream = await OpenThenTearDownStream();
        var model = new ModelParameter<JsonElement>(
            JsonSerializer.SerializeToElement(new { Value = 1 }),
            (_, _) => null);

        var log = await stream.SubmitModel(model).FirstAsync().Timeout(10.Seconds());

        log.Category.Should().Be(ActivityCategory.DataUpdate);
        log.Messages.Should().ContainSingle()
            .Which.MessageKey.Should().Be("activity.dataUpdate.streamClosed",
                "the refusal is REPORTED through the surface's own error channel and is localizable "
                + "— a submit that silently did nothing would be the swallow this issue forbids");
    }

    /// <summary>
    /// <c>GetStream&lt;T&gt;</c> read <c>Hub.JsonSerializerOptions</c> twice: once eagerly, to parse
    /// the pointer's id, and once per emission. A dead stream can neither parse nor ever emit
    /// again — its store is completed — so the honest answer is the empty sequence, which is
    /// exactly what subscribing to the dead stream would have produced anyway.
    ///
    /// <para>The two-segment pointer is deliberate: the eager read only happens for a pointer that
    /// HAS an id segment, so a one-segment pointer would leave the pre-change NRE unreached and the
    /// test would pass against the defect.</para>
    /// </summary>
    [HubFact]
    public async Task GetControlStream_OnATornDownStream_CompletesWithoutEmitting()
    {
        var stream = await OpenThenTearDownStream();

        var emissions = await stream.GetControlStream(TestArea)
            .ToList()
            .Timeout(10.Seconds());

        emissions.Should().BeEmpty(
            "a stream that can never emit again must hand back a sequence that says so by "
            + "COMPLETING, not one that throws when a subscriber arrives");
    }

    /// <summary>
    /// The synchronous read path. <c>default</c> is already this method's answer for "the value is
    /// not there" — it says so twice before the hub is ever touched — so a corpse reuses it rather
    /// than inventing a new failure mode.
    /// </summary>
    [HubFact]
    public async Task GetDataBoundValue_OnATornDownStream_IsAbsentRatherThanThrowing()
    {
        var stream = await OpenThenTearDownStream();

        var value = stream.GetDataBoundValue<string>(
            new JsonPointerReference($"{LayoutAreaReference.GetControlPointer(TestArea)}/data"), null);

        value.Should().BeNull("absent is the modelled answer; an NRE is not an answer");
    }

    /// <summary>
    /// The reactive binding path — the one a Blazor view actually holds. A dead stream yields no
    /// emission (the existing null filter drops the absent value), so the binding stalls honestly
    /// instead of faulting the circuit.
    /// </summary>
    [HubFact]
    public async Task DataBind_OnATornDownStream_EmitsNothing()
    {
        var stream = await OpenThenTearDownStream();

        var emissions = await stream
            .DataBind<string>(new JsonPointerReference(LayoutAreaReference.GetControlPointer(TestArea)))
            .ToList()
            .Timeout(10.Seconds());

        emissions.Should().BeEmpty();
    }

    /// <summary>
    /// 🚨 THE CONTROL, and it is not decoration. Every assertion above is satisfied by a guard that
    /// says "dead" unconditionally — which would migrate the call sites onto something that never
    /// works. This pins the acceptance bar of step 2 instead: for a LIVE stream every migrated site
    /// does exactly what it did before.
    /// </summary>
    [HubFact]
    public async Task OnALiveStream_TheMigratedSitesBehaveExactlyAsBefore()
    {
        var stream = OpenStream();

        var control = await stream.GetControlStream(TestArea)
            .Should().Within(10.Seconds()).Match(x => x is HtmlControl);
        control.Should().BeOfType<HtmlControl>()
            .Which.Data!.ToString().Should().Be("rendered",
                "GetStream<T> still deserializes through the hub's serializer options");

        stream.TryGetHub().Should().BeSameAs(stream.Hub,
            "the guarded read resolves the very hub the non-nullable property does");

        var bound = await stream
            .DataBind<string>(new JsonPointerReference($"{LayoutAreaReference.GetControlPointer(TestArea)}/data"))
            .FirstAsync()
            .Timeout(10.Seconds());
        bound.Should().Be("rendered", "the data binding still delivers the live value");
    }
}
