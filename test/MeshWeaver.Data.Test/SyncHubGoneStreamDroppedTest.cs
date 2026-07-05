using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Companion to <see cref="SyncHubRegistrationRaceTest"/>: proves the fix does NOT mask a genuinely
/// gone stream (a disposed circuit / released read stream). The hold in
/// <c>DataExtensions.RouteStreamMessage</c> is BOUNDED by
/// <see cref="SyncStreamOptions.SyncHubRegistrationGrace"/> — a stream whose <c>sync/{id}</c> sub-hub
/// never registers is still dropped once the grace elapses, exactly as before (just this window
/// later). It is not buffered forever.
/// </summary>
public class SyncHubGoneStreamDroppedTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(500);

    public record Ping : IRequest<Pong>;
    public record Pong;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithServices(services =>
                services.Configure<SyncStreamOptions>(o => o.SyncHubRegistrationGrace = Grace))
            .AddData()
            .WithTypes(typeof(Ping), typeof(Pong))
            .WithHandler<Ping>((hub, request) =>
            {
                hub.Post(new Pong(), o => o.ResponseFor(request));
                return request.Processed();
            });

    [HubFact]
    public async Task GenuinelyGoneStream_IsDroppedAfterGrace_NotBufferedForever()
    {
        var subscriber = GetHost();
        var streamId = "gone-" + Guid.NewGuid().AsString();

        subscriber.Post(
            new DataChangedEvent(streamId, 1, new RawJson("{}"), ChangeType.Full, null),
            o => o.WithTarget(subscriber.Address));

        (await subscriber.Observe(new Ping(), o => o.WithTarget(subscriber.Address))
            .Should().Within(10.Seconds()).Emit())
            .Message.Should().BeOfType<Pong>();

        // Let the grace elapse so the held Full is dropped (sanctioned "confirm nothing happened" wait).
        await Task.Delay(Grace + TimeSpan.FromSeconds(1));

        // A sub-hub registered AFTER the grace must NOT receive the already-dropped Full.
        var received = new ReplaySubject<DataChangedEvent>(1);
        using var syncHub = subscriber.GetHostedHub(
            SynchronizationAddress.Create(streamId),
            c => c.WithTypes(typeof(DataChangedEvent))
                .WithHandler<DataChangedEvent>((_, d) => { received.OnNext(d.Message); return d.Processed(); }));

        var outcome = await received
            .Select(Notification.CreateOnNext)
            .Take(1).Timeout(TimeSpan.FromSeconds(1))
            .Catch((TimeoutException _) => Observable.Return(Notification.CreateOnCompleted<DataChangedEvent>()))
            .FirstAsync().ToTask();

        outcome.Kind.Should().Be(NotificationKind.OnCompleted,
            "a genuinely-gone stream's message must be dropped after the grace, not buffered and replayed later");
    }
}
