using System;
using System.Linq;
using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// The partitioned wiring must REGISTER the change listener <b>and</b> the hosted service that
/// starts it.
///
/// <para>🚨 Why a shape test earns its place here: "registered but never started" is invisible from
/// behaviour. The listener is a DI singleton nobody resolves, so no LISTEN session opens, nothing
/// errors, nothing logs, and the change feed simply carries this process's own writes and no one
/// else's. That is #1440 exactly — filed 2026-08-13, closed as COMPLETED with only the doc fixed,
/// and five days later it was the middle leg of the three-way-broken notify chain that took every
/// course cover on memex.meshweaver.cloud down for two hours (#1814). The other two legs are the
/// per-database <c>mesh_node_notify</c> trigger (restored by V54/#1816) and the consumer that used
/// to discard an entity-less notification (fixed in <c>MeshDataSource</c>).</para>
///
/// <para>No container: this asserts on the service DESCRIPTORS, so it is a fast, deterministic gate
/// on the one property a functional test cannot cheaply cover — that a real host, which is the only
/// thing that ever starts an <c>IHostedService</c>, will find something to start.</para>
/// </summary>
public class ChangeListenerWiringTests
{
    private const string AnyConnectionString =
        "Host=localhost;Database=meshweaver_test;Username=postgres;Password=postgres";

    [Fact]
    public void ConnectionStringOverload_RegistersTheListenerAndStartsIt()
        => AssertWired(new ServiceCollection()
            .AddPartitionedPostgreSqlPersistence(AnyConnectionString));

    [Fact]
    public void AspireOverload_RegistersTheListenerAndStartsIt()
        // The overload the portal actually uses (Memex.Portal.Distributed) — the one whose absence
        // was measured on prod.
        => AssertWired(new ServiceCollection().AddPartitionedPostgreSqlPersistence());

    private static void AssertWired(IServiceCollection services)
    {
        services.Any(d => d.ServiceType == typeof(PostgreSqlChangeListener))
            .Should().Be(true,
                "the partitioned wiring must register the LISTEN/NOTIFY listener — without it a "
                + "mirror in another process is never told about a rival's write (#1440)");

        services.Any(d => d.ServiceType == typeof(IHostedService)
                          && d.ImplementationType == typeof(PostgreSqlChangeListenerHostedService))
            .Should().Be(true,
                "…and the hosted service that OPENS the session: a registered-but-unresolved "
                + "singleton is the exact shape that shipped for months, and it fails silently");
    }
}
