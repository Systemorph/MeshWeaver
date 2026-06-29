using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Routing / query CONVERGENCE invariants — the property that broke onboarding on
/// atioz (2026-06-05): a <c>SubscribeRequest</c> / query whose target node or
/// partition does not exist must <b>converge</b> — emit an empty <c>Initial</c>
/// (or a <c>null</c> resolution) promptly — and must <b>never hang</b>.
///
/// <para>The atioz symptom was the opposite: <c>IMeshQueryCore.Query</c> for a path
/// in an absent partition never emitted an <c>Initial</c>, so
/// <c>PathResolutionService.ResolvePath</c> never emitted, the Orleans
/// <c>RoutingGrain</c>'s <c>ResolvePath().Take(1).Subscribe(...)</c> never fired, and
/// the sender's <c>SubscribeRequest</c> aged out as a STALE-CALLBACK. Onboarding's
/// <c>FindUserByEmail</c> masked it with a 20s timeout; the onboarding page (no
/// timeout) hung forever on "Loading…" so "Complete Your Profile" never rendered.</para>
///
/// <para>Each assertion uses a TEST-SIDE <c>.Timeout(...)</c> guard: on a healthy
/// system the query converges in well under a second, so the guard never fires; if
/// the convergence bug regresses, the test FAILS with a <see cref="TimeoutException"/>
/// instead of hanging the whole suite. The guard is a test affordance — production
/// code must converge on its own (the point of the fix), NOT rely on a timeout.</para>
/// </summary>
[Collection("PostgreSql")]
public class RoutingConvergenceTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

    // A short guard relative to the 20s production lookup budget: a converging
    // query emits its empty Initial in milliseconds, so 8s is generous headroom
    // while still failing fast (and well under the [Fact] Timeout) if it hangs.
    private static readonly TimeSpan ConvergenceGuard = TimeSpan.FromSeconds(8);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 16,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddGraph();
    }

    [Fact(Timeout = 30000)]
    public async Task ResolvePath_PathInAbsentPartition_ConvergesToNull()
    {
        // A first segment (partition / schema) that was never created. Routing must
        // map "ghost/_UserActivity/ghost" to NotFound by emitting null — not hang.
        var ghost = $"ghost{Guid.NewGuid():N}".ToLowerInvariant()[..12];

        var resolution = await PathResolver
            .ResolvePath($"{ghost}/_UserActivity/{ghost}")
            .Take(1)
            .Timeout(ConvergenceGuard)
            .ToTask(TestContext.Current.CancellationToken);

        resolution.Should().BeNull(
            "a path whose partition does not exist must converge to a NotFound (null) " +
            "resolution, never leave the SubscribeRequest unanswered");
    }

    [Fact(Timeout = 30000)]
    public async Task GetQuery_ForNonMatchingUser_ConvergesToEmptyInitial()
    {
        // The exact onboarding lookup shape (OnboardingMiddleware.FindUserByEmail /
        // Onboarding.razor): no User node matches this email, so the synced query
        // must emit an empty Initial snapshot and the Take(1) consumer must complete.
        var workspace = Mesh.GetWorkspace();
        var email = $"nobody-{Guid.NewGuid():N}@nowhere.invalid";

        var items = await workspace
            .GetQuery($"test:userByEmail:{email}", $"nodeType:User content.email:{email} limit:1")
            .Take(1)
            .Timeout(ConvergenceGuard)
            .ToTask(TestContext.Current.CancellationToken);

        items.Should().BeEmpty(
            "a synced query that matches nothing must converge to an empty Initial — " +
            "this is what onboarding's existing-user check waits on");
    }

    [Fact(Timeout = 30000)]
    public async Task GetQuery_ScopedToAbsentPartition_ConvergesToEmptyInitial()
    {
        // A partition-scoped query against a schema that does not exist. The
        // per-schema adapter must swallow 42P01 (undefined table) → empty, and the
        // partitioned provider must still emit an Initial so the merge proceeds.
        var workspace = Mesh.GetWorkspace();
        var ghost = $"ghost{Guid.NewGuid():N}".ToLowerInvariant()[..12];

        var items = await workspace
            .GetQuery($"test:ghostScope:{ghost}", $"namespace:{ghost} scope:descendants limit:50")
            .Take(1)
            .Timeout(ConvergenceGuard)
            .ToTask(TestContext.Current.CancellationToken);

        items.Should().BeEmpty(
            "a query scoped to a non-existent partition must converge to empty, not hang");
    }
}
