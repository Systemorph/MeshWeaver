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

    private static RouteEndpoint Endpoint(string pattern, string verb, string display, string? module = null) =>
        new(
            _ => System.Threading.Tasks.Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            order: 0,
            module is null
                ? new EndpointMetadataCollection(new HttpMethodMetadata([verb]))
                : new EndpointMetadataCollection(
                    new HttpMethodMetadata([verb]), new MeshModuleEndpointMetadata(module)),
            display);

    [Fact]
    public void RouteCollisions_AreDetected_WithBothPartiesNamed()
    {
        // Module vs platform on the same (verb, pattern) — collision, both parties surfaced,
        // the module named.
        var detail = MeshModuleEndpointExtensions.FindRouteCollisions(
        [
            Endpoint("/api/x", "GET", "platform: X"),
            Endpoint("/api/x", "GET", "module: X", module: "My.Module"),
        ]);
        Assert.NotNull(detail);
        Assert.Contains("platform: X", detail);
        Assert.Contains("module: X", detail);
        Assert.Contains("My.Module", detail);

        // Module vs module is a collision too.
        Assert.NotNull(MeshModuleEndpointExtensions.FindRouteCollisions(
        [
            Endpoint("/api/x", "GET", "module: A", module: "A"),
            Endpoint("/api/x", "GET", "module: B", module: "B"),
        ]));

        // Same pattern, DIFFERENT verb — legitimate, no collision.
        Assert.Null(MeshModuleEndpointExtensions.FindRouteCollisions(
        [
            Endpoint("/api/x", "GET", "a", module: "A"),
            Endpoint("/api/x", "POST", "b", module: "B"),
        ]));
    }

    /// <summary>
    /// The ci.3958 prod refusal, pinned: a PUBLISHED app's static-asset table registers one
    /// endpoint per precompressed variant (identity/gzip/brotli) on the SAME (verb, pattern),
    /// disambiguated by content negotiation — platform-only duplicates with no module party.
    /// The gate must not refuse them; dev runs never have the variants, so only this shape
    /// catches the regression.
    /// </summary>
    [Fact]
    public void PlatformOnlyDuplicates_PublishedStaticAssetVariants_AreNotCollisions()
    {
        Assert.Null(MeshModuleEndpointExtensions.FindRouteCollisions(
        [
            Endpoint("app.styles.css", "GET", ""),
            Endpoint("app.styles.css", "GET", ""),   // gzip variant
            Endpoint("app.styles.css", "GET", ""),   // brotli variant
            Endpoint("app.styles.css", "HEAD", ""),
            Endpoint("app.styles.css", "HEAD", ""),
            Endpoint("app.styles.css", "HEAD", ""),
        ]));

        // …but the same route colliding WITH a module endpoint still refuses.
        Assert.NotNull(MeshModuleEndpointExtensions.FindRouteCollisions(
        [
            Endpoint("app.styles.css", "GET", ""),
            Endpoint("app.styles.css", "GET", "module route", module: "My.Module"),
        ]));
    }

    [Fact]
    public void ContributedEndpoints_CarryTheModuleMarker()
    {
        using var app = BuildAppWithTestModule();
        foreach (var endpoint in ContributedEndpoints(app))
        {
            var marker = endpoint.Metadata.GetMetadata<MeshModuleEndpointMetadata>();
            Assert.NotNull(marker);
            Assert.Equal(
                typeof(ModuleEndpointContributionTest).Assembly.GetName().Name,
                marker.ModuleName);
        }
    }
}
