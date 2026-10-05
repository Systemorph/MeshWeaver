using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the SERVED half of the owner-side heartbeat liveness check (#6047, #6109 review 4184438049).
/// A subscriber's acknowledged sync stream names itself on every heartbeat; the owner answers a
/// named heartbeat with <see cref="StreamEndedEvent"/> exactly when
/// <c>Workspace.ServesClientSubscription</c> misses. If the registration key and the heartbeat key
/// ever diverged (a normalised or impersonated sender on one side, the raw address on the other),
/// every heartbeat for a HEALTHY stream would read as unserved and the subscriber would re-subscribe
/// once per interval. A delivery assertion cannot see that (a flapping stream still delivers), so
/// this counts the owner's answers directly.
///
/// <para>Two cases over one instrument. The served stream's named heartbeats get silence. A named
/// heartbeat for a stream the owner does NOT serve gets a <see cref="StreamEndedEvent"/>, which
/// proves the counter can fire, so the zero in the first case is a reading, not a dead
/// instrument.</para>
/// </summary>
public class AServedStreamsNamedHeartbeatIsAnsweredWithSilenceTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly TimeSpan ShortHeartbeat = TimeSpan.FromMilliseconds(200);

    private int namedHeartbeats;
    private int streamEndedAnswers;

    /// <summary>The owner's data.</summary>
    public record ServedItem(string Id);

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithHeartBeatHandler()
            .AddDeliveryPipeline(p => p.AddPipeline((d, ct, next) =>
            {
                if (d.Message is HeartBeatEvent { StreamId.Length: > 0 })
                    Interlocked.Increment(ref namedHeartbeats);
                return next.Invoke(d, ct);
            }))
            .AddPostPipeline(p => p.AddPipeline((d, next) =>
            {
                if (d.Message is StreamEndedEvent)
                    Interlocked.Increment(ref streamEndedAnswers);
                return next.Invoke(d);
            }))
            .AddData(data => data.AddSource(src => src
                .WithType<ServedItem>(t => t.WithInitialData(
                    _ => Observable.Return(new[] { new ServedItem("served") }.AsEnumerable())))));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithServices(services => services.Configure<SyncStreamOptions>(o =>
            {
                o.HeartbeatInterval = ShortHeartbeat;
                o.FirstHeartbeat = ShortHeartbeat;
            }))
            .AddData(data => data.AddHubSource(CreateHostAddress(), ds => ds.WithType<ServedItem>()));

    /// <summary>
    /// A stream the owner serves: several named heartbeats arrive and not one is answered with an
    /// end. Then a heartbeat naming an unknown stream IS answered, so the counter is live.
    /// </summary>
    [HubFact]
    public async Task NamedHeartbeatsForAServedStream_GetNoEnd_AnUnservedOneDoes()
    {
        GetHost();
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();

        await workspace.GetObservable<ServedItem>("served")
            .Where(d => d is not null)
            .Should().Within(15.Seconds())
            .Match(d => d!.Id == "served");

        await Observable.Interval(50.Milliseconds())
            .Select(_ => Volatile.Read(ref namedHeartbeats))
            .Should().Within(15.Seconds())
            .Match(c => c >= 3);

        Volatile.Read(ref streamEndedAnswers).Should().Be(0,
            "the owner serves this stream: a named heartbeat for it must be answered with silence, "
            + "or every heartbeat interval would end and re-subscribe a healthy stream");

        client.Post(new HeartBeatEvent { StreamId = "no-such-stream" }, o => o.WithTarget(CreateHostAddress()));

        await Observable.Interval(50.Milliseconds())
            .Select(_ => Volatile.Read(ref streamEndedAnswers))
            .Should().Within(10.Seconds())
            .Match(c => c >= 1);
    }
}
