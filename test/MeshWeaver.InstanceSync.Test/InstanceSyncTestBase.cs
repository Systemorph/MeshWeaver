using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence.Http;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.InstanceSync.Test;

/// <summary>
/// Base for instance-sync integration tests: wires the instance-sync types + services with
/// tight test intervals and the in-memory <see cref="FakeRemoteMesh"/> standing in for the
/// remote instance, so the full replicate / accumulate / drain / pull loop runs offline.
/// </summary>
public abstract class InstanceSyncTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected readonly FakeRemoteMesh Remote = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddInstanceSyncTypes()
            .ConfigureServices(s =>
            {
                s.AddInstanceSyncServices();
                // Tight intervals so accumulate → probe → drain round-trips run in
                // milliseconds (last registration wins over the production defaults).
                s.AddSingleton(new InstanceSyncOptions
                {
                    DrainDebounce = TimeSpan.FromMilliseconds(50),
                    PullInterval = TimeSpan.FromMilliseconds(200),
                    RetryInitial = TimeSpan.FromMilliseconds(100),
                    RetryMax = TimeSpan.FromMilliseconds(400),
                });
                // The remote instance is the in-memory fake (last registration wins).
                s.AddSingleton<IRemoteMeshClientFactory>(Remote);
                return s;
            });

    protected InstanceSyncService Sync => Mesh.ServiceProvider.GetRequiredService<InstanceSyncService>();
    protected InstanceSyncCoordinator Coordinator => Mesh.ServiceProvider.GetRequiredService<InstanceSyncCoordinator>();

    protected async Task<MeshNode> CreateSpace(string id, string? name = null) =>
        await NodeFactory.CreateNode(new MeshNode(id)
        {
            NodeType = "Space",
            Name = name ?? id,
            State = MeshNodeState.Active,
            Content = new Space { Name = name ?? id },
        }).Timeout(60.Seconds()).ToTask();

    protected async Task<MeshNode> CreateMarkdown(string path, string name, string body)
    {
        var seed = MeshNode.FromPath(path);
        return await NodeFactory.CreateNode(seed with
        {
            NodeType = "Markdown",
            Name = name,
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = body },
        }).Timeout(60.Seconds()).ToTask();
    }

    /// <summary>Adds a sync source and configures its connection in one go.</summary>
    protected async Task<string> AddConfiguredSource(
        string spacePath, string name = "partner",
        InstanceSyncDirection direction = InstanceSyncDirection.Bidirectional,
        string? remoteSpace = null)
    {
        var node = await Sync.AddSyncSource(spacePath, name).Timeout(30.Seconds()).ToTask();
        await Sync.UpdateConfig(node.Path, c => c with
        {
            RemoteUrl = "https://remote.example",
            RemoteToken = "mw_test_token",
            RemoteSpace = remoteSpace,
            Direction = direction,
        }).Timeout(30.Seconds()).ToTask();
        return node.Path;
    }

    /// <summary>Polls the source's config until it satisfies <paramref name="predicate"/>.</summary>
    protected async Task<InstanceSyncConfig> WaitForConfig(
        string spacePath, string sourceId, Func<InstanceSyncConfig, bool> predicate,
        TimeSpan? timeout = null) =>
        await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Sync.ReadConfig(spacePath, sourceId))
            .Where(c => c is not null && predicate(c))
            .Select(c => c!)
            .FirstAsync()
            .Timeout(timeout ?? 30.Seconds())
            .ToTask();

    /// <summary>Polls the fake remote until <paramref name="predicate"/> holds.</summary>
    protected async Task WaitForRemote(Func<FakeRemoteMesh, bool> predicate, TimeSpan? timeout = null) =>
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Where(_ => predicate(Remote))
            .FirstAsync()
            .Timeout(timeout ?? 30.Seconds())
            .ToTask();

    /// <summary>Polls until a local node is readable at <paramref name="path"/>.</summary>
    protected async Task<MeshNode> WaitForNode(string path) =>
        (await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => ReadNode(path))
            .Where(n => n is not null)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask())!;

    protected static string MarkdownBody(MeshNode? node) => node?.Content switch
    {
        MarkdownContent mc => mc.Content,
        string s => s,
        _ => "",
    };
}
