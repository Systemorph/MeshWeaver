using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Hosting.Monolith.TestBase;
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
/// 🚨 THE <c>/static</c> CONTRACT (issue #587), driven over REAL HTTP against a REAL monolith mesh.
///
/// <para><b>What was broken.</b> <c>/static/**</c> resolved any registered content collection and
/// streamed the file with no authentication and no authorization anywhere.
/// <c>/static/storage/content/{node}/{file}</c> read the mesh-level backing store directly, so every
/// partition's uploads, attachments and PDFs were world-readable to anyone with (or guessing) a URL
/// — and the scheme is entirely predictable.</para>
///
/// <para><b>What must hold now.</b> The fix is NOT "check a permission on /static". It is that
/// <c>/static</c> carries application BUILD OUTPUT only — files compiled into a shipped assembly —
/// and performs no permission check at all, because everything reachable there is public by
/// construction. Mesh content is <b>not mounted</b>: it is UNREACHABLE under <c>/static</c>, not
/// merely denied. So the assertions below are 404 — <i>for an anonymous caller AND for a fully
/// entitled admin alike</i>. A 401/403 would mean the content is still there and something is
/// deciding; 404 for everyone is the proof it is gone.</para>
///
/// <para>The same bytes are still served, to an authorized caller, through
/// <c>/api/content/{node}/{file}</c> — proving the content moved rather than disappeared.</para>
/// </summary>
public class StaticContentUnmountedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PrivateSpace = "PrivateSpace";
    private const string SecretFile = "secret.pdf";
    private const string SecretBody = "confidential-bytes";

    /// <summary>A collection that is registered but NOT published — it must 404 on every route.</summary>
    private const string UnpublishedCollection = "internal-store";

    /// <summary>Header the test pipeline reads to stamp the request's identity (see BuildPortal).</summary>
    private const string UserHeader = "X-Test-User";

    // The backing store, laid out exactly as production does it: the mesh-level "storage"
    // collection's BasePath, with each Space's content under content/{nodePath}/….
    private readonly string storageRoot = CreateStorageFixture();

    private WebApplication? portal;
    private HttpClient client = null!;

    private static string CreateStorageFixture()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "static-unmount-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "content", PrivateSpace));
        File.WriteAllText(Path.Combine(root, "content", PrivateSpace, SecretFile), SecretBody);
        return root;
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(PrivateSpace) { Name = "Private Space", NodeType = "Markdown" })
            // The mesh-level raw backing store — the exact registration memex makes
            // (Memex.Portal.Monolith/Program.cs). 🚨 NOT published: this is the collection whose
            // /static/storage/content/{node}/{file} URLs leaked every partition's content.
            .ConfigureHub(hub => hub.AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = "storage",
                SourceType = "FileSystem",
                BasePath = storageRoot,
            }))
            // The per-Space content collection mounted over that store, as MemexConfiguration does
            // it: published (IsStatic) so it is reachable on the ACCESS-CONTROLLED route, and owned
            // by the Space so the read is gated on Read of that node.
            .ConfigureDefaultNodeHub(config => config.Address.ToString() == PrivateSpace
                ? config.AddContentCollection(_ => new ContentCollectionConfig
                {
                    Name = ContentCollectionsExtensions.DefaultCollectionName,
                    SourceType = "FileSystem",
                    BasePath = Path.Combine(storageRoot, "content", PrivateSpace),
                    Address = config.Address,
                    IsEditable = true,
                    ExposeInChildren = true,
                    IsStatic = true,
                })
                // A sibling collection on the same node that is deliberately NOT published.
                .AddContentCollection(_ => new ContentCollectionConfig
                {
                    Name = UnpublishedCollection,
                    SourceType = "FileSystem",
                    BasePath = Path.Combine(storageRoot, "content", PrivateSpace),
                    Address = config.Address,
                })
                : config);

    /// <inheritdoc />
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        portal = BuildPortal();
        await portal.StartAsync(TestContext.Current.CancellationToken);
        client = portal.GetTestClient();
    }

    /// <summary>
    /// The portal's HTTP surface: the REAL <c>MapMeshWeaver()</c> endpoints over the test mesh,
    /// preceded by an identity middleware that does what <c>UserContextMiddleware</c> does in
    /// production — stamp the request's <see cref="AccessContext"/> on the mesh-wide
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
                : TestUsers.Admin);
            await next();
        });
        app.MapMeshWeaver();
        return app;
    }

    private async Task<HttpResponseMessage> GetAsync(string url, bool asAdmin = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (asAdmin)
            request.Headers.Add(UserHeader, TestUsers.Admin.ObjectId);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  /static — content repos are NOT mounted here.
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 THE VULNERABILITY. An unauthenticated caller with nothing but the URL read any partition's
    /// uploaded file through the mesh-level backing store. That shape must not resolve at all.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task BackingStoreUrl_IsNotReachableUnderStatic_ForAnonymous()
    {
        var response = await GetAsync($"/static/storage/content/{PrivateSpace}/{SecretFile}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "/static carries build assets only — a content collection is not mounted there at all");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(SecretBody, "the bytes must never be served from this route");
    }

    /// <summary>
    /// 🚨 THE SHAPE OF THE FIX. The same URL must 404 for a FULLY ENTITLED admin too. If an
    /// authorized caller were served here, the content would still be mounted and the route would
    /// still be making an access decision — which is exactly what must not happen on <c>/static</c>.
    /// Identical answers for both callers is the proof that nothing is being decided.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task BackingStoreUrl_IsNotReachableUnderStatic_EvenForAnEntitledAdmin()
    {
        var response = await GetAsync($"/static/storage/content/{PrivateSpace}/{SecretFile}", asAdmin: true);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "being unmounted is a hosting decision — it must not vary with identity");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(SecretBody);
    }

    /// <summary>The address-based shape is gone from <c>/static</c> as well — for both callers.</summary>
    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddressBasedContentUrl_IsNotReachableUnderStatic(bool asAdmin)
    {
        var response = await GetAsync(
            $"/static/{PrivateSpace}/{ContentCollectionsExtensions.DefaultCollectionName}/{SecretFile}",
            asAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(SecretBody);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  /static — the build assets that DO remain, and take no permission check.
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The node-type icons still serve anonymously with shared-cacheable headers. They are SVGs
    /// compiled into MeshWeaver.Graph — no user data, identical in every deployment, and needed
    /// before any identity exists (the login page, the nav, every anonymous card).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task NodeTypeIcons_ServeAnonymously_WithPublicImmutableCaching()
    {
        var response = await GetAsync("/static/NodeTypeIcons/box.svg");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a build asset must render before sign-in — that is why it is on /static");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("<svg");
        var cacheControl = response.Headers.CacheControl!.ToString();
        cacheControl.Should().Contain("public").And.Contain("immutable",
            "everything on /static is public by construction, so a shared cache may keep it");
    }

    /// <summary>
    /// …and it serves IDENTICALLY for a signed-in caller: no permission is consulted on this route,
    /// so the response cannot depend on who is asking.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task NodeTypeIcons_ServeIdentically_ForAnonymousAndSignedIn()
    {
        var anonymous = await GetAsync("/static/NodeTypeIcons/box.svg");
        var admin = await GetAsync("/static/NodeTypeIcons/box.svg", asAdmin: true);

        admin.StatusCode.Should().Be(anonymous.StatusCode);
        (await admin.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Be(await anonymous.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A path whose first segment names no mount is 404 — not a hint, not a 403.</summary>
    [Fact(Timeout = 30000)]
    public async Task UnknownMount_IsNotFound()
    {
        var response = await GetAsync("/static/storage/anything.txt");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  Path traversal — the guard must sit on the DECODED path.
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 An encoded dot segment survives to the endpoint: ASP.NET Core normalizes a literal
    /// <c>..</c> out of the request line, but the catch-all route value stays percent-encoded and
    /// only becomes <c>..</c> when the endpoint un-escapes it. A double-encoded <c>%252E%252E</c>
    /// reaches the route value as <c>%2E%2E</c> and survives normalization outright. A guard applied
    /// BEFORE decoding waves both straight through — which is why the guard runs on the decoded
    /// path.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData("/static/NodeTypeIcons/%252E%252E/%252E%252E/appsettings.json")]
    [InlineData("/static/NodeTypeIcons/%252E/box.svg")]
    [InlineData("/static/NodeTypeIcons/%2E%2E/%2E%2E/appsettings.json")]
    [InlineData("/static/NodeTypeIcons//box.svg")]
    public async Task TraversalAttempts_AreRefused(string url)
    {
        var response = await GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a dot or empty segment must never be resolved — the guard runs on the DECODED path");
    }

    /// <summary>The same guard on the content route, where a bare <c>Path.Combine</c> in
    /// <c>FileSystemStreamProvider</c> would otherwise read outside the collection's BasePath —
    /// i.e. another partition's files, under a grant the caller legitimately holds here.</summary>
    [Fact(Timeout = 30000)]
    public async Task ContentRoute_RefusesEncodedTraversal()
    {
        var response = await GetAsync(
            $"{ContentCollectionsExtensions.ContentFileRoutePrefix}/{PrivateSpace}/%252E%252E/%252E%252E/{SecretFile}",
            asAdmin: true);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(SecretBody);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  /api/content — the content MOVED, it did not disappear.
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 THE OVER-STRICTNESS CATCHER. The very file that must be unreachable under <c>/static</c>
    /// is still served — to an authorized caller — through the access-controlled route. Without
    /// this, "404 everywhere" would pass by simply breaking the feature.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TheSameFile_IsServedToAnAuthorizedCaller_ThroughTheContentRoute()
    {
        var response = await GetAsync(
            $"{ContentCollectionsExtensions.ContentFileRoutePrefix}/{PrivateSpace}/{SecretFile}",
            asAdmin: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the content moved to the authenticated endpoint — it must still serve there");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Be(SecretBody);
    }

    /// <summary>Gated content is never shared-cacheable: a CDN or proxy that saw one authorized
    /// fetch would otherwise keep replaying it to callers the owning hub denies.</summary>
    [Fact(Timeout = 30000)]
    public async Task ContentRoute_NeverMarksAResponseSharedCacheable()
    {
        var response = await GetAsync(
            $"{ContentCollectionsExtensions.ContentFileRoutePrefix}/{PrivateSpace}/{SecretFile}",
            asAdmin: true);

        var cacheControl = response.Headers.CacheControl!.ToString();
        cacheControl.Should().NotContain("public",
            "an intermediary must not be allowed to store an access-controlled file");
        cacheControl.Should().Contain("private");
    }

    /// <summary>
    /// A collection that is registered but does not declare itself published is 404 on the content
    /// route too — for an admin who unquestionably holds Read. Registering a collection makes it
    /// available in the mesh; publishing its bytes over HTTP is a separate, deliberate act, and the
    /// default is closed. <c>IsStatic</c> used to be declared and read nowhere at all.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task UnpublishedCollection_IsNotServed_EvenToAnAdmin()
    {
        var response = await GetAsync(
            $"{ContentCollectionsExtensions.ContentFileRoutePrefix}/{PrivateSpace}/{UnpublishedCollection}/{SecretFile}",
            asAdmin: true);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(SecretBody);
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
