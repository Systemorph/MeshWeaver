#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.ImageClosures;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A STANDING watch over a live query must survive a fault of that query. Each of these sites used
/// to subscribe its query once, with an error arm that only logged, so ONE provider stall ended the
/// watch until the process restarted — the only trace a single log line. Each test drives the site
/// through the same deterministic script: the first open throws a
/// <see cref="QueryProviderStalledException"/>, the watch survives, the re-open runs when the test's
/// manual clock says so (never on wall time), and a later emission is PROCESSED by the site.
/// <see cref="TheUnguardedShape_NeverReopens"/> is the negative control: the pre-fix shape, driven by
/// the same source and clock, opens once and never again.
/// </summary>
public class StandingWatchesReopenAfterAFaultTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SecretKey = "Test:PlatformWebhookSecret";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddWebhookInbox()
            .AddImageClosures()
            .ConfigureServices(services => services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SecretKey] = "cd-shared-secret",
                    ["WebhookInbox:Targets:0"] = ImageClosureNodes.InboxTarget,
                    ["WebhookInbox:Targets:0:SecretConfigKey"] = SecretKey,
                }).Build()));

    /// <summary>The test's clock: a scheduled re-open runs only when <see cref="FireNext"/> says so.</summary>
    private sealed class ManualReopenClock
    {
        private ImmutableQueue<Action> due = ImmutableQueue<Action>.Empty;
        private int scheduled;

        public int Scheduled => Volatile.Read(ref scheduled);

        public IDisposable Schedule(Action reopen)
        {
            ImmutableInterlocked.Enqueue(ref due, reopen);
            Interlocked.Increment(ref scheduled);
            return Disposable.Empty;
        }

        public bool FireNext()
        {
            if (!ImmutableInterlocked.TryDequeue(ref due, out var reopen))
                return false;
            reopen();
            return true;
        }
    }

    /// <summary>A live query whose FIRST open faults as a stalled provider does; every later open is live.</summary>
    private sealed class FlakySource<T>
    {
        private int opens;

        public Subject<T> Live { get; } = new();

        public int Opens => Volatile.Read(ref opens);

        public IObservable<T> Open() => Interlocked.Increment(ref opens) == 1
            ? Observable.Throw<T>(new QueryProviderStalledException("test-provider", TimeSpan.FromSeconds(1), "q", "system"))
            : Live;
    }

    private static MeshNode NodeAt(string path, object content)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0
            ? new MeshNode(path) { Content = content }
            : new MeshNode(path[(slash + 1)..], path[..slash]) { Content = content };
    }

    private static QueryResultChange<MeshNode> Initial(params MeshNode[] nodes) =>
        new() { ChangeType = QueryChangeType.Initial, Items = nodes };

    private Task Eventually(Func<bool> condition, string because) =>
        Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Where(_ => condition())
            .Should().Within(TestTimeouts.Convergence).Emit(because, TestContext.Current.CancellationToken);

    [Fact]
    public void InstanceSecretCatalog_FailsClosedThroughTheFault_ThenReopensAndServesKeysAgain()
    {
        var clock = new ManualReopenClock();
        var source = new FlakySource<QueryResultChange<MeshNode>>();
        using var catalog = new InstanceSecretCatalog(Mesh, null, source.Open, clock.Schedule);

        catalog.Start();

        source.Opens.Should().Be(1);
        catalog.Faulted.Should().BeTrue("the dead feed cannot see a revocation — per-sender keys are refused");
        clock.Scheduled.Should().Be(1, "the fault schedules a re-open instead of ending the watch");

        clock.FireNext().Should().BeTrue();
        source.Opens.Should().Be(2, "the re-open opens a FRESH query");
        catalog.Faulted.Should().BeTrue("still closed: the re-opened query has not listed the set yet");

        source.Live.OnNext(Initial(NodeAt(InstanceSecrets.PathOf("Test:Key"),
            new InstanceSecretContent { ConfigKey = "Test:Key" })));

        catalog.Faulted.Should().BeFalse("the re-opened query's full listing ends the fail-closed window");
        catalog.Entry("Test:Key").Should().NotBeNull("a listing after the fault is processed");
    }

    [Fact]
    public void UiContributionCatalog_ReopensAfterAFault_AndTakesTheNextListing()
    {
        var clock = new ManualReopenClock();
        var source = new FlakySource<QueryResultChange<MeshNode>>();
        using var catalog = new UiContributionCatalog(Mesh, null, source.Open, clock.Schedule);

        catalog.Current.Should().BeEmpty("the fault answers the last known set — empty");
        clock.Scheduled.Should().Be(1);

        clock.FireNext().Should().BeTrue();
        source.Opens.Should().Be(2);
        source.Live.OnNext(Initial(NodeAt("Plugin/Contribution", new UiContribution())));

        catalog.Current.Should().ContainSingle("the re-opened query's listing replaces the frozen set");
    }

    [Fact]
    public void PartitionRegistry_NeverCachesAFault_TheNextLookupReopens()
    {
        var source = new FlakySource<QueryResultChange<MeshNode>>();
        var registry = new PartitionRegistry(Mesh) { OpenQueryOverride = source.Open };
        var first = new List<PartitionDefinition?>();
        using (registry.GetPartition("Acme").Subscribe(first.Add, _ => { }))
        {
        }
        first.Should().ContainSingle("the faulted lookup answers its caller once")
            .Which.Should().BeNull("a fault reads as 'no definition' for that caller");

        var second = new List<PartitionDefinition?>();
        using var live = registry.GetPartition("Acme").Subscribe(second.Add, _ => { });
        source.Opens.Should().Be(2, "the fault was evicted, so the next lookup opens a fresh query");

        source.Live.OnNext(Initial(NodeAt("Admin/Partition/Acme", new PartitionDefinition { Namespace = "Acme" })));
        second.Should().ContainSingle().Which!.Namespace.Should().Be("Acme");
    }

    [Fact(Timeout = 60000)]
    public async Task EventSubscriptionRunner_BothStandingQueriesReopen_AndThePendingSetIsProcessed()
    {
        var clock = new ManualReopenClock();
        var pendingSource = new FlakySource<IEnumerable<MeshNode>>();
        var legacySource = new FlakySource<IEnumerable<MeshNode>>();
        using var runner = new EventSubscriptionRunner(
            Mesh, new SilentChangeFeed(),
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh.ServiceProvider.GetRequiredService<AccessService>())
        {
            OpenQueryOverride = (id, _) => id == "event-subscriptions" ? pendingSource.Open() : legacySource.Open(),
            ScheduleReopen = clock.Schedule,
        };

        await runner.StartAsync(TestContext.Current.CancellationToken);

        clock.Scheduled.Should().Be(2, "both standing queries faulted and both scheduled a re-open");
        clock.FireNext().Should().BeTrue();
        clock.FireNext().Should().BeTrue();
        pendingSource.Opens.Should().Be(2);
        legacySource.Opens.Should().Be(2);
        legacySource.Live.HasObservers.Should().BeTrue("the legacy migration watch is live again");

        var subscription = new EventSubscription
        {
            Id = "reopened",
            TriggerType = EventTriggerType.NodeChange,
            TriggerNodeType = "Nothing",
            TriggerKind = MeshChangeKind.Updated,
            Status = EventSubscriptionStatus.Pending,
        };
        pendingSource.Live.OnNext([NodeAt(EventSubscriptionNodeType.Path(subscription.Id), subscription)]);

        runner.PendingSnapshot.Select(s => s.Id).Should().Equal("reopened");
    }

    [Fact(Timeout = 60000)]
    public async Task ImageClosureIngest_InboxWatchReopens_AndDrainsTheNextDelivery()
    {
        var clock = new ManualReopenClock();
        var source = new FlakySource<IEnumerable<MeshNode>>();
        using var ingest = new ImageClosureIngest(Mesh,
            Mesh.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ImageClosureIngest>>())
        {
            OpenInboxOverride = source.Open,
            ScheduleReopen = clock.Schedule,
        };

        await ingest.StartAsync(TestContext.Current.CancellationToken);
        await Eventually(() => clock.Scheduled == 1, "the index is ensured, the watch opens and faults, a re-open is scheduled");

        clock.FireNext().Should().BeTrue();
        source.Opens.Should().Be(2);
        var delivery = $"{ImageClosureNodes.InboxTarget}/{WebhookInbox.InboxContainer}/after-the-fault";
        source.Live.OnNext([NodeAt(delivery, "not a webhook event")]);

        ingest.HasClaimed(delivery).Should().BeTrue("a delivery after the fault is picked up");
    }

    /// <summary>
    /// The negative control: the pre-fix shape — subscribe once, log the error — driven by the same
    /// flaky source and clock. It opens once and nothing ever opens it again, so a later emission is
    /// never seen. Every test above passes only because its site no longer has this shape.
    /// </summary>
    [Fact]
    public void TheUnguardedShape_NeverReopens()
    {
        var clock = new ManualReopenClock();
        var source = new FlakySource<QueryResultChange<MeshNode>>();
        var seen = 0;
        Exception? fault = null;

        using var _ = source.Open().Subscribe(_ => seen++, ex => fault = ex);

        fault.Should().BeOfType<QueryProviderStalledException>();
        clock.Scheduled.Should().Be(0, "nothing schedules a re-open");
        clock.FireNext().Should().BeFalse();
        source.Opens.Should().Be(1);
        source.Live.OnNext(Initial());
        seen.Should().Be(0, "the watch is dead: the later listing is never processed");
    }

    private sealed class SilentChangeFeed : IMeshChangeFeed
    {
        public void Publish(MeshChangeEvent change) { }
        public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
            => Disposable.Empty;
    }
}
