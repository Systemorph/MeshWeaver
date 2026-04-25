using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.TestBase;

public abstract class MonolithMeshTestBase : Fixture.TestBase
{
    protected static Address CreateClientAddress() => new("client", "1");

    /// <summary>
    /// Base mesh configuration without access control setup.
    /// Security tests can call this directly instead of base.ConfigureMesh().
    /// </summary>
    /// <summary>
    /// Default test partition name. Tests can create nodes under this path
    /// (e.g., "TestData/mynode") and they'll have proper mesh node hubs.
    /// Registered as a Markdown node so the hub gets AddMeshDataSource + WithNodeOperationHandlers.
    /// </summary>
    public const string TestPartition = "TestData";

    protected MeshBuilder ConfigureMeshBase(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .AddRowLevelSecurity()
            .AddGraph()
            .AddMeshNodes(new MeshNode(TestPartition) { Name = "Test Data", NodeType = "Markdown" });

    /// <summary>
    /// Default mesh configuration with PublicAdminAccess for in-memory tests.
    /// File-system tests should override and omit PublicAdminAccess (access comes from _Access/ files).
    /// Security tests should call ConfigureMeshBase() instead.
    /// </summary>
    protected virtual MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(TestUsers.PublicAdminAccess());

    protected MonolithMeshTestBase(ITestOutputHelper output) : base(output)
    {
        var builder = ConfigureMesh(
            new(
                c => c.Invoke(Services),
                AddressExtensions.CreateMeshAddress()
            )
        );
        Services.AddSingleton(builder.BuildHub);
    }

    /// <summary>
    /// Called after ServiceProvider is built. Logs in the default admin user (DevLogin)
    /// and sets up access rights so that access control allows operations in tests.
    /// </summary>
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        TestUsers.DevLogin(Mesh);
        await SetupAccessRightsAsync();
    }

    /// <summary>
    /// Sets up access rights for tests. Default is a no-op since PublicAdminAccess
    /// is added as a configuration node in ConfigureMesh (never persisted to disk).
    /// Override to set up custom permissions for security tests.
    /// </summary>
    protected virtual Task SetupAccessRightsAsync() => Task.CompletedTask;

    protected IMessageHub Mesh => ServiceProvider.GetRequiredService<IMessageHub>();
    protected IRoutingService RoutingService => ServiceProvider.GetRequiredService<IRoutingService>();

    /// <summary>
    /// Public API for creating nodes in tests.
    /// Prefer seeding data via <see cref="ConfigureMesh"/> + <c>builder.AddMeshNodes(...)</c>
    /// for static test data that is known at setup time.
    /// </summary>
    protected IMeshService NodeFactory => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// Public API for querying nodes in tests.
    /// </summary>
    protected IMeshService MeshQuery => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// Public API for resolving URL paths to hub addresses in tests.
    /// </summary>
    protected IPathResolver PathResolver => Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

    /// <summary>
    /// Creates a test node using the public IMeshService API.
    /// Use this for dynamic test data. For static test data known at setup time,
    /// override <see cref="ConfigureMesh"/> and use <c>builder.AddMeshNodes(...)</c> instead.
    /// </summary>
    protected Task<MeshNode> CreateNodeAsync(MeshNode node, CancellationToken ct = default)
        => NodeFactory.CreateNode(node).ToTask(ct);

    /// <summary>
    /// Canonical CQRS-correct read primitive for tests: the per-node hub's
    /// <see cref="MeshNodeReference"/> reducer, surfaced as
    /// <see cref="IObservable{MeshNode}"/> via
    /// <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(IWorkspace, string)"/>.
    /// </summary>
    protected IObservable<MeshNode> ReadNode(string path)
        => Mesh.GetWorkspace().GetMeshNodeStream(path);

    private static readonly Address ReadHubAddress = new("test-reader", "shared");

    /// <summary>
    /// Convenience: targets the per-node hub's <see cref="MeshNodeReference"/>
    /// reducer via <see cref="GetDataRequest"/>, dispatched from a dedicated
    /// hosted reader hub so the response delivery never races the calling pump.
    /// Cancelled by <see cref="TestContext.Current"/>'s
    /// <see cref="ITestContext.CancellationToken"/> — every test inherits the
    /// same token automatically; never pass <c>default</c>.
    /// <para>
    /// Returns <c>null</c> only when the routing service reports
    /// <see cref="ErrorType.NotFound"/> (no per-node hub for this path — i.e.,
    /// the node was deleted or never existed). All other failures (timeout,
    /// cancellation, generic delivery failures) propagate so a hung lookup or a
    /// real bug surfaces as a test failure rather than a silent <c>null</c>.
    /// </para>
    /// <para>
    /// Replaces <c>await MeshQuery.QueryAsync&lt;MeshNode&gt;($"path:{X}").FirstOrDefaultAsync()</c>
    /// — see <c>Doc/Architecture/CqrsAndContentAccess.md</c>.
    /// </para>
    /// </summary>
    protected Task<MeshNode?> ReadNodeAsync(string path)
        => ReadNodeAsync(path, TestContext.Current.CancellationToken);

    /// <summary>
    /// Default upper bound for a single-node read in tests. Bounded so a misrouted
    /// request fails the test loudly with a <see cref="TimeoutException"/> instead
    /// of hanging the whole CI run until the inactivity guard aborts. 30 seconds
    /// is generous — typical per-node-hub activation + persistence load is sub-second.
    /// </summary>
    protected static readonly TimeSpan ReadNodeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Same as <see cref="ReadNodeAsync(string)"/> with an explicit token for
    /// tests that compose their own cancellation source on top of the
    /// test-context token. Composes the explicit token with a
    /// <see cref="ReadNodeTimeout"/> watchdog so a hung lookup surfaces quickly.
    /// </summary>
    protected async Task<MeshNode?> ReadNodeAsync(string path, CancellationToken ct)
    {
        var reader = Mesh.GetHostedHub(ReadHubAddress, c => c);
        // Wall-clock-bound the wait via Task.WhenAny — does NOT rely on the inner
        // AwaitResponse honouring cancellation, because routing failures on a path
        // with no per-node hub don't always cancel cleanly.
        var requestTask = reader.AwaitResponse(
            new GetDataRequest(new MeshNodeReference()),
            o => o.WithTarget(new Address(path)),
            ct);
        var winner = await Task.WhenAny(requestTask, Task.Delay(ReadNodeTimeout, ct));
        if (winner != requestTask)
        {
            throw new TimeoutException(
                $"ReadNodeAsync('{path}') exceeded {ReadNodeTimeout.TotalSeconds:F0}s. " +
                $"Likely cause: per-node hub for '{path}' never activated (the node " +
                $"was never created, or routing has no entry for the address), or its " +
                $"MeshDataSource never emitted on the MeshNodeReference reducer.");
        }

        IMessageDelivery<GetDataResponse> response;
        try
        {
            response = await requestTask;
        }
        catch (Exception ex) when (IsNotFoundFailure(ex))
        {
            // Routing reports NotFound (no per-node hub, or no GetDataRequest
            // handler on the hub that does exist). Treat as absence.
            return null;
        }

        var node = response.Message.Data as MeshNode;
        if (node == null && response.Message.Data is System.Text.Json.JsonElement je)
            node = je.Deserialize<MeshNode>(Mesh.JsonSerializerOptions);
        return node;
    }

    /// <summary>
    /// Subscribes to <c>ObserveQuery&lt;MeshNode&gt;</c> for <paramref name="query"/>
    /// and folds the live deltas (Initial / Reset / Added / Updated / Removed) into
    /// a running path set. Returns the path set the moment <paramref name="predicate"/>
    /// is satisfied. Wall-clock-bounded by <see cref="ReadNodeTimeout"/>.
    /// <para>
    /// Use this when a write changed the catalog state (Active ↔ Deleted, hard
    /// delete, etc.) and a follow-up <see cref="QueryAsync"/> would race the
    /// catalog update. Lossless replacement for poll-loops on stale snapshots.
    /// </para>
    /// </summary>
    protected async Task<IReadOnlySet<string>> WaitForQueryPathSetAsync(
        string query,
        Func<IReadOnlySet<string>, bool> predicate,
        CancellationToken ct)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var observable = MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Scan(paths, (acc, change) =>
            {
                if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                {
                    acc.Clear();
                    foreach (var n in change.Items) if (n.Path is { } p) acc.Add(p);
                }
                else if (change.ChangeType is QueryChangeType.Added or QueryChangeType.Updated)
                {
                    foreach (var n in change.Items) if (n.Path is { } p) acc.Add(p);
                }
                else if (change.ChangeType is QueryChangeType.Removed)
                {
                    foreach (var n in change.Items) if (n.Path is { } p) acc.Remove(p);
                }
                return acc;
            })
            .Where(predicate);

        var set = await Task.WhenAny(
            observable.FirstAsync().ToTask(ct),
            Task.Delay(ReadNodeTimeout, ct).ContinueWith<IReadOnlySet<string>>(_ =>
                throw new TimeoutException(
                    $"WaitForQueryPathSetAsync('{query}') exceeded {ReadNodeTimeout.TotalSeconds:F0}s. " +
                    $"Likely cause: a write completed but the query catalog never reflected the change. " +
                    $"Current path set ({paths.Count}): [{string.Join(", ", paths)}]"), ct));
        return await set;
    }

    /// <summary>
    /// Recognise the two routing-failure flavours that mean "this path has no
    /// readable MeshNode" so the helper can return <c>null</c> instead of
    /// surfacing a noisy exception:
    /// <list type="bullet">
    ///   <item>"No node found for address X" — the path has no per-node hub at all
    ///     (deleted or never existed).</item>
    ///   <item>"No handler found for message type GetDataRequest" — the per-node
    ///     hub exists but doesn't register the data layer (e.g., a test hub
    ///     configured without <c>AddMeshDataSource</c>); semantically still
    ///     "no MeshNode to read" from the test's POV.</item>
    /// </list>
    /// Everything else (timeouts, validation failures, generic delivery failures
    /// with a different message) propagates so real bugs surface.
    /// </summary>
    private static bool IsNotFoundFailure(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is DeliveryFailureException dfe)
            {
                var msg = dfe.Message;
                if (msg.StartsWith("No node found for address ", StringComparison.Ordinal))
                    return true;
                if (msg.StartsWith("No handler found for message type GetDataRequest", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    protected IMessageHub GetClient(Func<MessageHubConfiguration, MessageHubConfiguration>? config = null)
    {
        return Mesh.ServiceProvider.CreateMessageHub(CreateClientAddress(), config ?? ConfigureClient)!;
    }

    protected virtual MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.WithType(typeof(MeshNodeReference), nameof(MeshNodeReference));
        // Pre-resolve RoutingService to avoid re-entrant DI resolution deadlock
        // during client hub's BuildupAction (which runs on a thread pool thread)
        var routingService = RoutingService;
        return configuration
            .AddMeshTypes()
            .WithInitialization((h, _) => routingService.RegisterStreamAsync(h));
    }

    private static readonly string DisposeLogFile = Path.Combine(
        AppContext.BaseDirectory, "test-logs", "dispose-trace.log");

    private static void TraceDispose(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(DisposeLogFile)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(DisposeLogFile,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { /* best effort */ }
    }

    public override async ValueTask DisposeAsync()
    {
        var testName = GetType().Name;
        var sw = Stopwatch.StartNew();
        TraceDispose($"DISPOSE START: {testName} (MeshAddress={Mesh.Address})");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            TraceDispose($"  {testName}: Calling Mesh.Dispose()...");
            Mesh.Dispose();
            TraceDispose($"  {testName}: Mesh.Dispose() returned in {sw.ElapsedMilliseconds}ms. Awaiting Mesh.Disposal...");
            await Mesh.Disposal!.WaitAsync(cts.Token);
            TraceDispose($"  {testName}: Mesh.Disposal completed in {sw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            TraceDispose($"  {testName}: TIMEOUT waiting for Mesh.Disposal after {sw.ElapsedMilliseconds}ms!");
        }
        catch (Exception ex)
        {
            TraceDispose($"  {testName}: ERROR during dispose after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TraceDispose($"  {testName}: Calling base.DisposeAsync()...");
            await base.DisposeAsync();
            TraceDispose($"DISPOSE END: {testName} in {sw.ElapsedMilliseconds}ms total");
        }
    }
}
