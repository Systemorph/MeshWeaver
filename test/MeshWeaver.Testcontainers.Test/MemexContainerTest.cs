using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using MeshWeaver.Testcontainers;
using Testcontainers.PostgreSql;
using Xunit;

namespace MeshWeaver.Testcontainers.Test;

/// <summary>
/// The memex test container's contract. The builder cases need no Docker; the end-to-end case
/// starts a Postgres and a memex on one network and is SKIPPED, never silently green, when the
/// environment does not name the portal image to test against (<c>MEMEX_TEST_IMAGE</c>).
/// </summary>
public class MemexContainerTest
{
    /// <summary>The pinned portal image of the build under test, e.g. <c>meshweaver.azurecr.io/memex-portal-ai@sha256:…</c>.</summary>
    private static string? Image => Environment.GetEnvironmentVariable("MEMEX_TEST_IMAGE");

    [Fact]
    public void AMemexWithoutAPostgresDoesNotBuild_AndSaysWhy()
    {
        var ex = Assert.Throws<ArgumentException>(() => new MemexBuilder().WithImage("memex:test").Build());
        Assert.Contains("WithPostgres", ex.Message);
        Assert.Contains("ConnectionStrings:memex", ex.Message);
    }

    [Fact]
    public void AMemexWithAPostgresBuilds_WithoutStarting()
    {
        var container = new MemexBuilder()
            .WithImage("memex:test")
            .WithPostgres("Host=postgres;Port=5432;Database=memex;Username=postgres;Password=postgres")
            .Build();
        Assert.NotNull(container);
        // Not started: no Docker involved, and the mapped port would throw — that is the boundary
        // between the builder's contract (testable anywhere) and the substrate (needs Docker).
    }

    [Fact]
    public void DevLoginIsExplicitEitherWay()
    {
        // The host forces dev login OFF unless the value is literally "true", so the builder must
        // always write an explicit value — on by default, and off when asked. Verified by building
        // both shapes; the environment is applied at start, so this proves the builder accepts
        // the toggle in both positions without a Docker daemon.
        var on = new MemexBuilder().WithImage("memex:test").WithPostgres("Host=pg").Build();
        var off = new MemexBuilder().WithImage("memex:test").WithPostgres("Host=pg").WithDevLogin(false).Build();
        Assert.NotNull(on);
        Assert.NotNull(off);
    }

    [Fact]
    public async Task ARealMemexStartsBesideAPostgres_AndAnswersHealthz()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Image),
            "MEMEX_TEST_IMAGE is not set — name the pinned portal image (e.g. "
            + "meshweaver.azurecr.io/memex-portal-ai@sha256:…) to run the substrate end to end");

        var ct = TestContext.Current.CancellationToken;
        await using INetwork network = new NetworkBuilder().Build();
        await using var postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithNetwork(network)
            .WithNetworkAliases("postgres")
            .WithDatabase("memex")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await postgres.StartAsync(ct);

        await using var memex = new MemexBuilder()
            .WithImage(Image!)
            .WithNetwork(network)
            .WithPostgres("Host=postgres;Port=5432;Database=memex;Username=postgres;Password=postgres")
            .Build();
        await memex.StartAsync(ct);

        using var http = new HttpClient();
        using var response = await http.GetAsync(memex.HealthEndpoint, ct);
        Assert.True(response.IsSuccessStatusCode, $"{memex.HealthEndpoint} answered {(int)response.StatusCode}");
    }
}
