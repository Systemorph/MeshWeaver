using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Covers the <see cref="IEventContinuationHandler"/> extension point — how
/// <see cref="EventSubscriptionRunner"/> fires a continuation whose effect lives ABOVE it in the
/// assembly graph (<see cref="EventContinuationType.PublishSocialPost"/>, implemented in
/// <c>MeshWeaver.Social</c>, which references <c>MeshWeaver.Graph</c>).
///
/// <para>The three behaviours pinned here are the ones whose absence is invisible in production: a
/// timed publish actually REACHES its handler, a failing publish is RECORDED rather than swallowed,
/// and a continuation with no handler registered FAILS LOUDLY instead of sitting Pending forever.
/// That last shape is what a forgotten <c>AddSocial</c> produces, and it looks exactly like a post
/// that was scheduled and never went out.</para>
/// </summary>
public class EventContinuationHandlerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PostPath = "PublishSpace";

    /// <summary>Records what the runner dispatched, and can be told to fail — an instance, registered
    /// into the mesh's DI, never a static: a static would leak across the parallel test classes below.</summary>
    private sealed class RecordingHandler : IEventContinuationHandler
    {
        public EventContinuationType ContinuationType => EventContinuationType.PublishSocialPost;
        public string? SeenTargetPath { get; private set; }
        public int Calls { get; private set; }
        public string? FailWith { get; set; }

        // subjectId is unused: a timed publish acts on the subscription's own TargetPath and has no
        // triggering subject. Named _ so that is explicit rather than an oversight (IDE0060).
        public IObservable<MeshNode> Execute(EventSubscription subscription, string _)
        {
            Calls++;
            SeenTargetPath = subscription.TargetPath;
            return FailWith is { } reason
                ? Observable.Throw<MeshNode>(new InvalidOperationException(reason))
                : Observable.Return(new MeshNode(subscription.TargetPath!));
        }
    }

    private readonly RecordingHandler handler = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(PostPath) { Name = "Publish Space", NodeType = "Space" })
            .ConfigureServices(services => services.AddSingleton<IEventContinuationHandler>(handler));

    /// <summary>A due timer whose continuation belongs to another assembly reaches that assembly's
    /// registered handler, and the subscription completes as <c>Fired</c>. This is the whole path
    /// behind "a post's scheduled slot publishes it".</summary>
    [Fact(Timeout = 60000)]
    public async Task DueTimer_DispatchesToTheRegisteredHandler_AndFires()
    {
        var subscription = await ArmDueTimer();

        var final = await AwaitSettled(subscription.Id);

        Assert.True(final.Status == EventSubscriptionStatus.Fired,
            $"subscription ended {final.Status}: {final.LastError}");
        Assert.Equal(1, handler.Calls);
        Assert.Equal(PostPath, handler.SeenTargetPath);
    }

    /// <summary>A handler that throws leaves the subscription <c>Failed</c> WITH the reason on
    /// <see cref="EventSubscription.LastError"/> — never <c>Fired</c>, and never a silent success. A
    /// publish that LinkedIn refuses has to be visible on the subscription; that is the only place
    /// anyone can find out why a slot passed with nothing posted.</summary>
    [Fact(Timeout = 60000)]
    public async Task HandlerFailure_IsRecordedOnTheSubscription_NotSwallowed()
    {
        handler.FailWith = "linkedin refused: missing-w_member_social-reconnect";

        var subscription = await ArmDueTimer();

        var final = await AwaitSettled(subscription.Id);

        Assert.True(final.Status == EventSubscriptionStatus.Failed,
            $"a refused publish must not report success — ended {final.Status}");
        Assert.Contains("missing-w_member_social-reconnect", final.LastError ?? string.Empty);
    }

    /// <summary>
    /// A REPEATING timer fires more than once and never reaches a terminal state: after each fire it
    /// records its next slot and stays Pending.
    ///
    /// <para>Both halves matter. Firing twice is the feature. Recording the next slot is what makes
    /// it survive a restart honestly — the pending-set reconcile re-schedules from the STORED FireAt,
    /// so a fire that did not advance it would replay on the next boot, and a nightly job would run
    /// again on every pod restart.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task RepeatingTimer_FiresAgain_AndRecordsItsNextSlot()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var changeFeed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.Timer,
            FireAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            RepeatEvery = TimeSpan.FromSeconds(2),
            ContinuationType = EventContinuationType.PublishSocialPost,
            TargetPath = PostPath,
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit();

        var runner = new EventSubscriptionRunner(Mesh, changeFeed, meshService, accessService,
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>());
        runners.Add(runner);
        await runner.StartAsync(default);

        // Fires repeatedly — a one-shot would stop at 1.
        var seen = await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id))
            .Select(_ => handler.Calls)
            .Where(calls => calls >= 2)
            .FirstAsync().Timeout(40.Seconds());
        Assert.True(seen >= 2, $"a repeating timer must fire again — fired {seen}×");

        // …and it is still armed, with its slot moved into the future.
        var current = (await Mesh.GetWorkspace()
            .GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id))
            .Select(n => n?.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
            .Where(x => x?.FireAt > DateTimeOffset.UtcNow)
            .FirstAsync().Timeout(20.Seconds()))!;
        Assert.Equal(EventSubscriptionStatus.Pending, current.Status);
        Assert.True(current.FireAt > DateTimeOffset.UtcNow,
            "a repeater records its NEXT slot, or a restart replays the old one");
    }

    private async Task<EventSubscription> ArmDueTimer()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var changeFeed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // FireAt in the past = the restart-safe path: due while nothing was running, fires on boot.
        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.Timer,
            FireAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            ContinuationType = EventContinuationType.PublishSocialPost,
            TargetPath = PostPath,
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit();

        var runner = new EventSubscriptionRunner(Mesh, changeFeed, meshService, accessService,
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>());
        runners.Add(runner);
        await runner.StartAsync(default);
        return subscription;
    }

    private async Task<EventSubscription> AwaitSettled(string id) =>
        (await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(id))
            .Select(n => n?.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(40.Seconds()))!;

    private readonly List<EventSubscriptionRunner> runners = [];

    public override async ValueTask DisposeAsync()
    {
        foreach (var runner in runners)
            runner.Dispose();
        await base.DisposeAsync();
    }
}

/// <summary>
/// The forgotten-registration case, which needs a mesh where NO handler is registered — so it is its
/// own class rather than a flag on the one above.
/// </summary>
public class EventContinuationHandlerMissingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>With no handler for the continuation, the subscription must end <c>Failed</c>. The
    /// alternative — sitting <c>Pending</c> forever — is indistinguishable from a slot that simply has
    /// not arrived yet, which is precisely how a post can look scheduled for a day and never go out.</summary>
    [Fact(Timeout = 60000)]
    public async Task UnregisteredContinuation_FailsLoudly_RatherThanSittingPending()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var changeFeed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.Timer,
            FireAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            ContinuationType = EventContinuationType.PublishSocialPost,
            TargetPath = "SomePost",
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit();

        using var runner = new EventSubscriptionRunner(Mesh, changeFeed, meshService, accessService,
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>());
        await runner.StartAsync(default);

        var final = (await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id))
            .Select(n => n?.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(40.Seconds()))!;

        Assert.Equal(EventSubscriptionStatus.Failed, final.Status);
    }
}
