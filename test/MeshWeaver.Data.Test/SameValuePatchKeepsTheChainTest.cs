using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
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

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

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

        // The no-op: the SAME payload, parsed again — a new JsonElement, so the owner sees a change
        // and ships a frame whose diff is empty.
        host.Post(new DataChangeRequest().WithUpdates(new Blob("doc-1", Payload("""{"a":1}"""))),
            o => o.WithAccessContext(accessService.Context!));
        await Task.Delay(500);
        var noOpFrames = posted.Skip(framesBefore).ToArray();
        noOpFrames.Should().NotBeEmpty(
            "the scenario needs the owner to SEND a frame for the value-equal write — otherwise nothing chains onto it and this test proves nothing");

        // The real change that chains onto the no-op frame.
        host.Post(new DataChangeRequest().WithUpdates(new Blob("doc-2", Payload("""{"b":2}"""))),
            o => o.WithAccessContext(accessService.Context!));
        await mirror.Where(ci => Count(ci.Value, collectionName) == 2)
            .Take(1).Should().Within(TestTimeouts.Convergence).Emit();
        await Task.Delay(300);

        foreach (var f in posted)
            Output.WriteLine($"owner posted {f.Type} v{f.Version} basedOn v{f.BasedOn}: {f.Change[..Math.Min(80, f.Change.Length)]}");
        Output.WriteLine($"subscribe requests posted by the mirror: {subscribeRequests.Count}");

        subscribeRequests.Count.Should().Be(1,
            "a value-equal Patch is not a lost frame — the mirror must not re-ask for a snapshot it already holds");
        mirror.Current!.Version.Should().Be(posted.Last().Version,
            "the mirror's clock must sit on the last frame the owner sent, whether or not that frame changed anything");
    }
}
