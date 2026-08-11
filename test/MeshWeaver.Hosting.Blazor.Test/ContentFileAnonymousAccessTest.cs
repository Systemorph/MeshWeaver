using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// 🚨 THE <c>/api/content</c> AUTHORIZATION CONTRACT, driven over REAL HTTP against a REAL monolith
/// mesh with the REAL <see cref="PermissionEvaluator"/> (<c>AddRowLevelSecurity</c>, no mocks).
///
/// <para><b>What was broken.</b> <c>/api/content/{node}/{file}</c> — the route
/// <c>UserContextMiddleware</c> calls "the access-controlled route", and the route
/// <see cref="StaticContentUnmountedTest"/> moved every partition's uploads onto — served a file
/// out of a PRIVATE partition to a fully ANONYMOUS caller. Reproduced against production:
/// <c>GET /api/content/rbuergi/content/_icontest.png</c> returned <c>200 image/png</c> for a
/// caller with no cookie and no token, on a partition whose only grants name three users.</para>
///
/// <para><b>Why the gap existed.</b> The route's own doc says the collection-config
/// <c>GetDataRequest</c> "IS the permission check". That request is answered by the owning node's
/// hub, and the answer — the collection's <i>configuration</i> — is not the node's content, so the
/// read-permission rule that guards node content never denied it. The previous test suite only
/// ever exercised this route as an ADMIN (<see cref="StaticContentUnmountedTest"/> has no
/// anonymous case at all), so a permissive answer looked identical to a correct one.</para>
///
/// <para><b>What must hold now.</b> A collection file inherits EXACTLY the authorization of its
/// owning node — the same predicate the crawler-facing SEO route already uses
/// (<see cref="AnonymousGate.AllowAnonymous"/>): a logged-out caller is served only where the node
/// carries an explicit positive Anonymous Read grant, and an indeterminate answer denies.</para>
/// </summary>
public class ContentFileAnonymousAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// A USER partition with NO anonymous grant — the EXACT production shape of the disclosure
    /// (<c>rbuergi</c>). It must be a real <c>User</c> node, not a Markdown one: the permissive
    /// rule lives on the <c>User</c> node type's hub configuration
    /// (<c>UserNodeType.WithUserNodePublicRead</c>), so a Markdown fixture cannot reproduce this
    /// and would "pass" against the unfixed code.
    /// </summary>
    private const string PrivateSpace = "privateuser";

    /// <summary>A partition carrying an explicit Anonymous Viewer grant — the public plugin page
    /// shape (<c>/api/content/BusinessRules/content/og-card.png</c>), which MUST keep working.</summary>
    private const string PublicSpace = "PublicSpace";

    private const string SecretFile = "secret.pdf";
    private const string SecretBody = "confidential-bytes";
    private const string PublicFile = "og-card.txt";
    private const string PublicBody = "public-share-card";

    /// <summary>The owner of <see cref="PrivateSpace"/> — the partition is theirs.</summary>
    private const string Owner = PrivateSpace;

    /// <summary>An authenticated caller holding no grant anywhere.</summary>
    private const string Stranger = "stranger-user";

    private static readonly string UserHeader = "X-Test-User";

    private readonly string storageRoot = CreateStorageFixture();

    private WebApplication? portal;
    private HttpClient client = null!;

    private static string CreateStorageFixture()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "content-gate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "content", PrivateSpace));
        Directory.CreateDirectory(Path.Combine(root, "content", PublicSpace));
        File.WriteAllText(Path.Combine(root, "content", PrivateSpace, SecretFile), SecretBody);
        File.WriteAllText(Path.Combine(root, "content", PublicSpace, PublicFile), PublicBody);
        return root;
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                // 🚨 A real User node — this is what carries the permissive hub rule in production.
                new MeshNode(PrivateSpace) { Name = "Private User", NodeType = "User" },
                new MeshNode(PublicSpace) { Name = "Public Space", NodeType = "Markdown" },
                // The owner may read their own partition; nobody else is named.
                AssignmentNodeFactory.UserRole(Owner, "Admin", PrivateSpace),
                // The public partition carries the explicit Anonymous grant — exactly what
                // AnonymousGate keys on, and exactly what the shipped public plugin pages have.
                AssignmentNodeFactory.UserRole(
                    WellKnownUsers.Anonymous, "Viewer", PublicSpace,
                    accessObject: WellKnownUsers.Anonymous))
            .ConfigureDefaultNodeHub(config =>
                config.Address.ToString() is PrivateSpace or PublicSpace
                    ? config.AddContentCollection(_ => new ContentCollectionConfig
                    {
                        Name = ContentCollectionsExtensions.DefaultCollectionName,
                        SourceType = "FileSystem",
                        BasePath = Path.Combine(storageRoot, "content", config.Address.ToString()!),
                        Address = config.Address,
                        IsEditable = true,
                        ExposeInChildren = true,
                        IsStatic = true,
                    })
                    : config);

    // Granular permissions — skip the blanket PublicAdmin seed, exactly as the other
    // access-control suites do; otherwise every caller trivially holds Read.
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        portal = BuildPortal();
        await portal.StartAsync(TestContext.Current.CancellationToken);
        client = portal.GetTestClient();
    }

    /// <summary>
    /// The REAL <c>MapMeshWeaver()</c> endpoints, preceded by the identity middleware production
    /// runs: the request's <see cref="AccessContext"/> is stamped on the mesh-wide
    /// <c>AccessService</c>, NEVER null, anonymous when unauthenticated.
    /// </summary>
    private WebApplication BuildPortal()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(Mesh);

        var app = builder.Build();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        app.Use(async (ctx, next) =>
        {
            var userId = ctx.Request.Headers[UserHeader].ToString();
            accessService.SetContext(string.IsNullOrEmpty(userId)
                ? new AccessContext { ObjectId = WellKnownUsers.Anonymous, Name = WellKnownUsers.Anonymous }
                : new AccessContext { ObjectId = userId, Name = userId });
            await next();
        });
        app.MapMeshWeaver();
        return app;
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string? asUser = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(asUser))
            request.Headers.Add(UserHeader, asUser);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Url(string space, string file) =>
        $"{ContentCollectionsExtensions.ContentFileRoutePrefix}/{space}/{file}";

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  (a) THE VULNERABILITY — anonymous must NOT read a private partition's collection file.
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 THE REGRESSION TEST FOR THE DISCLOSURE. A caller with no identity at all asks for a file
    /// in a partition that grants nothing to Anonymous. It must be refused, and — the part that
    /// actually matters — the bytes must not appear in the response under any status code.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AnonymousRead_OfAPrivatePartitionsFile_IsDenied()
    {
        var response = await GetAsync(Url(PrivateSpace, SecretFile));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(SecretBody,
            "a partition with no Anonymous grant must never serve its content to a logged-out caller");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a denied file answers exactly as a missing one — the 404 must not confirm it exists");
    }

    /// <summary>
    /// The same refusal for a file addressed through the EXPLICIT collection segment
    /// (<c>{node}/{collection}/{file}</c>). The resolver reads the two shapes differently, so the
    /// gate has to hold on both — a gate that only covers the default-collection shape leaves the
    /// documented URL form open.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AnonymousRead_ViaTheExplicitCollectionSegment_IsAlsoDenied()
    {
        var response = await GetAsync(
            $"{ContentCollectionsExtensions.ContentFileRoutePrefix}/{PrivateSpace}/"
            + $"{ContentCollectionsExtensions.DefaultCollectionName}/{SecretFile}");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(SecretBody);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  (b) THE OVER-STRICTNESS CATCHER — an anonymous grant still serves.
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 Without this, "deny everything anonymous" would pass by breaking every public page. The
    /// shipped public plugin covers (<c>/api/content/BusinessRules/content/og-card.png</c>) are
    /// exactly this shape — a partition carrying an explicit Anonymous Viewer grant — and they must
    /// keep serving to a logged-out visitor.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AnonymousRead_OfAPartitionWithAnAnonymousGrant_Succeeds()
    {
        var response = await GetAsync(Url(PublicSpace, PublicFile));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an explicit Anonymous Read grant is what 'published' means — it must still serve");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Be(PublicBody);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  (c) + (d) — the authenticated cases bracket the gate from both sides.
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An authenticated caller who simply holds no grant on the partition is refused too. This is
    /// the assertion that proves the gate keys on the PERMISSION and not merely on "has a cookie".
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AuthenticatedNonMember_IsDenied()
    {
        var response = await GetAsync(Url(PrivateSpace, SecretFile), asUser: Stranger);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(SecretBody,
            "signing in is not a grant — the caller holds nothing on this partition");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The grant holder is served. Without this the whole feature could be "fixed" by refusing
    /// everyone, and the private-partition uploads that legitimately render in the product would
    /// silently become broken images.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TheGrantHolder_IsServed()
    {
        var response = await GetAsync(Url(PrivateSpace, SecretFile), asUser: Owner);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the owner holds Viewer on this partition — the file must still serve");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Be(SecretBody);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        client?.Dispose();
        if (portal is not null)
            await portal.DisposeAsync();
        try
        {
            if (Directory.Exists(storageRoot))
                Directory.Delete(storageRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leftover fixture directory is harmless; never fail teardown on it.
        }
        await base.DisposeAsync();
    }
}
