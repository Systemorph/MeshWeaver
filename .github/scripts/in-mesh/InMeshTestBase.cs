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
using Microsoft.Extensions.Logging;

/// <summary>
/// What a migrated xunit test class inherits instead of <c>MonolithMeshTestBase</c>: the SAME
/// protected vocabulary — <see cref="Mesh"/>, <see cref="NodeFactory"/>, <see cref="MeshQuery"/>,
/// <see cref="PathResolver"/>, <see cref="ReadNode"/>, <see cref="SeedTopLevel"/>,
/// <see cref="GetClient"/>, <see cref="RequestHub"/>, <see cref="AwaitResponseAsync{TResponse}"/>,
/// <see cref="ObserveNodeOperation{TResponse}"/>, <see cref="Output"/>, <see cref="TestPartition"/> —
/// bound to the mesh the <c>Tests</c> area renders in (the gate's mesh: one process, monolith
/// routing), with the class's own partition instead of a fresh host. Maintainer, 2026-09-13:
/// "for this test, we should setup monolith routing" / "no special setup or anything?" — none:
/// the mesh is up before the first case runs. Laid into every suite by generate-in-mesh-suites.py
/// from .github/scripts/in-mesh/InMeshTestBase.cs.
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

    /// <summary>For a migrated class with no constructor of its own: the runner's ambient context.</summary>
    protected InMeshTestBase() : this(Current) { }

    /// <summary>The xunit estate's field name for its output helper (<c>output.WriteLine(…)</c>).</summary>
    protected TestOutput output => Output;

    // ── HubTestBase's vocabulary (test/MeshWeaver.Fixture/HubTestBase.cs), bound to the LIVE mesh ──
    // The xunit fixture stands up a host hub and client hubs of its own; in-mesh the host IS the
    // mesh the Tests area renders in. A class that configures the host (ConfigureHost/ConfigureClient/
    // ConfigureMesh overrides, GetHost(config)) is refused by the converter — that is the pre-boot
    // substitution facility, not something this base can fake.
    protected const string MeshType = "mesh";
    protected const string HostType = "host";
    protected const string ClientType = "client";

    /// <summary>The live mesh's address (the fixture's separate host hub has no in-mesh twin).</summary>
    protected static Address CreateMeshAddress(string? id = null) => Mesh.Address;

    /// <summary>The live mesh's address — every post the xunit test aimed at "the host" lands here.</summary>
    protected static Address CreateHostAddress(string? id = null) => Mesh.Address;

    /// <summary>A fresh client address, the shape <see cref="GetClient"/> registers.</summary>
    protected static Address CreateClientAddress(string? id = null) => new(ClientType, id ?? Guid.NewGuid().ToString("N")[..12]);

    /// <summary>The host hub of the xunit fixture: in-mesh, the mesh itself.</summary>
    protected static IMessageHub GetHost() => Mesh;

    /// <summary>A logger for the test class (the fixture's <c>Logger</c> field).</summary>
    protected ILogger Logger => Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(GetType().Name);

    /// <summary>The mesh hub (the xunit base's <c>Mesh</c>).</summary>
    protected static IMessageHub Mesh => Current.Hub;

    private static MeshTestContext Current => MeshTestContext.Current ?? throw new InvalidOperationException("no MeshTestContext is current — the runner sets it before constructing a test class");

    /// <summary>This class's partition — every node a case writes goes under it. Static, read off the
    /// runner's ambient context, because the xunit estate uses it in field initialisers.</summary>
    protected static string TestPartition => Current.Partition;

    /// <summary>The fixture's <c>Services</c> / <c>ServiceProvider</c>: the mesh's service provider.</summary>
    protected static IServiceProvider Services => Mesh.ServiceProvider;
    protected static IServiceProvider ServiceProvider => Mesh.ServiceProvider;

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

    /// <summary>
    /// Runs <paramref name="work"/> as the system identity in the sanctioned in-mesh shape (core#1820,
    /// check-impersonation.py): the scope is opened on the subscribing thread and closed by it, never
    /// latched onto a terminating thread. The xunit estate's <c>access.RunAsSystem(() => …)</c> is
    /// rewritten to this by convert-xunit-to-inmesh.py.
    /// </summary>
    public static IObservable<T> AsSystem<T>(AccessService? access, Func<IObservable<T>> work) =>
        Observable.Create<T>(observer =>
        {
            using (access?.ImpersonateAsSystem())
                return work().Subscribe(observer);
        });

    /// <summary>The mesh's access service.</summary>
    protected static AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>Creates a node as the platform provisioner (the xunit base's SeedTopLevel). Reactive: a test
    /// awaits the observable it returns; no mesh write is awaited here (HubReachableAsyncGuard).</summary>
    protected IObservable<MeshNode> SeedTopLevel(MeshNode node)
        => AsSystem(Access, () => NodeFactory.CreateNode(node))
            .SubscribeOn(TaskPoolScheduler.Default)
            .Take(1)
            .Timeout(Context.Deadline);

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
        => (IMessageDelivery<TResponse>)await (hub ?? RequestHub).Observe(request, options).Take(1).Timeout(Context.Deadline);

    /// <summary>Posts a node operation to the mesh's node-operation target.</summary>
    protected IObservable<IMessageDelivery<TResponse>> ObserveNodeOperation<TResponse>(
        IRequest<TResponse> request, Func<PostOptions, PostOptions>? options = null)
    {
        var hub = RequestHub;
        var target = hub.NodeOperationTarget();
        return hub.Observe(request, o => options is null ? o.WithTarget(target) : options(o.WithTarget(target)))
            .Select(d => (IMessageDelivery<TResponse>)d);
    }

    /// <summary>The xunit ITestOutputHelper surface a migrated test calls.</summary>
    public sealed class TestOutput(Action<string> sink)
    {
        public void WriteLine(string line) => sink(line);
        public void WriteLine(string format, params object?[] args) => sink(string.Format(format, args));
    }
}
