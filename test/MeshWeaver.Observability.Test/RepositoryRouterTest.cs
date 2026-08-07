using System.Collections.Immutable;
using MeshWeaver.Observability;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// Routing decides which repository an automated ticket lands in, and one of its inputs is an LLM.
/// These tests pin both halves: the deterministic route, and the guard rails on the agent override.
/// </summary>
public class RepositoryRouterTest
{
    private static readonly LogWatchOptions Options = new()
    {
        DefaultRepository = "Systemorph/MeshWeaver",
        Routes =
        [
            new LogWatchRoute { Prefix = "MeshWeaver.", Repository = "Systemorph/MeshWeaver" },
            new LogWatchRoute { Prefix = "MeshWeaver.Courses.", Repository = "Systemorph/Courses" },
            new LogWatchRoute { Prefix = "Memex.", Repository = "Systemorph/Memex" },
        ],
    };

    private static LogIncident Incident(string category, string? proposed = null) => new()
    {
        Fingerprint = "abc123",
        Category = category,
        Draft = proposed is null ? null : new LogIncidentDraft { Title = "t", Repository = proposed },
    };

    [Theory]
    [InlineData("MeshWeaver.Data.MeshDataSource", "Systemorph/MeshWeaver")]
    [InlineData("Memex.Portal.Startup", "Systemorph/Memex")]
    [InlineData("Some.Unknown.Thing", "Systemorph/MeshWeaver")]
    public void ByCategory_RoutesByPrefixWithADefault(string category, string expected)
        => RepositoryRouter.ByCategory(category, Options).Should().Be(expected);

    [Fact]
    public void ByCategory_PrefersTheLongestMatchingPrefix()
        // The specific route must win over the catch-all regardless of configured order.
        => RepositoryRouter.ByCategory("MeshWeaver.Courses.ExerciseRunner", Options)
            .Should().Be("Systemorph/Courses");

    [Fact]
    public void Resolve_UsesTheRouteWhenTheAgentProposesNothing()
    {
        var route = RepositoryRouter.Resolve(Incident("Memex.Portal.Startup"), Options);

        route.Repository.Should().Be("Systemorph/Memex");
        route.Overridden.Should().BeFalse();
        route.RejectedOverride.Should().BeNull();
    }

    [Fact]
    public void Resolve_AcceptsAnOverrideIntoAKnownRepository()
    {
        var route = RepositoryRouter.Resolve(
            Incident("MeshWeaver.Data.MeshDataSource", "Systemorph/Memex"), Options);

        route.Overridden.Should().BeTrue();
        RepositoryRouter.Normalize(route.Repository).Should().Be("Systemorph/Memex");
    }

    [Fact]
    public void Resolve_RefusesAnOverrideIntoAnUnknownRepository()
    {
        var route = RepositoryRouter.Resolve(
            Incident("MeshWeaver.Data.MeshDataSource", "attacker/somewhere-else"), Options);

        // The agent cannot invent a write target: the route stands and the proposal is recorded.
        route.Repository.Should().Be("Systemorph/MeshWeaver");
        route.Overridden.Should().BeFalse();
        route.RejectedOverride.Should().Be("attacker/somewhere-else");
    }

    [Fact]
    public void Resolve_RefusesEveryOverrideWhenOverridesAreOff()
    {
        var options = Options with { AllowAgentRepositoryOverride = false };

        var route = RepositoryRouter.Resolve(
            Incident("MeshWeaver.Data.MeshDataSource", "Systemorph/Memex"), options);

        route.Repository.Should().Be("Systemorph/MeshWeaver");
        route.Overridden.Should().BeFalse();
        route.RejectedOverride.Should().Be("Systemorph/Memex");
    }

    [Fact]
    public void Resolve_HonoursAnExplicitAllowlist()
    {
        var options = Options with
        {
            AllowedRepositories = ImmutableList.Create("Systemorph/Plugins"),
        };

        // In the allowlist, but NOT a configured route — still allowed.
        RepositoryRouter.Resolve(Incident("MeshWeaver.Data.X", "Systemorph/Plugins"), options)
            .Overridden.Should().BeTrue();
        // A configured route that is NOT in the allowlist is now refused.
        RepositoryRouter.Resolve(Incident("MeshWeaver.Data.X", "Systemorph/Memex"), options)
            .RejectedOverride.Should().Be("Systemorph/Memex");
    }

    [Fact]
    public void Resolve_TreatsAUrlAndAnOwnerNameAsTheSameDestination()
    {
        var route = RepositoryRouter.Resolve(
            Incident("Memex.Portal.Startup", "https://github.com/Systemorph/Memex.git"), Options);

        // Same place as the route, so it is not an "override" at all.
        route.Overridden.Should().BeFalse();
        route.RejectedOverride.Should().BeNull();
        route.Repository.Should().Be("Systemorph/Memex");
    }

    [Theory]
    [InlineData("Systemorph/MeshWeaver", "Systemorph/MeshWeaver")]
    [InlineData("https://github.com/Systemorph/MeshWeaver", "Systemorph/MeshWeaver")]
    [InlineData("https://github.com/Systemorph/MeshWeaver.git", "Systemorph/MeshWeaver")]
    [InlineData("git@github.com:Systemorph/MeshWeaver.git", "Systemorph/MeshWeaver")]
    [InlineData("nonsense", null)]
    [InlineData("", null)]
    public void Normalize_ReducesAnyReferenceToOwnerName(string input, string? expected)
        => RepositoryRouter.Normalize(input).Should().Be(expected);

    [Fact]
    public void Resolve_ReturnsNoDestinationWhenNothingIsConfigured()
        => RepositoryRouter.Resolve(Incident("MeshWeaver.Data.X"), new LogWatchOptions())
            .Repository.Should().BeNull();
}
