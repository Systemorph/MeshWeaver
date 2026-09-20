using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the firing contract of <see cref="RecycleAnnouncement"/> — the seam that lets a hub whose
/// address is coming back tell its live subscribers it is going, which nothing else can do
/// (Systemorph/MeshWeaver#2533 / #2551, corrected by #3986).
///
/// <para><b>Why a seam was needed at all.</b> A hub's own teardown callbacks run in the ShutDown
/// phase, and <c>JsonSynchronizationStream</c> deliberately SUPPRESSES its end-of-stream
/// announcement there — "a hub must speak only for itself, and never while it is dying", because a
/// dying owner reaching up the tree for a last word resurrects the Orleans activation it is
/// retiring. So the terminal event was emitted after the thing that would deliver it had been torn
/// down, and a subscriber of a recycled hub got no frame, no completion and no error: it held its
/// last snapshot forever. The end-to-end consequence is pinned by
/// <c>RecycleStrandsLiveSubscriberTest</c>; what THIS test pins is the two properties that make
/// the seam safe.</para>
///
/// <para>🚨 <b>And WHICH teardowns announce is the correction #3986 made.</b> The seam was
/// originally keyed on "a routed <c>DisposeRequest</c>", on the reasoning that a routed request is a
/// recycle (the address is coming back) while a direct <c>Dispose()</c> is a whole-tree teardown
/// (every subscriber is going down with it). The second half is false for the route that produces
/// most of the direct disposals in the mesh: <c>MessageHubGrain.OnDeactivateAsync</c> calls
/// <c>hub.Dispose()</c> — never a <c>DisposeRequest</c> — for an address Orleans WILL reactivate on
/// the next message, whose subscribers are live mirrors in other hubs, circuits and pods. Keyed on
/// the routed request, a deactivating owner told them nothing, they kept replaying a stale snapshot,
/// and every user action they sent afterwards was refused and discarded. So the guard is now the fact
/// that actually decides it — <b>is an ANCESTOR taking us with it</b> — asked of
/// <see cref="IMessageHub.IsDisposing"/>'s sibling <c>IsShuttingDown</c>, which the ancestor's
/// <c>CloseCreation</c> cascade has already set by the time it disposes its children.</para>
///
/// <list type="number">
/// <item><description><b>It fires once per hub, and while the hub is still WHOLE</b>
/// (<c>IsDisposing == false</c>). That is not decoration: the announcement implementation reads the
/// workspace's client-subscription registry and resolves the parent hub that will carry the message,
/// and both are only available before the teardown starts.</description></item>
/// <item><description><b>It fires on a routed <see cref="DisposeRequest"/></b> — the automatic
/// recycles (<c>WithOverlaySelfHeal</c>, <c>NodeTypeRebindWatcher</c>, the stale-build convergence
/// branch).</description></item>
/// <item><description><b>It fires on a direct <c>Dispose()</c> of a hub whose own teardown this is</b>
/// — the Orleans-deactivation route.</description></item>
/// <item><description><b>It does NOT fire when an ANCESTOR is disposing this hub</b> — there the
/// carrier is going down too, the address is not coming back, and telling subscribers to re-ask is
/// precisely the resurrection the suppression above exists to prevent.</description></item>
/// </list>
/// </summary>
public class RecycleAnnouncementTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly Address RecycledAddress = new("recycled", "1");
    private static readonly Address DirectAddress = new("direct", "1");
    private static readonly Address CascadeParentAddress = new("cascadeparent", "1");
    private static readonly Address CascadeChildAddress = new("cascadechild", "1");

    [HubFact]
    public async Task RoutedDisposeRequest_Announces_Once_AndBeforeTheTeardownStarts()
    {
        var host = GetHost();
        var announcements = 0;
        // Read INSIDE the announcement: "the hub is still whole" is a fact about the moment the
        // callback runs, and a probe taken afterwards would always read true.
        var disposingWhenAnnounced = true;

        var recycled = host.GetHostedHub(RecycledAddress, c => c.WithInitialization(h =>
            h.Set(new RecycleAnnouncement(() =>
            {
                Interlocked.Increment(ref announcements);
                disposingWhenAnnounced = h.IsDisposing;
            }))));
        recycled.Should().NotBeNull();
        await recycled!.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);

        // THE RECYCLE — byte-for-byte what NodeTypeEnrichmentHelpers.WithOverlaySelfHeal posts.
        host.Post(new DisposeRequest(), o => o.WithTarget(RecycledAddress));

        // The announcement necessarily precedes the teardown, so the hub's own completion signal is
        // the exact "the recycle has happened" event to wait on — no poll, no sleep, no watchdog.
        await recycled.DisposalCompleted.FirstOrDefaultAsync()
            .Await(TestContext.Current.CancellationToken);

        announcements.Should().Be(1,
            "a recycle must give its live subscribers exactly one goodbye — none strands them on a "
            + "dead activation, and more than one multiplies the bounded re-ask they answer with. "
            + "🚨 This is also the control on WHERE the announcement is made: HandleDispose ends in "
            + "Dispose(), so announcing from both would double every routed recycle's goodbye");
        disposingWhenAnnounced.Should().BeFalse(
            "the announcement runs on the recycle's own turn, BEFORE the teardown starts — that is "
            + "what lets it read the client-subscription registry and resolve the carrier that "
            + "outlives the teardown; announcing from inside the teardown is the phase inversion "
            + "being fixed");
    }

    /// <summary>
    /// 🚨 THE #3986 CASE. <c>MessageHubGrain.OnDeactivateAsync</c> calls <c>hub.Dispose()</c>
    /// directly — never a <c>DisposeRequest</c> — for an address Orleans reactivates on the next
    /// message. Its subscribers are live mirrors elsewhere, and the parent that carries their
    /// goodbye is untouched. A hub in that position MUST announce: until it did, an owner grain
    /// going idle stranded every mirror bound to it, and each of their clicks was refused
    /// "NO sync hub for this stream was EVER registered on the current activation".
    /// </summary>
    [HubFact]
    public async Task ADirectDisposeAnnouncesOnce_BecauseAnOrleansDeactivationIsOne()
    {
        var host = GetHost();
        var announcements = 0;
        var disposingWhenAnnounced = true;

        var direct = host.GetHostedHub(DirectAddress, c => c.WithInitialization(h =>
            h.Set(new RecycleAnnouncement(() =>
            {
                Interlocked.Increment(ref announcements);
                disposingWhenAnnounced = h.IsDisposing;
            }))));
        direct.Should().NotBeNull();
        await direct!.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);

        // The Orleans-deactivation route, verbatim: MessageHubGrain.OnDeactivateAsync notes the
        // cause and calls Dispose(). No DisposeRequest is ever posted.
        direct.Dispose();
        await direct.DisposalCompleted.FirstOrDefaultAsync()
            .Await(TestContext.Current.CancellationToken);

        announcements.Should().Be(1,
            "an Orleans deactivation is a direct Dispose() of an address that IS coming back, with a "
            + "parent that outlives it and subscribers that do not go down with it — so it has to "
            + "give them the same one goodbye a routed recycle does. Keyed on the routed request "
            + "instead, this route told nobody and every later user action from those mirrors was "
            + "thrown away (#3986, measured on memex-cloud 2026-09-18)");
        disposingWhenAnnounced.Should().BeFalse(
            "and it runs as the FIRST statement of Dispose(), before IsDisposing flips — the "
            + "registry it reads and the carrier it resolves are only available while the hub is "
            + "whole");
    }

    /// <summary>
    /// The other direction, and the one the original "direct Dispose() stays silent" expectation was
    /// really about: a whole-tree teardown. <c>HostedHubsCollection</c> disposes its children with a
    /// direct <c>Dispose()</c>, and by then the ancestor's <c>CloseCreation</c> has frozen the whole
    /// subtree — so <c>IsShuttingDown</c> is already true. The carrier is going down with us, the
    /// address is not coming back, and a re-ask would reactivate what is being retired.
    /// </summary>
    [HubFact]
    public async Task AnAncestorsCascadeDoesNotAnnounce()
    {
        var host = GetHost();
        var announcements = 0;

        var parent = host.GetHostedHub(CascadeParentAddress, c => c);
        parent.Should().NotBeNull();
        var child = parent!.GetHostedHub(CascadeChildAddress, c => c.WithInitialization(h =>
            h.Set(new RecycleAnnouncement(() => Interlocked.Increment(ref announcements)))));
        child.Should().NotBeNull();
        await child!.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);

        // The ANCESTOR goes. It freezes the subtree first (CloseCreation), then disposes the child.
        parent.Dispose();
        await child.DisposalCompleted.FirstOrDefaultAsync()
            .Await(TestContext.Current.CancellationToken);

        announcements.Should().Be(0,
            "a hub an ancestor is taking with it must stay silent: its carrier — the parent — is "
            + "going down too, so there is nothing that could still deliver the goodbye, and "
            + "telling a subscriber to re-ask is exactly the resurrection JsonSynchronizationStream's "
            + "teardown suppression exists to prevent");
    }
}
