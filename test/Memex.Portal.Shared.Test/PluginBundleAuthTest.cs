using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The bundle routes hand out COMPILED ASSEMBLIES for paid modules, so they must fail closed.
///
/// <para>🚨 <b>Every assertion here demands exactly 401, never merely "not 200".</b> A typo'd route
/// answers 404, which satisfies "not success" while proving nothing — the guard would pass having
/// tested an endpoint that does not exist. 401 is the only status that can only come from the
/// filter, so it is simultaneously the security assertion and the proof the route is mapped.</para>
///
/// <para>The registry's <c>/api/plugins</c> keeps an anonymous escape hatch for local dev. These
/// routes deliberately do not: "open when unconfigured" is how private sources were once served to
/// anyone who knew the URL, and the blast radius is larger here.</para>
///
/// <para>No <see cref="IMessageHub"/> is registered at all, and that is deliberate: the rejection
/// path must need nothing but the header. <c>InstanceRegistryAuthenticator.Authenticate</c> returns
/// null the moment <c>InstanceKeys.ExtractKey</c> finds no key, without touching the mesh.</para>
/// </summary>
public class PluginBundleAuthTest
{
    /// <summary>Both routes, with values that would resolve if the caller were authenticated.</summary>
    public static TheoryData<string> BundleRoutes =>
    [
        PluginBundleEndpoints.RoutePrefix + "/index.json",
        PluginBundleEndpoints.RoutePrefix + "/ThreeBody/1.3.2",
    ];

    private static async Task<HttpResponseMessage> Get(string route, string? authorization)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // 🚨 NO IMessageHub is registered, deliberately — that is half the assertion. The routes
        // resolve the hub inside the handler rather than binding it as a parameter, because
        // minimal-API binds arguments BEFORE filters run: a bound hub would make an unauthenticated
        // request depend on the mesh being resolvable and answer 500 instead of 401. With nothing
        // registered here, any regression to parameter binding fails these tests immediately.
        builder.Services.AddSingleton<InstanceRegistryAuthenticator>(sp =>
            new InstanceRegistryAuthenticator(
                null!,
                sp.GetRequiredService<
                    Microsoft.Extensions.Logging.ILogger<InstanceRegistryAuthenticator>>()));

        var app = builder.Build();
        app.MapPluginBundles();
        await app.StartAsync();

        var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);

        return await client.SendAsync(request);
    }

    [Theory]
    [MemberData(nameof(BundleRoutes))]
    public async Task NoCredentialIsRejected(string route)
    {
        using var response = await Get(route, authorization: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(BundleRoutes))]
    public async Task AnUnparseableBearerIsRejected(string route)
    {
        using var response = await Get(route, "Bearer not-an-instance-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(BundleRoutes))]
    public async Task AnUnparseableBasicIsRejected(string route)
    {
        // Basic is accepted as a SCHEME (a NuGet-style client cannot send Bearer), which must not
        // become a second, weaker way in: the password half still has to be a real instance key.
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:not-a-key"));

        using var response = await Get(route, header);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheRejectionNamesWhatIsMissing()
    {
        // An operator wiring up a consumer sees this body. "401" alone sends them to the wrong
        // place — a personal mw_ token, an OAuth flow — when what is needed is the instance key.
        using var response = await Get(PluginBundleEndpoints.RoutePrefix + "/index.json", null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("instance key", body, StringComparison.OrdinalIgnoreCase);
    }
}
