#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Grpc.AspNetCore.Web;
using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Hosting.Grpc;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the gRPC mesh transport's module shape — the SECOND consumer of the
/// endpoint-contribution hook (design #1655, after <c>MeshWeaver.Social</c>): the assembly
/// carries BOTH module attributes (mesh half + endpoint half), installing it applies the same
/// <c>AddGrpcHub()</c> a fixture calls (the two lanes must not drift), and the
/// <c>meshweaver.v1.Mesh</c> endpoints map through <c>MapMeshModuleEndpoints</c> with the exact
/// auth semantics the compiled-in registration had — grpc-web enabled and ANONYMOUS by explicit
/// opt-out, because the transport authenticates each connection itself (Bearer API token in
/// gRPC call metadata / trusted loopback port; see <c>MeshGrpcService</c>). This module is
/// DEFAULT-ON: the React GUI's browser data plane (Connect+Deliver split) rides the same
/// endpoint as the py/node foreign participants.
/// </summary>
public class GrpcModuleContributionTest
{
    [Fact]
    public void TheAssembly_CarriesBothModuleAttributes_WithNonEmptyContributions()
    {
        var assembly = typeof(GrpcModuleAttribute).Assembly;

        // Mesh half: the Modules:Assemblies install path folds AddGrpcHub over the builder.
        var meshHalf = Assert.Single(assembly.GetCustomAttributes<MeshNodeProviderAttribute>());
        Assert.NotEmpty(meshHalf.BuilderConfigurations);

        // Endpoint half: the host's MapMeshModuleEndpoints applies these.
        var endpointHalf = Assert.Single(assembly.GetCustomAttributes<MeshEndpointProviderAttribute>());
        Assert.NotEmpty(endpointHalf.EndpointConfigurations);
    }

    [Fact]
    public void InstallingTheAssembly_AppliesAddGrpcHub()
    {
        var serviceConfigs = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure), AddressExtensions.CreateMeshAddress());

        builder.InstallAssemblies(typeof(GrpcModuleAttribute).Assembly.Location);

        var services = serviceConfigs.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        // The connection registry, singleton, plus its IParticipantPresence face (the fail-fast
        // presence check for foreign-language Code runs).
        Assert.Contains(services, d =>
            d.ServiceType == typeof(GrpcConnectionRegistry) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IParticipantPresence) && d.Lifetime == ServiceLifetime.Singleton);

        // Options ride the options pipeline (Grpc:TrustedPort — the loopback trust boundary).
        // The py/node stream-routed address-type declarations ride the SAME AddGrpcHub code path
        // pinned here (MeshBuilder.StreamRoutedAddressTypes is internal, so the service
        // registrations are the observable proof the fold ran).
        Assert.Contains(services, d => d.ServiceType == typeof(IConfigureOptions<GrpcOptions>));
    }

    [Fact]
    public void GrpcEndpoints_MapThroughTheHook_AnonymousByExplicitOptOut()
    {
        // Built but never started — endpoint metadata is inspectable without Kestrel
        // (same shape as SocialModuleContributionTest).
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        // ONLY the gRPC marker services MapGrpcService needs — deliberately NOT AddGrpcHub():
        // the module's DI half registers GrpcConnectionRegistry, a type-registered singleton
        // whose ctor takes IMessageHub, and this host has no mesh. Under CI's
        // DOTNET_ENVIRONMENT=Development, WebApplication.CreateBuilder turns on ValidateOnBuild,
        // which eagerly validates every such descriptor and fails Build() on the unresolvable
        // hub (a check Production/local skips — the exact false-local-green this comment pins).
        // The DI half itself is asserted descriptor-level by InstallingTheAssembly_AppliesAddGrpcHub.
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(
            new InstalledModuleAssembly(typeof(GrpcModuleAttribute).Assembly));
        using var app = builder.Build();
        app.MapMeshModuleEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        // The full service surface: the bidi stream + the grpc-web Connect/Deliver split the
        // React GUI uses.
        string[] expected =
        [
            "/" + MeshGrpcService.ServiceName + "/Open",
            "/" + MeshGrpcService.ServiceName + "/Connect",
            "/" + MeshGrpcService.ServiceName + "/Deliver",
        ];
        foreach (var pattern in expected)
        {
            var endpoint = Assert.Single(endpoints, e => e.RoutePattern.RawText == pattern);

            // ANONYMOUS by explicit opt-out of the hook's authenticated-by-default group: the
            // transport authenticates connections ITSELF (Bearer token in gRPC metadata /
            // trusted loopback port) — ASP.NET-level authorization would break the foreign
            // gates' mw_ tokens and the credential-less trusted-port path.
            Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());

            // grpc-web stays enabled — the browser Connect+Deliver split depends on it.
            Assert.NotNull(endpoint.Metadata.GetMetadata<EnableGrpcWebAttribute>());
        }
    }
}
