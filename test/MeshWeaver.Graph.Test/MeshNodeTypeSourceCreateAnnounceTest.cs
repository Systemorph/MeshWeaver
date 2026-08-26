using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#2087 — the per-node hub's create-flush must ANNOUNCE a genuine create.</b>
///
/// <para><c>MeshNodeTypeSource.FlushPendingWrites</c> persists creates/deletes for a per-node
/// hub's own <see cref="MeshNode"/> directly against <see cref="IStorageAdapter"/> — bypassing
/// every other node-mutation path's <see cref="IMeshChangeFeed"/> publish. A write that skips the
/// feed leaves a node that EXISTS in storage and does not exist to the running mesh: a path
/// probed while it was still absent resolves to its ancestor with a remainder — a perfectly
/// cacheable value — and nothing ever evicts it, so routing answers <c>No node found at '…'</c>
/// for the life of the process (the #817/#824 announce-loss class; #2087 is its third
/// recurrence — #824 fixed the bulk installer path, #2257 fixed the post-creation-handler and
/// NodeType-bake paths).</para>
///
/// <para>An "add" in <c>MeshNodeTypeSource.UpdateImpl</c> is add-RELATIVE-TO-THIS-INSTANCE, not
/// necessarily globally new (a reactivated hub's bookkeeping starts empty, so its own
/// already-durable node is classified as an "add" too) — so only the genuinely-new case (the
/// incoming node's <see cref="MeshNode.Version"/> is 0, never minted before) announces
/// <see cref="MeshChangeKind.Created"/>; a re-add of already-durable content stays a bare write,
/// exactly as before the fix. This test drives the genuinely-new case directly: a
/// <c>DataChangeRequest.Update</c> for a path that has never been persisted is exactly the "add"
/// classification a genuinely-new node's first durable write takes when it lands here (its own
/// per-node hub's create-flush racing <c>Initialize</c>'s durable-seed read — see
/// <c>ApplyUpdateViaStream</c> in <c>MeshExtensions.cs</c>, "how imported nodes land when the
/// create race is lost").</para>
/// </summary>
public class MeshNodeTypeSourceCreateAnnounceTest(ITestOutputHelper output) : HubTestBase(output)
{
    private InMemoryStorageAdapter _persistence = null!;
    private static readonly JsonSerializerOptions JsonOptions = new();

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
    {
        _persistence = new InMemoryStorageAdapter();

        return conf
            .WithServices(services => services.AddInMemoryPersistence(_persistence))
            .WithRoutes(forward => forward
                .RouteAddressToHostedHub(HostType, c => c)
                .RouteAddressToHostedHub(ClientType, ConfigureClient));
    }

    // Plumbing fixture with no logged-in user → post as System (matches HubTestBase.GetHost),
    // same rationale as MeshNodeTypeSourceTest.
    private IMessageHub GetHostWithHandler(string hostId, Func<MessageHubConfiguration, MessageHubConfiguration> config)
        => Mesh.GetHostedHub(new Address(HostType, hostId),
            c => config(c).WithPostingIdentity(PostingIdentity.System));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddMeshDataSource(ds => ds.WithContentType<AnnounceTestContent>());

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration);

    private string GetHubPath(string hostId) => $"{HostType}/{hostId}";

    [HubFact]
    public async Task AGenuinelyNewNode_CreateFlush_AnnouncesCreatedOnTheMeshChangeFeed()
    {
        var hubPath = GetHubPath("announce-new");

        // Subscribe BEFORE anything happens — no pre-seed: the row genuinely does not exist.
        var changeFeed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var seen = new ConcurrentQueue<MeshChangeEvent>();
        using var subscription = changeFeed.Subscribe(seen.Enqueue);

        var host = GetHostWithHandler("announce-new", c => c
            .AddMeshDataSource(ds => ds.WithContentType<AnnounceTestContent>()));
        var workspace = host.GetWorkspace();

        // The host hub must finish starting (Initialize() settles — here on an absent row) before
        // RequestChange can dispatch into its own workspace; MeshNodeTypeSourceTest's other tests
        // wait on a non-empty stream instead, which only works because they pre-seed persistence.
        await host.Started;

        // Act — the SAME technique MeshNodeTypeSourceTest uses to drive an update through the
        // workspace pipeline, but for a path that was never persisted: UpdateImpl classifies this
        // as an "add" relative to this (empty) source instance, which is exactly the genuinely-new
        // shape of the create-flush path this test pins.
        var content = new AnnounceTestContent { Id = "1", Title = "New Item", Notes = "Notes" };
        var newNode = MeshNode.FromPath(hubPath) with
        {
            Name = "New Node",
            NodeType = "test",
            Content = content
        };
        _ = workspace.RequestChange(DataChangeRequest.Update([newNode]));

        // Wait for the debounced create-flush to land durably.
        var persisted = await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => _persistence.Read(hubPath, JsonOptions))
            .Should().Within(5.Seconds()).Match(n => n?.Name == "New Node");
        persisted.Should().NotBeNull();

        // Assert — the create-flush write must have announced Created. Without it the path stays
        // unreachable to any subscriber that invalidates a cached miss off the feed (#2087).
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Select(_ => seen.ToArray())
            .Where(events => events.Any(e => e.Path == hubPath && e.Kind == MeshChangeKind.Created))
            .FirstAsync().Timeout(20.Seconds()).ToTask();

        seen.Should().Contain(e => e.Path == hubPath && e.Kind == MeshChangeKind.Created,
            "a genuinely new node's create-flush write is a CREATE like any other, and the "
            + "mesh-change feed is what makes it reachable — without it the row is in storage and "
            + "does not exist to the running mesh (#2087, the #817/#824 class)");
    }
}

/// <summary>Test content type for <see cref="MeshNodeTypeSourceCreateAnnounceTest"/>.</summary>
public record AnnounceTestContent
{
    [Key]
    public string Id { get; init; } = "";

    [MeshNodeProperty("Name")]
    public string Title { get; init; } = "";

    public string Notes { get; init; } = "";
}
