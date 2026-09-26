using System.Net;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 The app host's publicness decision (<see cref="PublicSite.UsePublicHostRedirect"/>) must ask
/// the mesh AS THE STRANGER it is deciding for — MeshWeaver#5227.
///
/// <para>The middleware is registered after authentication and BEFORE <c>UserContextMiddleware</c>
/// (it must not mint a guest identity for a request that only bounces), so when it runs no
/// <see cref="AccessContext"/> has been established for the request. Its default decision,
/// <see cref="SeoResolver.Resolve"/>, captures the ambient identity for the authoritative owner
/// read — and captured null. The read left <c>portal/reads-{meshId}</c> with no identity, the
/// never-null guard refused it (<c>hub=portal/reads-…, message=GetDataRequest, target=Store was
/// posted with no AccessContext</c>), the resolver's fail-open turned the refusal into "not
/// public", and the app host SERVED the public page instead of redirecting it. Measured on
/// memex.meshweaver.cloud 2026-09-26: every run of the prod synthetic probe logged
/// <c>app host: 200 -&gt; (no redirect)  https://memex.meshweaver.cloud/Store</c> within a second of
/// a <c>target=Store</c> refusal on the pod that answered.</para>
///
/// <para><see cref="PublicSiteTest"/> could not see it: it stubs the decision, which is the one
/// thing that was broken. This test runs the DEFAULT decision over a real mesh with real row-level
/// security and no ambient or host identity to fall back on.</para>
/// </summary>
public class PublicHostRedirectAsksAsTheStrangerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Public = "www.example.test";
    private const string App = "memex.example.test";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .AddMeshNodes(
            new MeshNode("OpenSpace") { NodeType = "Space", Name = "Open space" },
            AssignmentNodeFactory.Policy("OpenSpace", new PartitionAccessPolicy { PublicRead = true }),
            new MeshNode("Guide", "OpenSpace")
            {
                NodeType = "Markdown", Name = "Guide",
                Content = new MarkdownContent { Content = "A public page." },
            },
            new MeshNode("ClosedSpace") { NodeType = "Space", Name = "Closed space" });

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    private WebApplication Host()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PublicSite.PublicHostKey] = Public,
        });
        // The portal's request services resolve the ROOT mesh hub for a request outside a circuit.
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        var app = builder.Build();
        app.UseRouting();
        app.UsePublicHostRedirect();
        app.MapGet("/{**path}", (string? path) => Results.Text($"page:{path}", "text/html"));
        app.Start();
        return app;
    }

    private static HttpRequestMessage StrangerOnTheAppHost(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = App;
        request.Headers.TryAddWithoutValidation("Accept", "text/html");
        return request;
    }

    /// <summary>
    /// Nothing to fall back on: no ambient context, no circuit context and no test-host identity —
    /// the state of a request thread before <c>UserContextMiddleware</c> has run.
    ///
    /// <para>🚨 The path-resolution cache is WARMED first, as it is on any portal that has served a
    /// page. A cold <c>IPathResolver</c> answers from its query's system-scoped continuation, so the
    /// owner read would ride <c>system-security</c> and the old code would pass (the trap recorded in
    /// Doc/Architecture/AccessContextPropagation for #5227's earlier repros).</para>
    /// </summary>
    private async Task<AccessService> NoIdentityAnywhere(string path)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await SeoResolver.Resolve(Mesh, path.Trim('/')).Should().Emit();
        access.ClearHostIdentity();
        access.SetContext(null);
        access.SetCircuitContext(null);
        return access;
    }

    private void Restore(AccessService access)
    {
        access.SetContext(TestUsers.Admin);
        access.SetHostIdentity(TestUsers.Admin);
    }

    [Theory]
    [InlineData("/OpenSpace")]
    [InlineData("/OpenSpace/Guide")]
    public async Task APublicPageAskedForOnTheAppHost_IsRedirectedToThePublicHost(string path)
    {
        var access = await NoIdentityAnywhere(path);
        try
        {
            using var app = Host();
            var response = await app.GetTestClient().SendAsync(StrangerOnTheAppHost(path));

            Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
            Assert.Equal($"https://{Public}{path}", response.Headers.Location?.ToString());
        }
        finally
        {
            Restore(access);
        }
    }

    /// <summary>
    /// The control that keeps the fix honest: asking as the stranger must not widen anything. A
    /// page the anonymous gate refuses stays on the app host (its own sign-in handling answers).
    /// </summary>
    [Fact]
    public async Task APrivatePage_StaysOnTheAppHost()
    {
        var access = await NoIdentityAnywhere("/ClosedSpace");
        try
        {
            using var app = Host();
            var response = await app.GetTestClient().SendAsync(StrangerOnTheAppHost("/ClosedSpace"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("page:ClosedSpace", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Restore(access);
        }
    }
}
