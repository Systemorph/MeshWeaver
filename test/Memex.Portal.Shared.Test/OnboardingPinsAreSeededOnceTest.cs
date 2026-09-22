using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// <see cref="User.PinnedPaths"/> is SEEDED at onboarding and never re-imposed: the first global
/// administrator is created with no learning path, an ordinary user with the four documentation
/// sections, and a second <see cref="UserOnboardingService.CreateUser"/> for a user who exists
/// leaves whatever they have pinned since exactly as it is.
///
/// <para>🚨 Why the second call is the case that matters. <c>BootstrapController.FirstAdmin</c> is
/// documented as re-runnable — "already exists" is a successful step, because a re-run still wants
/// the Admin grants — and <c>CreateUser</c> lands the profile through the full-instance upsert,
/// whose update leg takes <c>Content</c> wholesale. Before this test, a re-run therefore replaced
/// the user's pins with the seed: <c>[]</c> for the first administrator, which silently erased every
/// pin added after the original onboarding (Copilot's finding on #5077). A test that names only the
/// FIRST call cannot see that, which is why the pure <see cref="FirstAdministratorHasNoLearningPathTest"/>
/// did not.</para>
///
/// <para>Real mesh, no doubles: the profile is read back through <c>GetMeshNodeStream</c>, and the
/// second write is proven to have LANDED (its <c>FullName</c> change is awaited on the node) before
/// the pins are asserted — otherwise "the pin is still there" would also be true of a write that
/// never arrived.</para>
/// </summary>
public class OnboardingPinsAreSeededOnceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string[] LearningPath =
        ["Doc/Architecture", "Doc/DataMesh", "Doc/GUI", "Doc/AI"];

    private const string MyOwnPin = "something/i/pinned";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureServices(services =>
            {
                services.AddScoped<UserOnboardingService>();
                return services;
            });

    /// <summary>Pre-warm the User type hub so the partition-root write does not cold-start it.</summary>
    protected override void PreWarmNodeTypeHubs()
    {
        base.PreWarmNodeTypeHubs();
        var typeNode = Mesh.ServiceProvider.FindStaticNode("User");
        if (typeNode?.HubConfiguration is { } cfg)
            _ = Mesh.GetHostedHub(new Address("User"), cfg);
    }

    // ---------------------------------------------------------------- the first call (controls)

    [Fact(Timeout = 60000)]
    public async Task TheFirstAdministratorIsCreatedWithNoLearningPath()
    {
        var username = NewUsername("boot");
        await Onboard(FirstAdministrator(username), TestContext.Current.CancellationToken);

        var profile = await AwaitProfile(username, u => u.Email == Email(username), TestContext.Current.CancellationToken);

        profile.PinnedPaths.Should().BeEmpty(
            "the first global administrator is setting the instance up, not learning it");
    }

    [Fact(Timeout = 60000)]
    public async Task AnOrdinaryUserIsCreatedWithTheLearningPath()
    {
        var username = NewUsername("ord");
        await Onboard(Ordinary(username), TestContext.Current.CancellationToken);

        var profile = await AwaitProfile(username, u => u.Email == Email(username), TestContext.Current.CancellationToken);

        profile.PinnedPaths.Should().Equal(LearningPath,
            "an ordinary new user's Pinned tab opens onto the four documentation sections");
    }

    // ---------------------------------------------------------------- the second call (the finding)

    [Fact(Timeout = 60000)]
    public async Task ARerunOfTheRecoveryEndpointLeavesThePinsAlone()
    {
        // The recovery endpoint, run twice: once to materialise the first administrator, and again
        // later — after they have pinned something — because an operator re-ran it for the grants.
        var username = NewUsername("boot2");
        await Onboard(FirstAdministrator(username), TestContext.Current.CancellationToken);
        await AwaitProfile(username, u => u.Email == Email(username), TestContext.Current.CancellationToken);

        await Pin(username, MyOwnPin, TestContext.Current.CancellationToken);
        await AwaitProfile(username, u => u.PinnedPaths.Contains(MyOwnPin), TestContext.Current.CancellationToken);

        // The re-run carries a different display name so that its landing is OBSERVABLE: the pins
        // are asserted only once the node shows the second write's name.
        await Onboard(FirstAdministrator(username) with { FullName = "Re-run Name" }, TestContext.Current.CancellationToken);
        var profile = await AwaitProfile(username, u => u.FullName == "Re-run Name", TestContext.Current.CancellationToken);

        profile.PinnedPaths.Should().Equal(new[] { MyOwnPin },
            "the seed applies when the profile is CREATED; a re-run of the recovery endpoint must not "
            + "erase what the administrator pinned after the original onboarding");
    }

    [Fact(Timeout = 60000)]
    public async Task ARerunForAnOrdinaryUserKeepsTheirOwnPinsToo()
    {
        // The same rule for an ordinary user, whose seed is the learning path: a second onboarding
        // (a re-submitted form after a lagged existence check) must not re-impose the four sections
        // over what they have curated since.
        var username = NewUsername("ord2");
        await Onboard(Ordinary(username), TestContext.Current.CancellationToken);
        await AwaitProfile(username, u => u.PinnedPaths.SequenceEqual(LearningPath), TestContext.Current.CancellationToken);

        await Unpin(username, "Doc/GUI", TestContext.Current.CancellationToken);
        await Pin(username, MyOwnPin, TestContext.Current.CancellationToken);
        await AwaitProfile(username, u => u.PinnedPaths.Contains(MyOwnPin), TestContext.Current.CancellationToken);

        await Onboard(Ordinary(username) with { FullName = "Re-run Name" }, TestContext.Current.CancellationToken);
        var profile = await AwaitProfile(username, u => u.FullName == "Re-run Name", TestContext.Current.CancellationToken);

        profile.PinnedPaths.Should().Equal(
            new[] { "Doc/Architecture", "Doc/DataMesh", "Doc/AI", MyOwnPin },
            "the learning path is a create-time seed, not a value every onboarding re-imposes");
    }

    // ---------------------------------------------------------------- helpers

    private static string NewUsername(string tag) => $"{tag}-{Guid.NewGuid():N}"[..16].ToLowerInvariant();

    private static string Email(string username) => $"{username}@example.com";

    private static UserOnboardingRequest Ordinary(string username) =>
        new(username, Email(username), FullName: "First Name");

    private static UserOnboardingRequest FirstAdministrator(string username) =>
        Ordinary(username) with { IsPlatformBootstrap = true };

    /// <summary>Drives <see cref="UserOnboardingService.CreateUser"/> exactly as the controller and
    /// the onboarding page do — the service opens its own System scope for the write.</summary>
    private Task<MeshNode> Onboard(UserOnboardingRequest request, CancellationToken cancellationToken)
    {
        var service = Mesh.ServiceProvider.GetRequiredService<UserOnboardingService>();
        Output.WriteLine($"CreateUser {request.Username} bootstrap={request.IsPlatformBootstrap} name={request.FullName}");
        return service.CreateUser(request)
            .FirstAsync()
            .Timeout(TestTimeouts.WriteConvergence)
            .Await(cancellationToken);
    }

    /// <summary>What the user does between the two calls — the ordinary pin write, on the live node.</summary>
    private Task Pin(string username, string path, CancellationToken cancellationToken) =>
        Edit(username, u => u with { PinnedPaths = [.. u.PinnedPaths, path] }, cancellationToken);

    private Task Unpin(string username, string path, CancellationToken cancellationToken) =>
        Edit(username, u => u with { PinnedPaths = [.. u.PinnedPaths.Where(p => p != path)] }, cancellationToken);

    private Task Edit(string username, Func<User, User> change, CancellationToken cancellationToken)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(username)
                .Update(node => node.ContentAs<User>(Mesh.JsonSerializerOptions) is { } u
                    ? node with { Content = change(u) }
                    : node))
            .FirstAsync()
            .Timeout(TestTimeouts.WriteConvergence)
            .Await(cancellationToken);
    }

    private async Task<User> AwaitProfile(string username, Func<User, bool> predicate, CancellationToken cancellationToken)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(username)
            .Where(n => n?.ContentAs<User>(Mesh.JsonSerializerOptions) is { } u && predicate(u))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(cancellationToken);
        var profile = node.ContentAs<User>(Mesh.JsonSerializerOptions)!;
        Output.WriteLine($"profile {username}: name={profile.FullName} pins=[{string.Join(", ", profile.PinnedPaths)}]");
        return profile;
    }
}
