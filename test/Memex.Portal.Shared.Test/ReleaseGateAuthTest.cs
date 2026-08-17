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
/// The release gate answers with this environment's installed packages and the reasons a release is
/// unsafe for them — deployment inventory, not public information. So it fails closed, like the
/// bundle routes beside it.
///
/// <para>🚨 <b>Every assertion demands exactly 401, never merely "not 200".</b> A typo'd route
/// answers 404, which satisfies "not success" while proving nothing — the guard would pass having
/// tested an endpoint that does not exist. 401 can only come from the authenticator, so it is
/// simultaneously the security assertion and the proof the route is mapped.</para>
///
/// <para>No <see cref="IMessageHub"/> and no <c>ReleaseAvailabilityService</c> are registered, and
/// that is half the assertion: the rejection path must need nothing but the header. If the handler
/// ever resolved the service (or bound the hub as a parameter) before authenticating, an
/// unauthenticated request would answer 500 and these tests would catch it immediately.</para>
/// </summary>
public class ReleaseGateAuthTest
{
    private static async Task<HttpResponseMessage> Get(string route, string? authorization)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<InstanceRegistryAuthenticator>(sp =>
            new InstanceRegistryAuthenticator(
                null!,
                sp.GetRequiredService<
                    Microsoft.Extensions.Logging.ILogger<InstanceRegistryAuthenticator>>()));

        var app = builder.Build();
        app.MapReleaseGate();
        await app.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);

        return await app.GetTestClient().SendAsync(request);
    }

    [Fact]
    public async Task NoCredentialIsRejected()
    {
        using var response = await Get(
            ReleaseGateEndpoints.Route + "?version=3.0.0", authorization: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnUnparseableBearerIsRejected()
    {
        using var response = await Get(
            ReleaseGateEndpoints.Route + "?version=3.0.0", "Bearer not-an-instance-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnUnparseableBasicIsRejected()
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("user:nope"));

        using var response = await Get(
            ReleaseGateEndpoints.Route + "?version=3.0.0", "Basic " + basic);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AMissingVersion_IsStillRejectedAsUnauthorized_NotAsABadRequest()
    {
        // Order matters: authenticate BEFORE validating the query. Answering 400 to an
        // unauthenticated caller would confirm the route exists and leak its contract.
        using var response = await Get(ReleaseGateEndpoints.Route, authorization: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
