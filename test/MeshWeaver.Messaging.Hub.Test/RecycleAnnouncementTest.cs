using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the firing contract of <see cref="RecycleAnnouncement"/> — the seam that lets a RECYCLED
/// hub tell its live subscribers it is going, which nothing else could do
/// (Systemorph/MeshWeaver#2533 / #2551).
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
/// <list type="number">
/// <item><description><b>It fires for a routed <see cref="DisposeRequest"/> — a RECYCLE — and
/// fires while the hub is still WHOLE</b> (<c>IsDisposing == false</c>). That is not decoration:
/// the announcement implementation reads the workspace's client-subscription registry and resolves
/// the parent hub that will carry the message, and both are only available before the
/// teardown starts.</description></item>
/// <item><description><b>It does NOT fire on a direct <c>Dispose()</c></b> — the host-teardown /
/// ancestor-cascade route. There the address is NOT coming back, so telling subscribers to re-ask
/// is precisely the resurrection the suppression above exists to prevent.</description></item>
/// </list>
/// </summary>
public class RecycleAnnouncementTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly Address RecycledAddress = new("recycled", "1");
    private static readonly Address DirectAddress = new("direct", "1");

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
        await recycled!.Started.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // THE RECYCLE — byte-for-byte what NodeTypeEnrichmentHelpers.WithOverlaySelfHeal posts.
        host.Post(new DisposeRequest(), o => o.WithTarget(RecycledAddress));

        // The announcement necessarily precedes the teardown, so the hub's own completion signal is
        // the exact "the recycle has happened" event to wait on — no poll, no sleep, no watchdog.
        await recycled.DisposalCompleted.FirstOrDefaultAsync()
            .Await(TestContext.Current.CancellationToken);

        announcements.Should().Be(1,
            "a recycle must give its live subscribers exactly one goodbye — none strands them on a "
            + "dead activation, and more than one multiplies the bounded re-ask they answer with");
        disposingWhenAnnounced.Should().BeFalse(
            "the announcement runs on the recycle's own turn, BEFORE Dispose() — that is what lets "
            + "it read the client-subscription registry and resolve the carrier that outlives the "
            + "teardown; announcing from inside the teardown is the phase inversion being fixed");
    }

    [HubFact]
    public async Task DirectDispose_DoesNotAnnounce()
    {
        var host = GetHost();
        var announcements = 0;

        var direct = host.GetHostedHub(DirectAddress, c => c.WithInitialization(h =>
            h.Set(new RecycleAnnouncement(() => Interlocked.Increment(ref announcements)))));
        direct.Should().NotBeNull();
        await direct!.Started.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The host-teardown route: HostedHubsCollection disposes its children with a direct
        // Dispose(), never a DisposeRequest, and a whole-tree teardown must stay SILENT — every
        // subscriber it could speak to is going down with it, and on Orleans a re-ask would
        // reactivate the very activation being retired.
        direct.Dispose();
        await direct.DisposalCompleted.FirstOrDefaultAsync()
            .Await(TestContext.Current.CancellationToken);

        announcements.Should().Be(0,
            "only a message-routed DisposeRequest is a RECYCLE — an address that is coming back. A "
            + "direct Dispose() is a teardown, and announcing it would tell subscribers to re-ask "
            + "for something that is gone");
    }
}
