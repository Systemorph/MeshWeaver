#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Hosting.Monolith.Test;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[assembly: TestModuleEndpoints]

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// A test module-endpoint contribution living on THIS assembly — the discovery walks
/// <see cref="MeshEndpointProviderAttribute"/>s on installed module assemblies, so the test
/// assembly plays the module.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class TestModuleEndpointsAttribute : MeshEndpointProviderAttribute
{
    public const string SecuredRoute = "/api/test-module/secured";
    public const string PublicRoute = "/api/test-module/public";

    public override IEnumerable<Action<IEndpointRouteBuilder>> EndpointConfigurations =>
    [
        endpoints =>
        {
            endpoints.MapGet(SecuredRoute, () => Results.Ok("secured"));
            endpoints.MapGet(PublicRoute, () => Results.Ok("public")).AllowAnonymous();
        },
    ];
}

/// <summary>
/// Pins the endpoint-contribution hook (design #1655): discovery over
/// <see cref="InstalledModuleAssembly"/>, the authenticated-by-default group with per-route
/// anonymous opt-out, and the loud (verb, pattern) collision refusal.
/// </summary>
public class ModuleEndpointContributionTest
{
    private static WebApplication BuildAppWithTestModule()
    {
        // The app is BUILT but never started — endpoint metadata is inspectable without Kestrel.
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(
            new InstalledModuleAssembly(typeof(ModuleEndpointContributionTest).Assembly));
        var app = builder.Build();
        app.MapMeshModuleEndpoints();
        return app;
    }

    private static IReadOnlyList<RouteEndpoint> ContributedEndpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.Contains("test-module") == true)
            .ToList();

    [Fact]
    public void ModuleEndpoints_AreDiscovered_AndAuthenticatedByDefault()
    {
        using var app = BuildAppWithTestModule();
        var endpoints = ContributedEndpoints(app);
        Assert.Equal(2, endpoints.Count);

        var secured = Assert.Single(endpoints,
            e => e.RoutePattern.RawText == TestModuleEndpointsAttribute.SecuredRoute);
        // The group default: authorization metadata present, no anonymous escape.
        Assert.NotNull(secured.Metadata.GetMetadata<IAuthorizeData>());
        Assert.Null(secured.Metadata.GetMetadata<IAllowAnonymous>());

        // The explicit per-route opt-out is the ONLY way a contributed route is anonymous.
        var open = Assert.Single(endpoints,
            e => e.RoutePattern.RawText == TestModuleEndpointsAttribute.PublicRoute);
        Assert.NotNull(open.Metadata.GetMetadata<IAllowAnonymous>());
    }

    [Fact]
    public void RouteCollisions_AreDetected_WithBothPartiesNamed()
    {
        static RouteEndpoint Endpoint(string pattern, string verb, string display) =>
            new(
                _ => System.Threading.Tasks.Task.CompletedTask,
                RoutePatternFactory.Parse(pattern),
                order: 0,
                new EndpointMetadataCollection(new HttpMethodMetadata([verb])),
                display);

        // Same pattern, same verb — collision, both display names surfaced.
        var detail = MeshModuleEndpointExtensions.FindRouteCollisions(
        [
            Endpoint("/api/x", "GET", "platform: X"),
            Endpoint("/api/x", "GET", "module: X"),
        ]);
        Assert.NotNull(detail);
        Assert.Contains("platform: X", detail);
        Assert.Contains("module: X", detail);

        // Same pattern, DIFFERENT verb — legitimate, no collision.
        Assert.Null(MeshModuleEndpointExtensions.FindRouteCollisions(
        [
            Endpoint("/api/x", "GET", "a"),
            Endpoint("/api/x", "POST", "b"),
        ]));
    }
}
