using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Two core follow-ups of the declared point read (MeshWeaver#7298, split out of #4306), measured on a
/// real monolith mesh.
///
/// <list type="bullet">
///   <item><description><b>F5 — ownership follows the OWNER, not the store.</b> A STORED type's
///     declaration is honoured only while its partition is system-owned (a one-way <c>_GitSync</c>).
///     That ownership was read from STORAGE, re-read on the invalidation feed — so an ownership change
///     the sync config's owner had already acknowledged, but the store had not yet persisted (the
///     owner samples its saves), still admitted a fresh point read, and an open admission stayed open
///     until the row landed. The store's writes of the sync config are HELD here, which makes that
///     window as long as the test needs it.</description></item>
///   <item><description><b>F8 — a hub's read of its OWN path.</b> A gated, declaration-only read a
///     node hub sends to ITSELF under a visitor's context made the declaration's System probe of the
///     owner queue behind that very read (same sender, <c>SenderOrderedRelease</c>): the probe could
///     not be answered until the read was released, and the read could not be released until the
///     probe answered — the read timed out and failed closed.</description></item>
/// </list>
///
/// Every refusal has a POSITIVE CONTROL beside it that differs in the one input under test.
/// </summary>
public class DeclaredPointReadOwnershipAndSelfReadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "t";
    private const string HostDeclaredType = "Kiosk/Ticket";

    private static readonly AccessContext Visitor = new() { ObjectId = WellKnownUsers.Anonymous, Name = "Anonymous" };

    private static readonly PointReadDeclaration Declaration = new()
    {
        SubjectProperty = "subject",
        Namespace = Partition,
        RevokedProperty = "revokedAt",
        ExpiresProperty = "expiresAt",
        Areas = [""],
    };

    /// <summary>The store's writes of these paths are held while <see cref="HeldStore.Holding"/> is set.</summary>
    private readonly HeldStore held = new(["lost/_GitSync", "kept/_GitSync"]);

    // ConfigureMeshBase, never ConfigureMesh: the default test configuration grants Public Admin.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .WithMeshType(typeof(TicketContent), nameof(TicketContent))
        .AddMeshNodes(new MeshNode("Ticket", "Kiosk")
        {
            NodeType = "NodeType",
            Name = HostDeclaredType,
            Content = new NodeTypeDefinition { PointRead = Declaration },
            HubConfiguration = config => config.WithContentType<TicketContent>().AddDefaultLayoutAreas(),
        })
        .ConfigureServices(services =>
        {
            // Wrap the OUTERMOST non-keyed adapter — the one every read and every own-save goes
            // through — exactly as CreateWhenTheStoreIsUnreachableTest does.
            var registered = services.Last(d => d.ServiceType == typeof(IStorageAdapter) && !d.IsKeyedService);
            services.Remove(registered);
            return services.AddSingleton<IStorageAdapter>(sp => held.Wrap(Materialise(registered, sp)));
        });

    public override async ValueTask DisposeAsync()
    {
        // Whatever the test did, the held saves land before the mesh tears down.
        held.Release();
        await base.DisposeAsync();
    }

    /// <summary>
    /// 🚨 F5: a partition that STOPS being system-owned withdraws its stored type's declaration the
    /// moment the sync config's OWNER acknowledges it — before the store has the row. A fresh point
    /// read is refused, and an admission already open turns false.
    ///
    /// <para>The instrument is checked first: while the hold is armed the STORE still reads the sync
    /// config as one-way, so a refusal here cannot have come from a store that already caught up.</para>
    ///
    /// <para>POSITIVE CONTROL: a second partition whose sync config is rewritten under the same hold
    /// but STAYS one-way keeps admitting — the refusal is the ownership change, not the write, the
    /// hold or the re-decision.</para>
    /// </summary>
    [HubFact]
    public async Task APartitionThatStopsBeingSystemOwned_WithdrawsTheDeclaration_BeforeTheStoreHasTheRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTopLevel(new MeshNode(Partition) { NodeType = "Markdown", Name = Partition });
        await SeedStoredType("lost", ct);
        await SeedStoredType("kept", ct);
        var lostLink = await Issue("lost-link", "lost/Ticket", ct);
        var keptLink = await Issue("kept-link", "kept/Ticket", ct);

        Assert.True(await Admits(lostLink, ct), "PRECONDITION: a stored type in a system-owned partition declares the point read");
        Assert.True(await Admits(keptLink, ct), "PRECONDITION: …in both partitions");
        var lostOpen = DeclaredPointRead.Admits(Mesh, lostLink, Visitor).Replay(1);
        var keptOpen = DeclaredPointRead.Admits(Mesh, keptLink, Visitor).Replay(1);
        using var lostConnection = lostOpen.Connect();
        using var keptConnection = keptOpen.Connect();
        await lostOpen.Should().Within(TestTimeouts.Convergence).Match(v => v, "the open admission starts admitted", cancellationToken: ct);
        await keptOpen.Should().Within(TestTimeouts.Convergence).Match(v => v, "…in both partitions", cancellationToken: ct);

        // The sync configs' OWNERS are live before the hold is armed: an activation re-saves what it
        // loaded, and a held activation write would hold the owner itself rather than its persistence.
        await SyncConfig("lost", twoWay: false, ct);
        await SyncConfig("kept", twoWay: false, ct);

        // The ownership changes, under the hold. A patch's acknowledgement waits for its durable
        // flush (bounded), so the writes stay subscribed and the test waits for what matters: the
        // OWNER carries the change, and its save is parked at the store.
        held.Holding = true;
        var lostMarker = Guid.NewGuid().ToString("N");
        var keptMarker = Guid.NewGuid().ToString("N");
        using var writes = new System.Reactive.Disposables.CompositeDisposable(
            UpdateSyncConfig("lost", twoWay: true, lostMarker).Subscribe(_ => { }, ex => FileOutput.WriteLine($"lost/_GitSync update: {ex}")),
            UpdateSyncConfig("kept", twoWay: false, keptMarker).Subscribe(_ => { }, ex => FileOutput.WriteLine($"kept/_GitSync update: {ex}")));
        await OwnerCarries("lost", lostMarker, ct);
        await OwnerCarries("kept", keptMarker, ct);
        await held.Held.Where(p => p == "lost/_GitSync").Take(1).Should().Within(TestTimeouts.Convergence)
            .Emit("INSTRUMENT: the owner's save of the lost sync config is parked at the store", cancellationToken: ct);
        await held.Held.Where(p => p == "kept/_GitSync").Take(1).Should().Within(TestTimeouts.Convergence)
            .Emit("INSTRUMENT: …and of the kept one", cancellationToken: ct);

        // The instrument: the change the owner carries has NOT reached the store.
        var stored = await Mesh.ReadStoredNode("lost/_GitSync").Should().Emit(cancellationToken: ct);
        Assert.True(AccessAssignmentGuard.IsSystemOwned(stored, Mesh.JsonSerializerOptions),
            "INSTRUMENT: while the hold is armed the store still reads the sync config as one-way");

        Assert.False(await Admits(lostLink, ct),
            "a fresh point read is refused once the owner acknowledged that the partition is no longer system-owned");
        await lostOpen.Should().Within(TestTimeouts.Convergence).Match(v => !v,
            "the OPEN admission is withdrawn with nothing persisted and nothing announced on the feed", cancellationToken: ct);

        // POSITIVE CONTROL: the same write under the same hold, ownership kept.
        Assert.True(await Admits(keptLink, ct), "a sync-config change that keeps the partition system-owned still admits");
        Assert.True(await keptOpen.Take(1).Should().Emit(cancellationToken: ct), "…and leaves the open admission admitted");
    }

    /// <summary>
    /// 🚨 F8: a node hub's gated, declaration-only read of its OWN path under a visitor's context is
    /// admitted — the declaration reads the hub's local own-node stream instead of asking the owner
    /// (itself) over a round-trip that queues behind the read it is deciding.
    ///
    /// <para>REFUSAL beside it: the same self-issued read of a REVOKED link is refused — so the admission
    /// is the declaration's verdict, not a self-read slipping past the gate.</para>
    /// </summary>
    [HubFact]
    public async Task AHubsOwnPointRead_UnderAVisitorsContext_IsDecided_NotDeadlocked()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTopLevel(new MeshNode(Partition) { NodeType = "Markdown", Name = Partition });
        var link = await Issue("self-read", HostDeclaredType, ct);
        var revoked = await Issue("self-read-revoked", HostDeclaredType, ct, revoked: true);

        // Both node hubs are active — the read is issued BY the hub, so it must be there to issue it.
        await AsSystemRead(link, ct);
        await AsSystemRead(revoked, ct);

        var served = await SelfRead(link).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        Assert.True(served.Kind == NotificationKind.OnNext,
            $"the hub's own point read is admitted by the declaration — got {served.Kind}: {served.Exception?.Message}");
        Assert.Equal(link, served.Value?.Path);

        // REFUSAL: same shape, same hub-to-itself delivery, a link that admits nobody.
        var refused = await SelfRead(revoked).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        Assert.True(refused.Kind == NotificationKind.OnError || refused.Value is null,
            "a self-issued read of a revoked link is refused — the gate decided it");
    }

    /// <summary>The link's OWN hub reads its own path as the visitor (<c>hub.GetMeshNode(hub.Address.Path)</c>).</summary>
    private IObservable<Notification<MeshNode?>> SelfRead(string path)
        => Observable.Defer(() =>
            {
                var owner = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never)
                            ?? throw new InvalidOperationException($"the node hub of {path} is not hosted");
                return owner.ServiceProvider.GetRequiredService<AccessService>()
                    .RunAs(Visitor, () => owner.GetMeshNode(path, timeout: TestTimeouts.Convergence));
            })
            .Materialize()
            .Take(1);

    private async Task AsSystemRead(string path, CancellationToken ct)
        => await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream(path).Select(n => n.Path).Take(1))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<bool> Admits(string link, CancellationToken ct)
        => DeclaredPointRead.Admits(Mesh, link, Visitor).Take(1).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    // ───────────────────────────── fixtures ─────────────────────────────

    private async Task SeedStoredType(string partition, CancellationToken ct)
    {
        await SeedTopLevel(new MeshNode(partition) { NodeType = "Markdown", Name = partition });
        await AsSystem(new MeshNode(AccessAssignmentGuard.SyncConfigId, partition)
        {
            NodeType = "Markdown",
            Content = new JsonObject { ["repositoryUrl"] = $"https://github.com/example/{partition}" },
        }, ct);
        await AsSystem(new MeshNode("Ticket", partition)
        {
            NodeType = "Markdown",
            Content = new JsonObject
            {
                [DeclaredPointRead.DeclarationProperty] = new JsonObject
                {
                    ["subjectProperty"] = "subject",
                    ["namespace"] = Partition,
                    ["revokedProperty"] = "revokedAt",
                    ["expiresProperty"] = "expiresAt",
                },
            },
        }, ct);
    }

    private async Task SyncConfig(string partition, bool twoWay, CancellationToken ct)
        => await UpdateSyncConfig(partition, twoWay, Guid.NewGuid().ToString("N")).Should().Emit(cancellationToken: ct);

    private IObservable<MeshNode> UpdateSyncConfig(string partition, bool twoWay, string marker)
        => Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream($"{partition}/{AccessAssignmentGuard.SyncConfigId}").Update(node => node with
            {
                Content = new JsonObject
                {
                    ["repositoryUrl"] = $"https://github.com/example/{partition}",
                    ["twoWay"] = twoWay,
                    ["lastSyncAt"] = marker,
                },
            }));

    /// <summary>Waits until the sync config's OWNER (through the node stream, as System) carries the write marked <paramref name="marker"/>.</summary>
    private async Task OwnerCarries(string partition, string marker, CancellationToken ct)
        => await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream($"{partition}/{AccessAssignmentGuard.SyncConfigId}").Select(n => (MeshNode?)n))
            .Should().Within(TestTimeouts.Convergence)
            .Match(node => MarkerOf(node) == marker, $"the owner of {partition}/_GitSync carries the new revision", cancellationToken: ct);

    private string? MarkerOf(MeshNode? node)
        => node?.Content is null
            ? null
            : JsonSerializer.SerializeToElement(node.Content, node.Content.GetType(), Mesh.JsonSerializerOptions) is { ValueKind: JsonValueKind.Object } body
              && body.TryGetProperty("lastSyncAt", out var at) && at.ValueKind == JsonValueKind.String
                ? at.GetString()
                : null;

    private async Task<string> Issue(string id, string nodeType, CancellationToken ct, bool revoked = false)
    {
        // Created straight to the store (a create is written at once — only updates ride the sampler).
        var created = await AsSystem(new MeshNode(id, Partition)
        {
            NodeType = nodeType,
            Name = id,
            Content = new TicketContent(WellKnownUsers.Anonymous, DateTimeOffset.UtcNow.AddDays(1).ToString("O"),
                revoked ? DateTimeOffset.UtcNow.ToString("O") : null),
        }, ct);
        Assert.Equal(WellKnownUsers.System, created.LastModifiedBy);
        return created.Path;
    }

    private async Task<MeshNode> AsSystem(MeshNode node, CancellationToken ct)
        => (await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.CreateNode(node)).Should().Emit(cancellationToken: ct))!;

    private static IStorageAdapter Materialise(ServiceDescriptor descriptor, IServiceProvider sp)
        => descriptor.ImplementationFactory is { } factory
            ? (IStorageAdapter)factory(sp)
            : descriptor.ImplementationInstance as IStorageAdapter
              ?? throw new InvalidOperationException(
                  "The IStorageAdapter registration is neither a factory nor an instance, so this test cannot hold its "
                  + "writes — an unwrapped store would make the persistence-lag assertions vacuous.");

    /// <summary>
    /// Holds the store's WRITES of a few paths while <see cref="Holding"/> is set — the persistence lag
    /// of an acknowledged update, made as long as the test needs. Reads are untouched, so the store
    /// keeps answering the row it had; a held write lands (in order) on <see cref="Release"/>.
    /// </summary>
    private sealed class HeldStore(IReadOnlyCollection<string> heldPaths)
    {
        private readonly ReplaySubject<Unit> released = new(1);
        private readonly ReplaySubject<string> held = new();

        public volatile bool Holding;

        /// <summary>Every path whose write was parked, as it is parked.</summary>
        public IObservable<string> Held => held;

        public void Release()
        {
            Holding = false;
            released.OnNext(Unit.Default);
        }

        public IStorageAdapter Wrap(IStorageAdapter inner) => new Adapter(inner, this);

        private string? HeldPath(IEnumerable<string?> paths)
            => Holding
                ? paths.Select(p => (p ?? "").Trim('/')).FirstOrDefault(p => heldPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                : null;

        private IObservable<T> Gate<T>(IEnumerable<string?> paths, Func<IObservable<T>> write)
        {
            if (HeldPath(paths) is not { } path)
                return write();
            held.OnNext(path);
            return released.Take(1).SelectMany(_ => write());
        }

        private sealed class Adapter(IStorageAdapter inner, HeldStore owner) : IStorageAdapter
        {
            public IObservable<DataChangeNotification> Changes => inner.Changes;
            public IObservable<ChangeFeedGap> ChangeFeedGaps => inner.ChangeFeedGaps;

            public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options) => inner.Read(path, options);

            public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
                => inner.ReadMany(paths, options);

            public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
                => Observable.Defer(() => owner.Gate([node.Path], () => inner.Write(node, options)));

            public IObservable<IReadOnlyList<MeshNode>> WriteMany(IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
                => Observable.Defer(() => owner.Gate(nodes.Select(n => n.Path), () => inner.WriteMany(nodes, options)));

            public IObservable<bool?> WriteIfVersion(MeshNode node, long expectedVersion, JsonSerializerOptions options)
                => Observable.Defer(() => owner.Gate([node.Path], () => inner.WriteIfVersion(node, expectedVersion, options)));

            public IObservable<string> Delete(string path) => inner.Delete(path);
            public IObservable<bool> DeleteIfExists(string path) => inner.DeleteIfExists(path);

            public IObservable<bool> DeleteIfVersionAtMost(string path, long maxVersion, JsonSerializerOptions options)
                => inner.DeleteIfVersionAtMost(path, maxVersion, options);

            public IObservable<bool> DeleteIfIncarnationAtMost(string path, long maxVersion, DateTimeOffset incarnation, JsonSerializerOptions options)
                => inner.DeleteIfIncarnationAtMost(path, maxVersion, incarnation, options);

            public IObservable<IReadOnlyList<string>> DeleteMany(IReadOnlyCollection<string> paths) => inner.DeleteMany(paths);
            public IObservable<string?> FindDeleteBlockingProvider(string path) => inner.FindDeleteBlockingProvider(path);

            public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
                => inner.ListChildPaths(parentPath);

            public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath) => inner.ListDescendantPaths(rootPath);
            public IObservable<bool> Exists(string path) => inner.Exists(path);
            public IObservable<bool> ExistsInWritableStorage(string path) => inner.ExistsInWritableStorage(path);

            public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(string fullPath, JsonSerializerOptions options)
                => inner.FindBestPrefixMatch(fullPath, options);

            public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(string fullPath, JsonSerializerOptions options)
                => inner.ResolvePath(fullPath, options);

            public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath) => inner.ListPartitionSubPaths(nodePath);

            public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
                => inner.GetPartitionObjects(nodePath, subPath, options);

            public IObservable<Unit> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
                => inner.SavePartitionObjects(nodePath, subPath, objects, options);

            public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
                => inner.DeletePartitionObjects(nodePath, subPath);

            public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
                => inner.GetPartitionMaxTimestamp(nodePath, subPath);
        }
    }
}
