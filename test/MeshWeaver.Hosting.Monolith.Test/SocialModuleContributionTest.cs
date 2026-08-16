#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Social;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the Social module's shape as the FIRST consumer of the endpoint-contribution hook
/// (design #1655): the assembly carries BOTH module attributes (mesh half + endpoint half),
/// installing it applies the same <c>AddSocial()</c> a fixture calls (the two lanes must not
/// drift), and its endpoint contributions map through <c>MapMeshModuleEndpoints</c> with the
/// exact auth semantics the compiled-in registration had — authenticated by default, the two
/// OAuth callbacks anonymous by explicit opt-out.
/// </summary>
public class SocialModuleContributionTest
{
    [Fact]
    public void TheAssembly_CarriesBothModuleAttributes_WithNonEmptyContributions()
    {
        var assembly = typeof(SocialModuleAttribute).Assembly;

        // Mesh half: the Modules:Assemblies install path folds AddSocial over the builder.
        var meshHalf = Assert.Single(assembly.GetCustomAttributes<MeshNodeProviderAttribute>());
        Assert.NotEmpty(meshHalf.BuilderConfigurations);

        // Endpoint half: the host's MapMeshModuleEndpoints applies these.
        var endpointHalf = Assert.Single(assembly.GetCustomAttributes<MeshEndpointProviderAttribute>());
        Assert.NotEmpty(endpointHalf.EndpointConfigurations);
    }

    [Fact]
    public void InstallingTheAssembly_AppliesAddSocial()
    {
        var serviceConfigs = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure), AddressExtensions.CreateMeshAddress());

        builder.InstallAssemblies(typeof(SocialModuleAttribute).Assembly.Location);

        var services = serviceConfigs.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        // The two node-menu providers, scoped, on the INodeMenuProvider multi-registration.
        var menuProviders = services
            .Where(d => d.ServiceType == typeof(INodeMenuProvider))
            .ToList();
        Assert.Contains(menuProviders, d =>
            d.ImplementationType == typeof(LinkedInCredentialMenuProvider) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(menuProviders, d =>
            d.ImplementationType == typeof(SocialPostMenuProvider) && d.Lifetime == ServiceLifetime.Scoped);

        // Options ride the options pipeline (Social:LinkedIn) with the bare-instance bridge
        // LinkedInPublisher's ctor consumes; the publisher itself is the typed HTTP client.
        Assert.Contains(services, d => d.ServiceType == typeof(IConfigureOptions<LinkedInOptions>));
        Assert.Contains(services, d => d.ServiceType == typeof(LinkedInOptions));
        Assert.Contains(services, d => d.ServiceType == typeof(LinkedInPublisher));
    }

    [Fact]
    public void SocialEndpoints_MapThroughTheHook_WithTheExactAuthSemantics()
    {
        // Built but never started — endpoint metadata is inspectable without Kestrel
        // (same shape as ModuleEndpointContributionTest).
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(
            new InstalledModuleAssembly(typeof(SocialModuleAttribute).Assembly));
        using var app = builder.Build();
        app.MapMeshModuleEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.Contains("linkedin", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        // The full compiled surface: connect (3) + page sync (3) + publish/engagement (3).
        string[] expected =
        [
            "/connect/linkedin/me",
            "/connect/linkedin",
            "/connect/linkedin/callback",
            "/connect/linkedin/org",
            "/connect/linkedin/org/callback",
            "/connect/linkedin/sync",
            "/linkedin/publish",
            "/linkedin/engagement",
        ];
        foreach (var pattern in expected)
            Assert.Contains(endpoints, e => e.RoutePattern.RawText == pattern);
        Assert.Equal(9, endpoints.Count); // /linkedin/publish maps twice (GET + POST)

        // The ONLY anonymous routes are the two OAuth redirect targets — LinkedIn's callback
        // must not bounce through a login challenge (the CSRF state cookie is the guard).
        string[] anonymous = ["/connect/linkedin/callback", "/connect/linkedin/org/callback"];
        foreach (var endpoint in endpoints)
        {
            if (anonymous.Contains(endpoint.RoutePattern.RawText))
            {
                Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            }
            else
            {
                Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>());
                Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            }
        }
    }
}
