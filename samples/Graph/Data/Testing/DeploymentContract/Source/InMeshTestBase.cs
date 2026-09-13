// <meshweaver>
// Id: InMeshTestBase
// DisplayName: In-mesh test base — MonolithMeshTestBase's helpers over the live mesh
// </meshweaver>
#nullable enable
using System;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using MeshWeaver.Testing.InMesh;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// What a migrated xunit test class inherits instead of <c>MonolithMeshTestBase</c>: the SAME
/// protected vocabulary — <see cref="Mesh"/>, <see cref="NodeFactory"/>, <see cref="MeshQuery"/>,
/// <see cref="PathResolver"/>, <see cref="ReadNode"/>, <see cref="SeedTopLevel"/>,
/// <see cref="GetClient"/>, <see cref="RequestHub"/>, <see cref="AwaitResponseAsync{TResponse}"/>,
/// <see cref="ObserveNodeOperation{TResponse}"/>, <see cref="Output"/>, <see cref="TestPartition"/> —
/// bound to the mesh the <c>Tests</c> area renders in (the gate's mesh: one process, monolith
/// routing), with the class's own partition instead of a fresh host. Maintainer, 2026-09-13:
/// "for this test, we should setup monolith routing" / "no special setup or anything?" — none:
/// the mesh is up before the first case runs. Written by convert-xunit-to-inmesh.py's output.
/// </summary>
public abstract class InMeshTestBase
{
    /// <summary>The runner's context: hub, partition, output, deadline.</summary>
    protected MeshTestContext Context { get; }

    protected InMeshTestBase(MeshTestContext context)
    {
        Context = context;
        Output = new TestOutput(context.Output);
    }

    /// <summary>The mesh hub (the xunit base's <c>Mesh</c>).</summary>
    protected IMessageHub Mesh => Context.Hub;

    /// <summary>This class's partition — every node a case writes goes under it.</summary>
    protected string TestPartition => Context.Partition;

    /// <summary>The xunit <c>ITestOutputHelper</c> shape: lines land in the verdict's detail column.</summary>
    protected TestOutput Output { get; }

    /// <summary>Node creation / update service.</summary>
    protected IMeshService NodeFactory => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>Query service (the same service; the xunit base named both).</summary>
    protected IMeshService MeshQuery => NodeFactory;

    /// <summary>Path resolution.</summary>
    protected IPathResolver PathResolver => Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

    /// <summary>A path inside this class's partition.</summary>
    protected string P(string relative) => Context.Path(relative);

    /// <summary>The 60-second read cap the xunit base used.</summary>
    protected static readonly TimeSpan ReadNodeTimeout = TimeSpan.FromSeconds(60);

    private static readonly Address ReadHubAddress = new("test-reader", "shared");

    private IMessageHub ReadHub => Mesh.GetHostedHub(ReadHubAddress, c => c.AddData());

    /// <summary>Reads a node by path — null when it does not exist.</summary>
    protected IObservable<MeshNode?> ReadNode(string path)
        => ReadHub.GetMeshNode(path, ReadNodeTimeout)
            .Select(n => (MeshNode?)n)
            .Catch((Exception ex) => IsNotFound(ex) ? Observable.Return<MeshNode?>(null) : Observable.Throw<MeshNode?>(ex));

    private static bool IsNotFound(Exception ex)
        => ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates a node as the platform provisioner (the xunit base's SeedTopLevel).</summary>
    protected async Task<MeshNode> SeedTopLevel(MeshNode node)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return await access.RunAsSystem(() => NodeFactory.CreateNode(node))
            .SubscribeOn(TaskPoolScheduler.Default)
            .Take(1)
            .Timeout(Context.Deadline);
    }

    private readonly List<IMessageHub> _clients = new();
    private IMessageHub? _requestHub;

    /// <summary>A client hub on the mesh (disposed by the runner with the class).</summary>
    protected IMessageHub GetClient(Func<MessageHubConfiguration, MessageHubConfiguration>? config = null)
    {
        var address = new Address("client", Guid.NewGuid().ToString("N")[..12]);
        var client = Mesh.ServiceProvider.CreateMessageHub(address, config ?? (c => c))!;
        lock (_clients) _clients.Add(client);
        return client;
    }

    /// <summary>The class's shared request client.</summary>
    protected IMessageHub RequestHub
    {
        get { lock (_clients) return _requestHub ??= GetClient(); }
    }

    /// <summary>Posts a request and awaits its response under the case deadline.</summary>
    protected async Task<IMessageDelivery<TResponse>> AwaitResponseAsync<TResponse>(
        IRequest<TResponse> request, Func<PostOptions, PostOptions>? options = null, IMessageHub? hub = null, CancellationToken? ct = null)
        => await (hub ?? RequestHub).Observe(request, options).Take(1).Timeout(Context.Deadline);

    /// <summary>Posts a node operation to the mesh's node-operation target.</summary>
    protected IObservable<IMessageDelivery<TResponse>> ObserveNodeOperation<TResponse>(
        IRequest<TResponse> request, Func<PostOptions, PostOptions>? options = null)
    {
        var hub = RequestHub;
        var target = hub.NodeOperationTarget();
        return hub.Observe(request, o => options is null ? o.WithTarget(target) : options(o.WithTarget(target)));
    }

    /// <summary>The xunit ITestOutputHelper surface a migrated test calls.</summary>
    public sealed class TestOutput(Action<string> sink)
    {
        public void WriteLine(string line) => sink(line);
        public void WriteLine(string format, params object?[] args) => sink(string.Format(format, args));
    }
}
