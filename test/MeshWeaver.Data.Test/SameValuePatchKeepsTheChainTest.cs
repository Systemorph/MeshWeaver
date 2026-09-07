using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Systemorph/MeshWeaver#3520. A Patch that changes nothing is still SENT by the owner and chained
/// onto by the next frame — a JsonElement payload written again is a new element (no value equality
/// on the struct), so the owner keeps the update and ships an EMPTY diff; the JSON mirror deep-compares
/// it as equal and skips it. Skipping without adopting the frame's version made the NEXT frame prove a
/// "frame loss" that never happened: the mirror re-asked for a snapshot it already held. Measured on
/// Education's disposable mesh: 164 of 164 loss warnings in one run were exactly this, none a real loss.
/// </summary>
public class SameValuePatchKeepsTheChainTest(ITestOutputHelper output) : HubTestBase(output)
{
    public record Blob(string Id, JsonElement Payload);

    private readonly ConcurrentQueue<(ChangeType Type, long Version, long BasedOn, string Change)> posted = new();
    private readonly ConcurrentQueue<SubscribeRequest> subscribeRequests = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<Blob>(t => t.WithKey(d => d.Id))))
            .AddPostPipeline(p => p.AddPipeline((d, next) =>
            {
                if (d.Message is DataChangedEvent e)
                    posted.Enqueue((e.ChangeType, e.Version, e.BasedOnVersion, e.Change.Content));
                return next.Invoke(d);
            }));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<Blob>(t => t.WithKey(d => d.Id))))
            .AddPostPipeline(p => p.AddPipeline((d, next) =>
            {
                if (d.Message is SubscribeRequest sr)
                    subscribeRequests.Enqueue(sr);
                return next.Invoke(d);
            }));

    private static JsonElement Payload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [HubFact]
    public async Task ANoOpPatch_IsNotAFrameLoss_TheMirrorAdoptsItsVersion()
    {
        var host = GetHost();
        var client = GetClient();
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });
        var collectionName = host.GetWorkspace().DataContext.GetTypeSource(typeof(Blob))!.CollectionName;

        host.Post(new DataChangeRequest().WithUpdates(new Blob("doc-1", Payload("""{"a":1}"""))),
            o => o.WithAccessContext(accessService.Context!));

        // The mirror the portal's client is: a JsonElement view of the owner's store.
        var mirror = client.GetWorkspace()
            .GetRemoteStream<JsonElement, CollectionsReference>(CreateHostAddress(), new CollectionsReference(collectionName));
        // The wire shape of an EntityStore: { "$type": …, "<collection>": { "\"id\"": entity, … } }.
        static int Count(JsonElement e, string collection)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(collection, out var coll)
               && coll.ValueKind == JsonValueKind.Object
                ? coll.EnumerateObject().Count()
                : -1;
        await mirror.Where(ci => Count(ci.Value, collectionName) == 1)
            .Take(1).Should().Within(TestTimeouts.Convergence).Emit();
        var framesBefore = posted.Count;
        // Both sides of the rule are pinned from here on: what the mirror EMITS to its consumers.
        var emitted = 0;
        var replayed = new TaskCompletionSource();
        using var emissions = mirror.Subscribe(_ =>
        {
            if (!replayed.Task.IsCompleted) replayed.TrySetResult(); // the subscription replays the current value
            else Interlocked.Increment(ref emitted);
        });
        await replayed.Task.WaitAsync(TestTimeouts.Convergence);
        var versionAfterFull = mirror.Current!.Version;

        // The no-op: the SAME payload, parsed again — a new JsonElement, so the owner sees a change
        // and ships a frame whose diff is empty.
        host.Post(new DataChangeRequest().WithUpdates(new Blob("doc-1", Payload("""{"a":1}"""))),
            o => o.WithAccessContext(accessService.Context!));
        await Observable.Interval(TimeSpan.FromMilliseconds(20))
            .Where(_ => posted.Count > framesBefore)
            .Take(1).Should().Within(TestTimeouts.Convergence).Emit();
        var noOpFrames = posted.Skip(framesBefore).ToArray();
        noOpFrames.Should().NotBeEmpty(
            "the scenario needs the owner to SEND a frame for the value-equal write — otherwise nothing chains onto it and this test proves nothing");
        Volatile.Read(ref emitted).Should().Be(0,
            "a frame that changed nothing must not wake a consumer — the dedup is the point, only the clock moves");
        mirror.Current!.Version.Should().Be(noOpFrames.Last().Version,
            "the skipped frame's version is adopted so the chain the owner built onto it stays intact");
        mirror.Current!.Version.Should().BeGreaterThan(versionAfterFull);

        // The real change that chains onto the no-op frame.
        host.Post(new DataChangeRequest().WithUpdates(new Blob("doc-2", Payload("""{"b":2}"""))),
            o => o.WithAccessContext(accessService.Context!));
        await mirror.Where(ci => Count(ci.Value, collectionName) == 2)
            .Take(1).Should().Within(TestTimeouts.Convergence).Emit();
        // A re-ask, if the old defect were back, is posted right after the gap is detected — i.e.
        // before the Full it earns lands; the doc-2 emission above is that Full or the real patch,
        // so by now a second SubscribeRequest would be in the queue. Nothing to wait for.

        foreach (var f in posted)
            Output.WriteLine($"owner posted {f.Type} v{f.Version} basedOn v{f.BasedOn}: {f.Change[..Math.Min(80, f.Change.Length)]}");
        Output.WriteLine($"subscribe requests posted by the mirror: {subscribeRequests.Count}");

        subscribeRequests.Count.Should().Be(1,
            "a value-equal Patch is not a lost frame — the mirror must not re-ask for a snapshot it already holds");
        Volatile.Read(ref emitted).Should().Be(1,
            "the genuinely different patch must still reach the consumer — the other side of the same rule, so a ValuesEqual that made everything equal cannot pass");
        mirror.Current!.Version.Should().Be(posted.Last().Version,
            "the mirror's clock must sit on the last frame the owner sent, whether or not that frame changed anything");
    }
}
