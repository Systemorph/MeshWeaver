using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Regression guard for the DevLogin new-user 500: signing in as a never-seen DevLogin id must
/// SELF-PROVISION the user (the documented contract — see <see cref="DevAuthController"/> and
/// NewUserOnboardingTest), not 500.
///
/// <para>The bug: <see cref="DevAuthController.Login"/> reads the partition-root User node first;
/// for a brand-new user that node does not exist, so routing the read to <c>{personId}</c> fails
/// FAST with a <see cref="DeliveryFailureException"/> ("No node found at '{personId}'") — NOT the
/// 5s <see cref="TimeoutException"/> the catch was written for. The uncaught exception escaped the
/// "fall through to provision" path and 500'd the signin, blocking onboarding for every new
/// DevLogin user. The fix catches <see cref="DeliveryFailureException"/> alongside the timeout.</para>
/// </summary>
public class DevAuthControllerNewUserTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureServices(services =>
            {
                services.AddScoped<UserOnboardingService>();
                return services;
            });

    // Pre-warm the User + Partition type hubs so onboarding's post-creation pipeline doesn't
    // cold-start them mid-test (mirrors UserOnboardingServiceTests).
    protected override void PreWarmNodeTypeHubs()
    {
        base.PreWarmNodeTypeHubs();
        foreach (var typeName in new[] { "User", "Partition" })
        {
            var typeNode = Mesh.ServiceProvider.FindStaticNode(typeName);
            if (typeNode?.HubConfiguration is { } cfg)
                _ = Mesh.GetHostedHub(new Address(typeName), cfg);
        }
    }

    /// <summary>
    /// A brand-new personId signs in: the partition-root read misses, the controller falls through
    /// to provisioning, and Login returns a Redirect (NOT a 500/throw) with the User node created.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task DevLogin_BrandNewUser_SelfProvisions_DoesNotThrow()
    {
        var personId = ("devnew" + Guid.NewGuid().ToString("N")).ToLowerInvariant()[..14];

        var controller = CreateController(devLoginEnabled: true);
        var result = await controller.Login(personId, "/");

        result.Should().BeOfType<RedirectResult>(
            "a brand-new DevLogin user self-provisions and is redirected, not 500'd (the bug: the " +
            "partition-root read miss surfaced a DeliveryFailureException that escaped to a 500). " +
            "Login only redirects when ProvisionDevUser returned a valid User node, so this also " +
            "proves the fall-through to self-provisioning ran.");

        // The provisioned partition-root node becomes readable shortly after provisioning; poll past the
        // transient routing miss (GetMeshNodeStream throws "No node found" until the write propagates).
        var node = await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .StartWith(0L)
            .SelectMany(_ => Mesh.GetMeshNodeStream(personId)
                .Take(1)
                .Catch((Exception _) => Observable.Return<MeshNode?>(null)))
            .Where(n => n is not null && n.NodeType == "User" && n.Content is not null)
            .Take(1).Timeout(TimeSpan.FromSeconds(20)).ToTask();
        node!.Id.Should().Be(personId, "onboarding wrote the partition-root User node at the bare id path");
    }

    /// <summary>
    /// Pins the ROOT CAUSE: reading a brand-new user's partition-root node surfaces a
    /// <see cref="DeliveryFailureException"/> ("No node found"), not a <see cref="TimeoutException"/>.
    /// If this ever changes shape, the catch in <see cref="DevAuthController.Login"/> must be revisited.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PartitionRootRead_ForUnknownUser_SurfacesDeliveryFailure_NotTimeout()
    {
        var personId = ("devmiss" + Guid.NewGuid().ToString("N")).ToLowerInvariant()[..14];

        var read = Mesh.GetMeshNodeStream(personId)
            .Where(n => n is not null && string.Equals(n.NodeType, "User", StringComparison.OrdinalIgnoreCase))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(5))
            .ToTask();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => read);
        ex.Should().BeOfType<DeliveryFailureException>(
            "a brand-new user's partition-root node is missing → routing fails fast with 'No node found', " +
            "NOT the 5s Timeout the original catch handled — this is exactly why the catch had to widen");
    }

    private DevAuthController CreateController(bool devLoginEnabled)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:EnableDevLogin"] = devLoginEnabled ? "true" : "false"
            })
            .Build();

        var controller = new DevAuthController(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh.ServiceProvider.GetRequiredService<UserOnboardingService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<AccessService>(),
            config);

        // SignInAsync (the final step of Login) resolves IAuthenticationService from
        // HttpContext.RequestServices — stub it so the controller can be driven without the MVC pipeline.
        var requestServices = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(new StubAuthenticationService())
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()),
            RequestServices = requestServices
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private sealed class StubAuthenticationService : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
            => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
            => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => Task.CompletedTask;
    }
}
