#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Mcp;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the MCP server's module shape: the assembly carries BOTH module halves, installing it
/// folds the same <c>AddMeshMcp()</c> the portal used to compile in, and <c>/mcp</c> maps through
/// <c>MapMeshModuleEndpoints</c> still gated by the <c>McpAuth</c> policy.
///
/// <para>🚨 The policy is named by STRING on purpose. <c>McpAuth</c> is registered by the portal
/// composition root (<c>McpAuthenticationExtensions</c>), which also gates the REST mirror
/// <c>/api/mesh/*</c> — a surface that outlives a delisted MCP module — so the auth scheme must
/// NOT ride this module and there is no compiled edge from here back to the host.</para>
/// </summary>
public class McpModuleContributionTest
{
    [Fact]
    public void TheAssembly_CarriesBothModuleAttributes_WithNonEmptyContributions()
    {
        var assembly = typeof(McpMeshModuleAttribute).Assembly;

        var meshHalf = Assert.Single(assembly.GetCustomAttributes<MeshNodeProviderAttribute>());
        Assert.NotEmpty(meshHalf.Nodes);

        var endpointHalf = Assert.Single(assembly.GetCustomAttributes<MeshEndpointProviderAttribute>());
        Assert.NotEmpty(endpointHalf.EndpointConfigurations);
    }

    [Fact]
    public void InstallingTheAssembly_AppliesAddMeshMcp()
    {
        var serviceConfigs = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure), AddressExtensions.CreateMeshAddress());

        builder.InstallAssemblies(typeof(McpMeshModuleAttribute).Assembly.Location);

        var services = serviceConfigs.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        // The tool surface: one McpServerTool descriptor per [McpServerTool] method on
        // McpMeshPlugin — the observable proof WithToolsFromAssembly ran in the fold.
        Assert.Contains(services, d => d.ServiceType == typeof(McpServerTool));
        // The resources half (tools-reference et al.).
        Assert.Contains(services, d => d.ServiceType == typeof(McpServerResource));
        // Mcp:BaseUrl rides the options pipeline — the same binding the REST mirror keeps
        // platform-side so a delisted module cannot take /api/mesh/base_url with it.
        Assert.Contains(services, d =>
            d.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<McpConfiguration>));
    }

    [Fact]
    public void McpEndpoint_MapsThroughTheHook_StillGatedByMcpAuth()
    {
        // Built but never started — endpoint metadata is inspectable without Kestrel
        // (same shape as GrpcModuleContributionTest).
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        // Only what MapMcp itself needs. Deliberately NOT the module's full DI half: its tools
        // resolve McpMeshPlugin, whose ctor takes IMessageHub, and this host has no mesh — under
        // CI's DOTNET_ENVIRONMENT=Development, ValidateOnBuild would fail Build() on that. The DI
        // half is asserted descriptor-level by InstallingTheAssembly_AppliesAddMeshMcp.
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true);
        builder.Services.AddSingleton(
            new InstalledModuleAssembly(typeof(McpMeshModuleAttribute).Assembly));
        using var app = builder.Build();
        app.MapMeshModuleEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/mcp", StringComparison.Ordinal) == true)
            .ToList();

        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
        {
            // NOT anonymous — the hook's authenticated-by-default group applies, and the module
            // adds the Bearer-only McpAuth policy on top (never a cookie redirect to /login).
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.Contains(
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                data => data.Policy == "McpAuth");
        }
    }
}
