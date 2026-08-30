using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Pins what a query walk does once its adapter's I/O pool has been drained — the state every
/// still-running walk is left in by the mesh teardown now that the FileSystem pool is drainable
/// (issue #613 follow-up (a)).
///
/// <para>🚨 The storm this guards against, measured on CI run 33300005706 shard 5: with the
/// FileSystem pool drained, every per-path <c>Read</c> of a samples-graph walk was refused with an
/// <c>OperationCanceledException</c>, and the walk's per-path catch swallowed each one, logged a
/// Warning-with-exception and carried on to the next path — 2,342 fault records in a few
/// milliseconds, which exhausted the fault-record budget (100 per 10 s) and suppressed every
/// genuine fault logged in the following ten seconds (the fault-sink pin test among them). Before
/// the pool was drainable those same reads ran unsupervised on the unbounded pool instead — the
/// teardown-crash shape. Cancellation must TERMINATE the walk, not be handled path by path.</para>
/// </summary>
public class QueryWalkTeardownCancellationTest : MonolithMeshTestBase
{
    private const string Partition = "WalkCancel";
    private const int Seeded = 40;

    private readonly string _dir = Directory.CreateTempSubdirectory("mw-walk-cancel-").FullName;
    private readonly CapturingLoggerProvider _capture = new();

    public QueryWalkTeardownCancellationTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Seeded on DISK, not through the mesh: the point is a walk whose every node is served
        // by the file-system adapter's pooled Read leaf.
        var partitionDir = Path.Combine(_dir, Partition);
        Directory.CreateDirectory(partitionDir);
        File.WriteAllText(Path.Combine(_dir, Partition + ".json"),
            $$"""{"id":"{{Partition}}","namespace":"","name":"Walk cancel","nodeType":"Markdown"}""");
        for (var i = 0; i < Seeded; i++)
            File.WriteAllText(Path.Combine(partitionDir, $"n{i:D2}.json"),
                $$"""{"id":"n{{i:D2}}","namespace":"{{Partition}}","name":"Node {{i}}","nodeType":"Markdown"}""");

        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(_dir)
            .AddRowLevelSecurity()
            .AddMeshNodes(TestUsers.PublicAdminAccess())
            // Registered AFTER TestBase's ClearProviders (which runs in its constructor), so the
            // mesh's ILoggerFactory includes it — asserted below by a probe, never assumed.
            .ConfigureServices(s => s.AddSingleton<ILoggerProvider>(_capture))
            .ConfigureServices(s => s.Configure<CompilationCacheOptions>(o =>
                o.CacheDirectory = Path.Combine(_dir, ".cache")))
            .AddGraph();
    }

    [Fact(Timeout = 60_000)]
    public async Task ADrainedFileSystemPool_StopsTheWalk_InsteadOfWarningOncePerPath()
    {
        // The capture must be LIVE, or the "no warnings" assertion below checks nothing.
        Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("WalkCancel.Probe").LogWarning("capture-probe");
        _capture.Warnings.Should().Contain(w => w.Contains("capture-probe"),
            "the capturing logger provider must be wired into the mesh's logger factory");

        // Control: with the pool alive the walk reads every seeded node — proves the query below
        // exercises exactly the per-path Read leaf whose refusal is under test.
        var alive = await Query();
        alive.Items.Should().HaveCount(Seeded,
            "the control walk must read every seeded node, otherwise the drained-pool assertion measures nothing");

        // Dispose ONLY the FileSystem pool: the state a mesh teardown leaves a still-running walk in
        // (the Query pool that carries the subscribe is still alive, so the walk actually runs).
        var registry = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>();
        ((IDisposable)registry.Get(IoPoolNames.FileSystem)).Dispose();

        var drained = await Query();

        drained.Items.Should().BeEmpty("every pooled Read is refused once the pool is drained");
        _capture.Warnings.Where(w => w.Contains($"swallowed for path={Partition}/")).Should().BeEmpty(
            "a refused-at-teardown read is a cancellation that must STOP the walk — handling it as one " +
            "path's miss logs a Warning-with-exception per remaining path (2,342 in one CI teardown) and " +
            "exhausts the fault-record budget that the genuine faults need");
    }

    private Task<QueryResultChange<MeshNode>> Query()
        => MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{Partition} scope:subtree"))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .Await();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        // Instance field on a per-test object — never static (NoStaticState).
        public ConcurrentQueue<string> Warnings { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Warnings);

        public void Dispose() { }

        private sealed class CapturingLogger(string category, ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                    sink.Enqueue($"[{category}] {formatter(state, exception)}");
            }
        }
    }
}
