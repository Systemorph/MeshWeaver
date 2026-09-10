using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 <b>A READ must not mint a permanent hub.</b> Systemorph/MeshWeaver#3432.
///
/// <para><b>The mechanism.</b> <c>Workspace._localStreamCache</c> caches a plain reduce and
/// deliberately does NOT cache a CONFIGURED one, because a caller-supplied configuration is
/// caller-specific (client id, subscriber, init callback). The read handlers, however, supplied a
/// CONSTANT — <c>x =&gt; x.ReturnNullWhenNotPresent()</c> — so every
/// <c>GetDataRequest</c> took the uncached branch, and every uncached reduce constructs a
/// <c>SynchronizationStream</c>, hence a hosted <c>sync/{id}</c> sub-hub with its own Autofac
/// scope, <c>TypeRegistry</c> and <c>JsonSerializerOptions</c> (~390 KB), registered for disposal on
/// its HUB-LIFETIME parent (the data source's <c>EntityStore</c> stream). One permanent hub per
/// read, released only when the owning node hub dies.</para>
///
/// <para><b>Measured on production before the fix</b> (memex.meshweaver.cloud, 2026-09-10): six
/// <c>GetDataRequest</c>s against ONE node hub took it from 8 to 14 <c>sync/</c> hubs — 1:1 — and the
/// replica was gaining ~16 <c>sync/</c> hubs a minute (≈985/h, ≈385 MB/h). The fix keys the
/// null-when-absent flag into the cache (<see cref="IWorkspace.GetNullableStream{TReduced}"/>) so the
/// population is bounded by DISTINCT REFERENCES instead of by reads.</para>
///
/// <para>🚨 <b>Both directions are asserted.</b>
/// <see cref="AnUncachedConfiguredReduce_MintsOneSyncHubPerCall"/> drives the OLD shape — the
/// uncached configured overload — through the same counter and shows it grow, which is what makes
/// the flat reading in <see cref="ReadingTheSameReferenceRepeatedly_DoesNotMintASyncHubPerRead"/>
/// mean something: without it, "the count did not move" would also pass on a build where the counter
/// is blind. The reads' own responses are the second control: a fix that bounded the population by
/// breaking the read would fail on the data, not on the count.</para>
/// </summary>
public class ReadPathStreamMintingTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Reads driven per arm. Small on purpose — the defect is monotone, so a handful
    /// separates "one hub per read" (grows with N) from "one hub per reference" (flat).</summary>
    private const int Reads = 5;

    /// <summary>Long enough that no heartbeat resubscribe fires during the measurement, so every
    /// hub counted is one this test caused.</summary>
    private static readonly TimeSpan LongHeartbeat = TimeSpan.FromMinutes(5);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithServices(services => services
                .Configure<SyncStreamOptions>(o => o.HeartbeatInterval = LongHeartbeat))
            .AddData(data => data.AddSource(src => src
                .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))));

    /// <summary>
    /// 🚨 <b>THE FIX.</b> Five <c>GetDataRequest</c>s for the SAME reference leave the owner's
    /// <c>sync/</c> hub population where it was: the read path resolves the SHARED stream. Each read
    /// still answers with the data — the fix must not buy a flat count with a broken read.
    /// </summary>
    [HubFact]
    public async Task ReadingTheSameReferenceRepeatedly_DoesNotMintASyncHubPerRead()
    {
        var host = GetHost();
        var client = GetClient();
        var reference = new CollectionReference(nameof(BusinessUnit));

        // The first read builds the shared stream — measure from AFTER it, so the baseline is the
        // steady state and the assertion is about the reads that follow, not about the first one.
        await ReadAsync(client, reference, "the first read must answer before the baseline is taken");
        var baseline = LiveSyncHubs(host);

        for (var i = 1; i <= Reads; i++)
            await ReadAsync(client, reference, $"read {i} must still answer with the collection");

        var grown = LiveSyncHubs(host) - baseline;
        Output.WriteLine($"DIAG shared: reads={Reads} ownerSyncHubs=+{grown}");

        grown.Should().Be(0,
            $"the {Reads} reads all resolve the SAME (reference, null-when-absent) key, so the owner "
            + "keeps ONE reduced stream and ONE sync/ hub for them — before #3432's fix each read "
            + "took the uncached configured branch and left a permanent sync/ hub behind");
    }

    /// <summary>
    /// 🚨 <b>THE POSITIVE CONTROL FOR LIVENESS.</b> Sharing one stream between reads must not turn
    /// it into a frozen snapshot: a write landing after the reads is visible to the next read.
    /// </summary>
    [HubFact]
    public async Task TheSharedReadStream_StaysLive()
    {
        var host = GetHost();
        var client = GetClient();
        var reference = new CollectionReference(nameof(BusinessUnit));

        var before = await ReadAsync(client, reference, "the initial read must answer");
        before.Instances.Should().HaveCount(TestData.BusinessUnits.Length);

        await client.Observe(
                DataChangeRequest.Update([new BusinessUnit("3", "Third")]),
                o => o.WithTarget(CreateHostAddress()))
            .Should().Within(TestTimeouts.Convergence).Emit();

        var settled = await Observable.Interval(PollInterval).StartWith(0L)
            .SelectMany(_ => ReadObservable(client, reference).Take(1))
            .Should().Within(TestTimeouts.Convergence)
            .Match(c => c.Instances.Count == TestData.BusinessUnits.Length + 1,
                "the shared read stream is LIVE — a write after the read is visible to the next one, "
                + "so bounding the sync/ hub population did not freeze the mirror");

        Output.WriteLine($"DIAG live: instances={settled.Instances.Count}");
    }

    /// <summary>
    /// A successful unified update is a read-after-write boundary. The owning data source and the
    /// already-warmed shared read stream are separate actors, so the owner can commit while the
    /// read actor still carries the old entity. The response must wait for that actor's matching
    /// post-baseline frame.
    /// </summary>
    [HubFact]
    public async Task UnifiedUpdate_WaitsUntilTheSharedReadStreamCarriesTheUpdate()
    {
        var host = GetHost();
        var client = GetClient();
        var workspace = host.ServiceProvider.GetRequiredService<IWorkspace>();
        var readStream = workspace.GetNullableStream(new EntityReference(nameof(BusinessUnit), "1"))
                         ?? throw new InvalidOperationException(
                             "The BusinessUnit entity reference did not resolve to a stream.");

        await readStream.Should().Within(TestTimeouts.Convergence)
            .Match(item => item.Value is BusinessUnit { DisplayName: not "Updated display" },
                "the shared read stream must carry the original entity before its actor is parked");

        var blockerEntered = new AsyncSubject<System.Reactive.Unit>();
        var releaseBlocker = 0;
        Exception? blockerException = null;
        readStream.Hub.InvokeAsync(
            _ =>
            {
                blockerEntered.OnNext(System.Reactive.Unit.Default);
                blockerEntered.OnCompleted();
                if (!SpinWait.SpinUntil(
                        () => Volatile.Read(ref releaseBlocker) == 1,
                        TestTimeouts.Convergence))
                    throw new TimeoutException("The test did not release the shared-read-stream actor.");
                return Task.CompletedTask;
            },
            ex =>
            {
                Interlocked.CompareExchange(ref blockerException, ex, null);
                Volatile.Write(ref releaseBlocker, 1);
                return Task.CompletedTask;
            });

        await blockerEntered.Should().Within(TestTimeouts.Quick)
            .Emit("the shared stream actor must be occupied before the update is issued");

        var answers = new ReplaySubject<UpdateUnifiedReferenceResponse>(1);
        using var answerSubscription = client
            .Observe(
                new UpdateUnifiedReferenceRequest(
                    $"data:{nameof(BusinessUnit)}/1",
                    new BusinessUnit("1", "Updated display")),
                o => o.WithTarget(CreateHostAddress()))
            .Select(delivery => delivery.Message)
            .Subscribe(answers);

        try
        {
            var source = workspace.DataContext.DataSourcesByCollection[nameof(BusinessUnit)]
                .GetStreamForPartition(null)!;
            await source.Should().Within(TestTimeouts.Convergence).Match(item =>
                    item.Value?.GetCollection(nameof(BusinessUnit))?.Instances.GetValueOrDefault("1")
                        is BusinessUnit { DisplayName: "Updated display" },
                "the owner must have applied the update while the shared read actor is still parked");

            await answers.Should().NotEmit(TimeSpan.FromMilliseconds(300),
                "owner commit alone is not enough: success must wait for the read view's queued update frame");
        }
        finally
        {
            Volatile.Write(ref releaseBlocker, 1);
        }

        var answer = await answers.Should().Within(TestTimeouts.Convergence)
            .Emit("releasing the shared read actor lets the matching frame land and completes the update");
        Volatile.Read(ref blockerException).Should().BeNull(
            "the parked actor turn must complete normally; swallowing a fault would make the ordering assertion vacuous");
        answer.Success.Should().BeTrue();
        readStream.Current?.Value.Should().BeOfType<BusinessUnit>()
            .Which.DisplayName.Should().Be("Updated display");
    }

    /// <summary>
    /// The delete acknowledgement is subscribed before the eager write. This pins the ordering in
    /// which the delete's null frame is followed by a legitimate same-ID recreation before the
    /// delete result is consumed; the committed delete must still answer instead of waiting forever
    /// on a null frame that has already been replaced in the shared replay slot.
    /// </summary>
    [HubFact]
    public async Task UnifiedDelete_RetainsItsAbsenceAcknowledgementAcrossSameIdRecreation()
    {
        var host = GetHost();
        var client = GetClient();
        var workspace = host.ServiceProvider.GetRequiredService<IWorkspace>();
        var readStream = workspace.GetNullableStream(new EntityReference(nameof(BusinessUnit), "1"))
                         ?? throw new InvalidOperationException(
                             "The BusinessUnit entity reference did not resolve to a stream.");

        await readStream.Should().Within(TestTimeouts.Convergence)
            .Match(item => item.Value is BusinessUnit,
                "the initial entity must be visible before the shared read actor is parked");

        var blockerEntered = new AsyncSubject<System.Reactive.Unit>();
        var releaseBlocker = 0;
        readStream.Hub.InvokeAsync(() =>
        {
            blockerEntered.OnNext(System.Reactive.Unit.Default);
            blockerEntered.OnCompleted();
            if (!SpinWait.SpinUntil(
                    () => Volatile.Read(ref releaseBlocker) == 1,
                    TestTimeouts.Convergence))
                throw new TimeoutException("The test did not release the shared-read-stream actor.");
        });
        await blockerEntered.Should().Within(TestTimeouts.Quick)
            .Emit("the shared stream actor must be occupied before the delete is issued");

        var answers = new ReplaySubject<DeleteUnifiedReferenceResponse>(1);
        using var answerSubscription = client
            .Observe(
                new DeleteUnifiedReferenceRequest($"data:{nameof(BusinessUnit)}/1"),
                o => o.WithTarget(CreateHostAddress()))
            .Select(delivery => delivery.Message)
            .Subscribe(answers);

        try
        {
            var source = workspace.DataContext.DataSourcesByCollection[nameof(BusinessUnit)]
                .GetStreamForPartition(null)!;
            await source.Should().Within(TestTimeouts.Convergence).Match(item =>
                    item.Value?.GetCollection(nameof(BusinessUnit))?.Instances.ContainsKey("1") == false,
                "the owner must apply the delete before the same ID is recreated");

            await workspace.RequestChange(DataChangeRequest.Update(
                    [new BusinessUnit("1", "Recreated display")]))
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the owner must apply the legitimate same-ID recreation");
            await source.Should().Within(TestTimeouts.Convergence).Match(item =>
                    item.Value?.GetCollection(nameof(BusinessUnit))?.Instances.GetValueOrDefault("1")
                        is BusinessUnit { DisplayName: "Recreated display" },
                "both owner frames must be queued for the parked shared read actor");

            await answers.Should().NotEmit(TimeSpan.FromMilliseconds(300),
                "the delete response still waits for its own absence acknowledgement");
        }
        finally
        {
            Volatile.Write(ref releaseBlocker, 1);
        }

        var answer = await answers.Should().Within(TestTimeouts.Convergence)
            .Emit("the pre-armed absence observation retains the delete acknowledgement after recreation");
        answer.Success.Should().BeTrue();
        await readStream.Should().Within(TestTimeouts.Convergence)
            .Match(item => item.Value is BusinessUnit { DisplayName: "Recreated display" },
                "the recreation remains the latest state after the delete acknowledgement");
    }

    /// <summary>
    /// A successful unified delete is a read-after-write boundary: once it answers, the shared
    /// read stream must already carry absence. The data-source owner and its reduced read stream
    /// are separate actors, so applying the owner store only QUEUES the reduced null frame. Before
    /// the fix the handler answered at owner commit and an immediate read could replay the old
    /// entity — the exact failure in the plugin release gate immediately after #3432's shared-read
    /// change.
    ///
    /// This test parks the shared stream's actor after its initial entity arrived, then proves the
    /// owner has applied the deletion while the read view cannot yet consume it. The delete reply
    /// must remain absent until that actor is released. The park is the subject, so it uses the
    /// repository's bounded SpinWait + finally release pattern; no sleeps, retries, or widened
    /// production bounds.
    /// </summary>
    [HubFact]
    public async Task UnifiedDelete_WaitsUntilTheSharedReadStreamCarriesAbsence()
    {
        var host = GetHost();
        var client = GetClient();
        var workspace = host.ServiceProvider.GetRequiredService<IWorkspace>();
        var reference = new EntityReference(nameof(BusinessUnit), "1");
        var readStream = workspace.GetNullableStream(reference)
            ?? throw new InvalidOperationException("The BusinessUnit entity reference did not resolve to a stream.");

        await readStream
            .Should().Within(TestTimeouts.Convergence)
            .Match(item => item.Value is BusinessUnit { SystemName: "1" },
                "the shared read stream must carry the entity before its actor is parked");

        var blockerEntered = new AsyncSubject<System.Reactive.Unit>();
        var releaseBlocker = 0;
        Exception? blockerException = null;
        readStream.Hub.InvokeAsync(
            _ =>
            {
                blockerEntered.OnNext(System.Reactive.Unit.Default);
                blockerEntered.OnCompleted();
                if (!SpinWait.SpinUntil(
                        () => Volatile.Read(ref releaseBlocker) == 1,
                        TestTimeouts.Convergence))
                    throw new TimeoutException("The test did not release the shared-read-stream actor.");
                return Task.CompletedTask;
            },
            ex =>
            {
                Interlocked.CompareExchange(ref blockerException, ex, null);
                Volatile.Write(ref releaseBlocker, 1);
                return Task.CompletedTask;
            });

        await blockerEntered.Should().Within(TestTimeouts.Quick)
            .Emit("the shared stream actor must be occupied before the deletion is issued");

        var answers = new ReplaySubject<DeleteUnifiedReferenceResponse>(1);
        using var answerSubscription = client
            .Observe(
                new DeleteUnifiedReferenceRequest($"data:{nameof(BusinessUnit)}/1"),
                o => o.WithTarget(CreateHostAddress()))
            .Select(delivery => delivery.Message)
            .Subscribe(answers);

        try
        {
            var source = workspace.DataContext.DataSourcesByCollection[nameof(BusinessUnit)]
                .GetStreamForPartition(null)!;
            await source
                .Should().Within(TestTimeouts.Convergence)
                .Match(item => item.Value?.GetCollection(nameof(BusinessUnit))?.Instances.ContainsKey("1") == false,
                    "the owner must have applied the deletion while the shared read actor is still parked");

            await answers.Should().NotEmit(TimeSpan.FromMilliseconds(300),
                "owner commit alone is not enough: success must wait for the read view's queued null frame");
        }
        finally
        {
            Volatile.Write(ref releaseBlocker, 1);
        }

        var answer = await answers.Should().Within(TestTimeouts.Convergence)
            .Emit("releasing the shared read actor lets its null frame land and completes the delete");
        Volatile.Read(ref blockerException).Should().BeNull(
            "the parked actor turn must complete normally; swallowing a fault would make the ordering assertion vacuous");
        answer.Success.Should().BeTrue();
        readStream.Current?.Value.Should().BeNull(
            "a successful delete response is now a read-after-write consistency boundary");
    }

    /// <summary>
    /// 🚨 <b>THE CONTROL IN THE OTHER DIRECTION — the counter can see growth.</b> The uncached
    /// configured overload is what the read path used to call, and it is still the contract for a
    /// genuinely caller-specific configuration. Five calls, five new <c>sync/</c> hubs. If this ever
    /// goes flat, the counter has stopped measuring and the arm above is vacuous.
    /// </summary>
    [HubFact]
    public async Task AnUncachedConfiguredReduce_MintsOneSyncHubPerCall()
    {
        var host = GetHost();
        var workspace = host.ServiceProvider.GetRequiredService<IWorkspace>();
        var reference = new CollectionReference(nameof(BusinessUnit));

        await workspace.GetObservable<BusinessUnit>()
            .Should().Within(TestTimeouts.Convergence)
            .Match(x => x.Count > 0, "the data source must have served its initial snapshot");

        var baseline = LiveSyncHubs(host);
        for (var i = 0; i < Reads; i++)
            _ = workspace.GetStream(reference, x => x.WithClientId($"caller-{i}"));

        var grown = LiveSyncHubs(host) - baseline;
        Output.WriteLine($"DIAG uncached: calls={Reads} ownerSyncHubs=+{grown}");

        grown.Should().Be(Reads,
            "a CONFIGURED reduce is caller-specific and therefore uncached BY CONTRACT — each call "
            + "constructs a SynchronizationStream and its sync/ hub. This is the metric proving the "
            + "shared arm's flat reading is a real release and not a blind counter");
    }

    /// <summary>The poll interval for a convergence wait — not a timeout, so it is not a guessed
    /// bound; the bound is <see cref="TestTimeouts.Convergence"/> on the wait itself.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>One <c>GetDataRequest</c> round trip, asserting the answer carries the collection —
    /// the read's OWN positive control, so a flat hub count can never come from a broken read.</summary>
    private async Task<InstanceCollection> ReadAsync(
        IMessageHub client, CollectionReference reference, string because)
    {
        var delivery = await client
            .Observe(new GetDataRequest(reference), o => o.WithTarget(CreateHostAddress()))
            .Should().Within(TestTimeouts.Convergence).Emit(because);
        var response = delivery.Message.Should().BeOfType<GetDataResponse>().Which;
        response.Error.Should().BeNull(because);
        return response.Data.Should().BeOfType<InstanceCollection>().Which;
    }

    /// <summary>The same read as an observable, for a convergence wait — no Task bridge inside an
    /// Rx pipeline.</summary>
    private IObservable<InstanceCollection> ReadObservable(
        IMessageHub client, CollectionReference reference) =>
        client.Observe(new GetDataRequest(reference), o => o.WithTarget(CreateHostAddress()))
            .Select(d => (d.Message as GetDataResponse)?.Data as InstanceCollection)
            .Where(c => c is not null)
            .Select(c => c!);

    /// <summary>
    /// #3432's own metric, in-process: <c>SynchronizationStream</c>'s constructor mints exactly one
    /// hosted <c>sync/{ClientId}</c> hub per stream, so the owner's hosted-hub collection filtered to
    /// <see cref="SynchronizationAddress.AddressType"/> IS the count of streams it is keeping alive.
    /// </summary>
    private static int LiveSyncHubs(IMessageHub hub) =>
        hub.ServiceProvider.GetRequiredService<HostedHubsCollection>()
            .Hubs.Count(h => h.Address.Type == SynchronizationAddress.AddressType);
}
