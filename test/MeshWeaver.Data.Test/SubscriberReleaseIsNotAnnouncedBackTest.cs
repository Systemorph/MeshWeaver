using System.Reactive.Linq;
using System.Threading;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 <b>An end the subscriber ASKED for is not announced back to it</b> — Systemorph/MeshWeaver#5532.
///
/// <para>The owner's <see cref="StreamEndedEvent"/> (#2191) tells a subscriber that its server-side
/// stream ended without being asked: an idle release, an eviction, an explicit disposal. It hung off
/// the owner-side stream's disposal, and the most common disposal is the subscriber's own
/// <see cref="UnsubscribeRequest"/> — so every release was answered with an announcement to a
/// <c>sync/</c> hub that had already disposed itself. Routing drops it (<c>[CanBeIgnored]</c>), so one
/// release costs one wasted message. A subscriber that releases thousands of mirrors at once — a
/// cache hub going down — got thousands of them back through ONE mesh hub inside a second:
/// production measured 454 aggregate-watermark trips in 36 s on <c>mesh/YxuIePH430S3wXXs-HRa8A</c>,
/// shedding <c>StreamEndedEvent</c>.</para>
///
/// <para>Both arms count the announcement as it LEAVES the owner (its post pipeline), so what is
/// measured is the emission, not whether routing later dropped it.</para>
/// </summary>
public class SubscriberReleaseIsNotAnnouncedBackTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>StreamEndedEvents the owner posted. Instance field — per-test lifetime.</summary>
    private int announced;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id).WithInitialData(MyData.InitialData))))
            // Passive counter on the owner's outbound path: counts, never alters.
            .AddPostPipeline(p => p.AddPipeline((d, next) =>
            {
                if (d.Message is StreamEndedEvent)
                    Interlocked.Increment(ref announced);
                return next.Invoke(d);
            }));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))));

    /// <summary>
    /// 🚨 <b>THE REGRESSION.</b> The subscriber disposes its mirror (→ <see cref="UnsubscribeRequest"/>),
    /// the owner's <c>sync/</c> hub finishes disposing — and nothing is announced back. RED before the
    /// fix: exactly one <see cref="StreamEndedEvent"/> per release.
    /// </summary>
    [HubFact]
    public async Task ASubscriberRelease_IsNotAnnouncedBackToTheSubscriber()
    {
        var (ownerSyncHub, clientStream) = await OpenMirrorAndFindOwnerSide();

        clientStream.Dispose();
        await ownerSyncHub.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the subscriber's UnsubscribeRequest must dispose the owner-side sync hub — the end "
            + "whose announcement is under test");

        Volatile.Read(ref announced).Should().Be(0,
            "the subscriber asked for this end, and its own sync hub is already gone: an "
            + "announcement reaches nobody, and a bulk release turns one per stream into a flood "
            + "on the subscriber's mesh hub (#5532)");
    }

    /// <summary>
    /// 🚨 <b>THE CONTROL, and it is not optional.</b> An end the subscriber did NOT ask for — the
    /// owner disposes its side, the route <c>Workspace.EvictClientSubscriptions</c> and an idle
    /// release take — is still announced. Without this arm the regression above would also pass on
    /// a build where the announcement was deleted outright, or where this test's counter saw
    /// nothing.
    /// </summary>
    [HubFact]
    public async Task AnOwnerSideEnd_IsStillAnnounced()
    {
        var (ownerSyncHub, _) = await OpenMirrorAndFindOwnerSide();

        ownerSyncHub.Dispose();
        await ownerSyncHub.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the owner-side sync hub must finish disposing");

        Volatile.Read(ref announced).Should().Be(1,
            "an owner-side end is exactly what StreamEndedEvent exists for (#2191): the subscriber "
            + "is still attached and would otherwise keep serving its last snapshot");
    }

    private async Task<(IMessageHub OwnerSyncHub, ISynchronizationStream<EntityStore> ClientStream)>
        OpenMirrorAndFindOwnerSide()
    {
        var host = GetHost();
        var client = GetClient();
        var collectionName = host.GetWorkspace().DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var stream = client.GetWorkspace()
            .GetRemoteStream<EntityStore>(CreateHostAddress(), new CollectionsReference(collectionName));

        await stream.Should().Within(TestTimeouts.Convergence).Emit(
            "the mirror must be live before its end can be measured");

        var ownerSyncHub = host.GetHostedHub(
            SynchronizationAddress.Create(stream.StreamId), HostedHubCreation.Never);
        ownerSyncHub.Should().NotBeNull(
            "the owner hosts one sync/{id} sub-hub per subscriber — without it this test measures "
            + "nothing");
        Volatile.Read(ref announced).Should().Be(0, "nothing has ended yet");
        return (ownerSyncHub!, stream);
    }
}
