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
            Mesh, Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>(),
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

    /// <summary>A live query that hands out a FRESH stream per open, so the test can fault one opening.</summary>
    private sealed class ReopenableSource<T>
    {
        private ImmutableList<Subject<T>> opened = [];

        public IReadOnlyList<Subject<T>> Opened => opened;

        public IObservable<T> Open()
        {
            var live = new Subject<T>();
            ImmutableInterlocked.Update(ref opened, list => list.Add(live));
            return live;
        }
    }

    /// <summary>
    /// A fault in the MIDDLE of a drain must not strand the deliveries that listing already claimed:
    /// the opening's queue dies with it, so A (in flight) and B (queued behind A) are released and the
    /// re-opened watch drains both. Before the fix both stayed claimed and B was never drained.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ImageClosureIngest_AFaultMidDrain_ReleasesTheClaimsItsListingHadNotDrained()
    {
        var clock = new ManualReopenClock();
        var source = new ReopenableSource<IEnumerable<MeshNode>>();
        var drained = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var inbox = $"{ImageClosureNodes.InboxTarget}/{WebhookInbox.InboxContainer}";
        string a = $"{inbox}/a", b = $"{inbox}/b";
        var firstOpening = true;
        using var ingest = new ImageClosureIngest(Mesh,
            Mesh.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ImageClosureIngest>>())
        {
            OpenInboxOverride = source.Open,
            ScheduleReopen = clock.Schedule,
            DrainOverride = node =>
            {
                drained.Enqueue(node.Path);
                // In the first opening A's drain never finishes, so B waits behind it.
                return firstOpening && node.Path == a ? Observable.Never<string?>() : Observable.Return<string?>(node.Path);
            },
        };

        await ingest.StartAsync(TestContext.Current.CancellationToken);
        await Eventually(() => source.Opened.Count == 1 && source.Opened[0].HasObservers,
            "the index is ensured and the inbox watch is subscribed");
        source.Opened[0].OnNext([NodeAt(a, "x"), NodeAt(b, "x")]);
        drained.Should().Equal([a], "B is queued behind A's unfinished drain");

        firstOpening = false;
        source.Opened[0].OnError(new QueryProviderStalledException("test-provider", TimeSpan.FromSeconds(1), "q", "system"));
        clock.Scheduled.Should().Be(1, "the listing's fault ends the opening at once, even with A's drain still in flight");
        clock.FireNext().Should().BeTrue();
        await Eventually(() => source.Opened.Count == 2 && source.Opened[1].HasObservers,
            "the re-opened inbox watch is subscribed");
        source.Opened[1].OnNext([NodeAt(a, "x"), NodeAt(b, "x")]);

        drained.Should().Equal([a, a, b], "both claims of the dead opening were released and are drained now");
    }

    /// <summary>
    /// An INCOMPLETE listing (a provider that did not answer) is a fault, never a recovery: it may be
    /// missing the entry that DISABLES a configured key. Folding it used to end the fail-closed window
    /// and drop that entry, so the configured fallback verified again.
    /// </summary>
    [Fact]
    public void InstanceSecretCatalog_AnIncompleteRecoveryListing_NeverReEnablesADisabledConfiguredKey()
    {
        const string section = "Test:Inbox";
        const string sender = "mounted";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{section}:{sender}"] = "the-mounted-key",
        }).Build();
        var clock = new ManualReopenClock();
        var openings = new List<Subject<QueryResultChange<MeshNode>>>();
        IObservable<QueryResultChange<MeshNode>> Open()
        {
            var live = new Subject<QueryResultChange<MeshNode>>();
            openings.Add(live);
            return live;
        }
        using var catalog = new InstanceSecretCatalog(Mesh, null, Open, clock.Schedule);
        catalog.Start();
        var disabled = NodeAt(InstanceSecrets.PathOf($"{section}:{sender}"),
            new InstanceSecretContent { ConfigKey = $"{section}:{sender}", Disabled = true });
        openings[0].OnNext(Initial(disabled));
        InstanceSecrets.Candidates(catalog, configuration, section).Should()
            .BeEmpty("the portal DISABLED the mounted key — precondition");

        openings[0].OnError(new QueryProviderStalledException("test-provider", TimeSpan.FromSeconds(1), "q", "system"));
        clock.FireNext().Should().BeTrue();
        openings[1].OnNext(new QueryResultChange<MeshNode>
        {
            ChangeType = QueryChangeType.Initial,
            Items = [],
            SilentProviders = ["test-provider"],
        });

        catalog.Faulted.Should().BeTrue("an incomplete listing is not the recovery that ends the fail-closed window");
        InstanceSecrets.Candidates(catalog, configuration, section).Should()
            .BeEmpty("a listing that may be missing the disabling entry must never re-enable the configured key");
        clock.Scheduled.Should().Be(2, "the incomplete listing is refused as a fault and the query re-opens");

        clock.FireNext().Should().BeTrue();
        openings[2].OnNext(Initial(disabled));
        catalog.Faulted.Should().BeFalse("a COMPLETE listing ends the window");
        InstanceSecrets.Candidates(catalog, configuration, section).Should().BeEmpty("and the key stays disabled");
    }

    /// <summary>
    /// The inbox secret is read PER DELIVERY. A key rotated after the watch opened — the endpoint
    /// already accepting deliveries signed with the new key — must verify those deliveries, not
    /// refuse and DELETE them with the retired key the opening started with.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ImageClosureIngest_AKeyRotatedAfterTheWatchOpened_StillVerifiesTheDelivery()
    {
        var configuration = Mesh.ServiceProvider.GetRequiredService<IConfiguration>();
        var original = configuration[SecretKey];
        var source = new ReopenableSource<IEnumerable<MeshNode>>();
        using var ingest = new ImageClosureIngest(Mesh,
            Mesh.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ImageClosureIngest>>())
        {
            OpenInboxOverride = source.Open,
            ScheduleReopen = new ManualReopenClock().Schedule,
        };
        try
        {
            await ingest.StartAsync(TestContext.Current.CancellationToken);
            await Eventually(() => source.Opened.Count == 1 && source.Opened[0].HasObservers,
                "the inbox watch is subscribed with the ORIGINAL key in force");

            const string rotated = "the-rotated-key";
            configuration[SecretKey] = rotated;
            var digest = "sha256:" + new string('e', 64);
            var body = ClosureRecord(digest);
            var delivery = NodeAt($"{ImageClosureNodes.InboxTarget}/{WebhookInbox.InboxContainer}/rotated",
                new WebhookEvent
                {
                    ReceivedAt = DateTimeOffset.UtcNow,
                    ContentType = "application/json",
                    Body = body,
                    Headers = ImmutableDictionary<string, string>.Empty.Add(WebhookInbox.SignatureHeader, Sign(body, rotated)),
                });
            source.Opened[0].OnNext([delivery]);

            var written = ImageClosureNodes.PathOf("memex-portal-ai", digest);
            var parent = written[..written.LastIndexOf('/')];
            var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
            var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .SelectMany(_ => access.RunAsSystem(() => meshService
                    .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{parent} scope:children")).Take(1)))
                .Where(c => c.Items.Any(n => n.Path == written))
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the delivery verified with the key in force when it was drained, and its record was written",
                    TestContext.Current.CancellationToken);
        }
        finally
        {
            configuration[SecretKey] = original;
        }
    }

    private static string Sign(string body, string secret) =>
        "sha256=" + Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private static string ClosureRecord(string digest) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = ImageClosureRecord.EventName,
            repository = "memex-portal-ai",
            digest,
            tags = new[] { "stg-9" },
            platformCommit = "0123abc",
            platformVersion = "3.1.10400",
            frameworkIdentity = "c003e001",
            runUrl = "https://github.com/Systemorph/MeshWeaver/actions/runs/1",
            platforms = new[]
            {
                new
                {
                    rid = "linux-x64",
                    fileCount = 1,
                    manifestSha256 = new string('c', 64),
                    files = new[] { new { path = "Lib0.dll", sha256 = new string('0', 64), bytes = 100L } },
                },
            },
        });

    /// <summary>
    /// A listing callback racing the opening's teardown must neither throw (an enqueue into the
    /// disposed queue escaped every error handler) nor leave claims behind (claims registered after
    /// teardown released the rest were stranded until the next boot). Driven deterministically: the
    /// claim itself tears the opening down, so the callback is guaranteed to be mid-flight.
    /// </summary>
    [Fact]
    public void InboxDrainWatch_ACallbackRacingTeardown_NeitherThrowsNorStrandsItsClaims()
    {
        var listing = new Subject<IEnumerable<MeshNode>>();
        var claimed = ImmutableHashSet<string>.Empty;
        var drained = 0;
        IDisposable? opening = null;
        IEnumerable<MeshNode> Claim(IEnumerable<MeshNode> nodes)
        {
            opening!.Dispose();   // teardown lands while this callback is claiming
            var fresh = nodes.ToArray();
            foreach (var node in fresh)
                claimed = claimed.Add(node.Path);
            return fresh;
        }

        opening = InboxDrainWatch.Open(
                () => listing,
                Claim,
                released => claimed = claimed.Except(released),
                _ => { drained++; return Observable.Return(System.Reactive.Unit.Default); })
            .Subscribe(_ => { }, _ => { });

        var raced = () => listing.OnNext([NodeAt("Inbox/_Inbox/a", "x"), NodeAt("Inbox/_Inbox/b", "x")]);

        raced.Should().NotThrow("an enqueue after teardown must never escape as ObjectDisposedException");
        claimed.Should().BeEmpty("claims made by a callback that lost the race are released, never stranded");
        drained.Should().Be(0, "nothing is drained by an opening that has been torn down");
    }

    /// <summary>
    /// A drain that runs SYNCHRONOUSLY inside the enqueue can tear the opening down mid-batch. For a
    /// claimed batch [A, B], the teardown inside A's drain must release B as well — B was claimed but
    /// not yet enqueued — and the loop must not enqueue B into the disposed queue. Before the fix B
    /// was registered only as it was enqueued, so teardown released A alone, B's enqueue threw
    /// ObjectDisposedException, and B stayed claimed until a restart.
    /// </summary>
    [Fact]
    public void InboxDrainWatch_ATeardownInsideTheFirstDrain_ReleasesTheRestOfTheBatch()
    {
        var listing = new Subject<IEnumerable<MeshNode>>();
        var claimed = ImmutableHashSet<string>.Empty;
        var drained = new List<string>();
        IDisposable? opening = null;
        const string a = "Inbox/_Inbox/a", b = "Inbox/_Inbox/b";

        opening = InboxDrainWatch.Open(
                () => listing,
                nodes =>
                {
                    var fresh = nodes.ToArray();
                    foreach (var node in fresh)
                        claimed = claimed.Add(node.Path);
                    return fresh;
                },
                released => claimed = claimed.Except(released),
                node =>
                {
                    drained.Add(node.Path);
                    opening!.Dispose();   // A's synchronous drain tears the opening down
                    return Observable.Return(System.Reactive.Unit.Default);
                })
            .Subscribe(_ => { }, _ => { });

        var batch = () => listing.OnNext([NodeAt(a, "x"), NodeAt(b, "x")]);

        batch.Should().NotThrow("B must never be enqueued into the queue A's drain disposed");
        drained.Should().Equal([a], "B is not drained by an opening that was torn down");
        claimed.Should().NotContain(b, "B's claim is released, so the re-opened watch drains it");
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
}
