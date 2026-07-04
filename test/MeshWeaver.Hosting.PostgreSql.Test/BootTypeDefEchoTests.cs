using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// 🚨 Regression repro for the memex-cloud boot noise (2026-07-02, image ci.211+):
/// <c>SaveMeshNode failed for Code (version=1): relation "code.mesh_nodes" does not
/// exist</c> (same for <c>markdown</c> / <c>user</c>) on EVERY portal boot. Built-in
/// type-definition nodes are static-served (AddMeshNodes → <c>WithInitialData</c>, no
/// persistence backing), yet the per-node-hub persistence sampler auto-posted a
/// <c>SaveMeshNodeRequest</c> when boot-time enrichment activated their hubs. The PG
/// router — correctly — refuses to conjure a schema for a type-def path (the ghost-schema
/// invariant: schema creation is gated to partition-owning creates), so the stray write
/// 42P01'd on every boot. The root fix removes the stray write (the sampler is gated to
/// persistence-backed hubs); provisioning <c>code</c>/<c>markdown</c>/<c>user</c> schemas
/// would have been the band-aid — exactly the per-type ghost schemas the router redesign
/// eliminated.
///
/// <para>PG-only persistence, wired like <see cref="PgOnlyProdShapeTests"/> (the prod
/// portal shape). Red before the fix via the captured <c>SaveMeshNodeHandler</c> warning;
/// green after: activating the type-def hubs produces no save attempt, and no type-def
/// schema ever exists.</para>
/// </summary>
[Collection("PostgreSql")]
public class BootTypeDefEchoTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;
    private readonly ConcurrentQueue<string> _saveMeshNodeWarnings = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 16,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddRowLevelSecurity()
            .AddGraph()
            .ConfigureServices(s => s.AddSingleton<ILoggerProvider>(
                new CapturingLoggerProvider(_saveMeshNodeWarnings)));
    }

    [Fact(Timeout = 60000)]
    public async Task ActivatingStaticTypeDefHubs_NeverAttemptsA42P01Save()
    {
        var ct = TestContext.Current.CancellationToken;
        var paths = new[] { "Markdown", "Code", "User" };

        // Activate each built-in type-def per-node hub — what boot-time NodeType
        // enrichment does in prod — and confirm the STATIC node serves on PG-only
        // persistence (no echo row is needed for the type-def hub to work).
        foreach (var path in paths)
        {
            var served = await ReadNode(path)
                .Should().Within(30.Seconds()).Match(n => n is not null);
            served!.Path.Should().Be(path);
        }

        // Negative assertion (sanctioned fixed wait — "confirm nothing happened"): the
        // persistence sampler fired 200 ms after hub activation; give it headroom, then
        // confirm no SaveMeshNode attempt was made for the static type-defs.
        await Task.Delay(1500, ct);

        _saveMeshNodeWarnings.Should().BeEmpty(
            "static-served type definitions must never be auto-persisted — every warning here " +
            "is the boot-time 42P01 noise: {0}",
            string.Join(" | ", _saveMeshNodeWarnings));

        // And the ghost-schema invariant holds: no per-type schema was ever conjured.
        var typeSchemas = await _fixture.DataSource.ScalarLong(
                "SELECT COUNT(*) FROM information_schema.schemata " +
                "WHERE schema_name IN ('markdown', 'code', 'user')", ct)
            .Should().Within(30.Seconds()).Emit();
        typeSchemas.Should().Be(0L,
            "type-definition paths must never materialise as partition schemas");
    }

    /// <summary>Captures warning-level messages on the SaveMeshNodeHandler channel.</summary>
    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName.Contains("SaveMeshNodeHandler", StringComparison.OrdinalIgnoreCase)
                ? new CapturingLogger(sink)
                : NullLogger.Instance;

        public void Dispose() { }
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                sink.Enqueue(formatter(state, exception)
                    + (exception is null ? "" : $" ({exception.Message})"));
        }
    }
}
