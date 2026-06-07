using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the live string-delta WRITE transport end-to-end: a client edits a small
/// region of a BIG string field, the subscriber ships only the splice
/// (<see cref="EntityDeltaUpdate"/>) in the <c>DataChangeRequest</c>, and the owning
/// hub reconstructs the EXACT entity by replaying the splice onto its current value
/// (<c>WorkspaceOperations</c> delta-resolve). Correctness of the reconstruction —
/// not the byte count — is what this guards; the byte savings are unit-tested in
/// <see cref="EntityDeltaTest"/> / StringDeltaPatchTest.
/// </summary>
public class StringDeltaTransportTest(ITestOutputHelper output) : HubTestBase(output)
{
    public record BigDoc(string Id, string Body);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds.WithType<BigDoc>(t => t.WithKey(d => d.Id))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddSource(ds => ds.WithType<BigDoc>(t => t.WithKey(d => d.Id))));

    [Fact]
    public void ClientEditsBigString_OwnerReconstructsExactEntity()
    {
        var host = GetHost();
        var client = GetClient();
        var collectionName = host.GetWorkspace().DataContext.GetTypeSource(typeof(BigDoc))!.CollectionName;

        // > 1 KB so the delta gate (EntityDelta.MinDeltaSize) fires; a tiny prepended edit.
        var bigBody = string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet consectetur adipiscing. ", 70));
        var editedBody = "INSERTED " + bigBody;

        var clientStream = client.GetWorkspace()
            .GetRemoteStream<EntityStore>(CreateHostAddress(), new CollectionsReference(collectionName));

        // Observe the OWNER's authoritative BigDoc("1").Body.
        var hostStream = host.GetWorkspace().GetStream(new CollectionsReference(collectionName))!;
        string? Body(EntityStore? s) => (s?.GetCollection(collectionName)?.Instances.GetValueOrDefault("1") as BigDoc)?.Body;
        var hostBodies = new ReplaySubject<string?>();
        using var sub = hostStream.Where(ci => ci.Value is not null).Subscribe(ci => hostBodies.OnNext(Body(ci.Value)));

        clientStream.Should().Within(10.Seconds()).Emit(); // stream live

        // 1. Seed the big base (full entity — no OldValue).
        Write(clientStream, collectionName, new BigDoc("1", bigBody), oldValue: null);
        hostBodies.Where(b => b == bigBody).Should().Within(10.Seconds()).Emit();

        // 2. Edit a small region — ships as a string-delta splice, not the whole 3.5 KB body.
        Write(clientStream, collectionName, new BigDoc("1", editedBody), oldValue: new BigDoc("1", bigBody));

        // 3. The owner reconstructs the EXACT edited entity from the splice.
        hostBodies.Where(b => b == editedBody).Should().Within(10.Seconds()).Emit();
    }

    private static void Write(ISynchronizationStream<EntityStore> stream, string collection, BigDoc doc, BigDoc? oldValue)
        => stream.Update(store =>
        {
            var existing = oldValue ?? store?.GetCollection(collection)?.Instances.GetValueOrDefault("1");
            return stream.ApplyChanges(new EntityStoreAndUpdates(
                WorkspaceOperations.Update(store ?? new(), collection, i => i.Update("1", doc)),
                [new EntityUpdate(collection, "1", doc) { OldValue = existing }],
                stream.StreamId));
        }, _ => { });
}
