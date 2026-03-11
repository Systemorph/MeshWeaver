using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
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
    protected MeshBuilder ConfigureMeshBase(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .AddRowLevelSecurity()
            .AddGraph();

    /// <summary>
    /// Default mesh configuration with PublicAdminAccess (grants all users Admin).
    /// Override to customize. Security tests should call ConfigureMeshBase() instead.
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
        => NodeFactory.CreateNodeAsync(node, ct);

    protected IMessageHub GetClient(Func<MessageHubConfiguration, MessageHubConfiguration>? config = null)
    {
        return Mesh.ServiceProvider.CreateMessageHub(CreateClientAddress(), config ?? ConfigureClient)!;
    }

    protected virtual MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        configuration.WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h));

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
