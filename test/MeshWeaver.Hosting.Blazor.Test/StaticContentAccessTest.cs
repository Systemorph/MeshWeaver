using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Hosting.Blazor;
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
/// 🚨 THE SECURITY BOUNDARY of the <c>/static/**</c> content endpoint (issue #587), driven over
/// REAL HTTP against a REAL monolith mesh — real <c>PermissionEvaluator</c>, real
/// <c>AccessAssignment</c> nodes, real content collections. No mocks.
///
/// <para><b>What was broken.</b> The endpoint resolved a collection and streamed the file with no
/// authorization anywhere. <c>/static/storage/content/{node}/{file}</c> read the mesh-level backing
/// store directly, so every partition's uploads, attachments and PDFs were world-readable to anyone
/// with (or guessing) a URL — and the scheme is entirely predictable. The fixture below reproduces
/// exactly that shape: one mesh-level <c>storage</c> collection whose <c>content/{nodePath}/…</c>
/// layout is what the per-Space mounts create in production.</para>
///
/// <para><b>What must hold now.</b> A file is owned by the mesh node whose collection it lives in,
/// and the caller needs <see cref="Permission.Read"/> on that node — the same decision
/// <c>AccessControlPipeline</c> applies to the <c>GetDataRequest</c> behind an ordinary
/// <c>/content/…</c> read. The gate therefore inherits the paywall for free: the seed is the
/// production paywall shape (root allow for Anonymous/Public = the public cover, per-child DENY =
/// the paywall, per-buyer allow at the root = the entitlement), so the cover asset stays anonymous
/// while the lesson's media does not.</para>
/// </summary>
public class StaticContentAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output), IAsyncDisposable
{
    private const string Course = "GatedCourse";
    private const string PaidLesson = Course + "/PaidLesson";
    private const string PrivateSpace = "PrivateSpace";
    private const string Buyer = "buyer";
    private const string Stranger = "stranger";
    private const string UnmountedCollection = "internal-store";

    private const string CoverBody = "cover-bytes";
    private const string LessonBody = "paid-lesson-bytes";
    private const string SecretBody = "confidential-bytes";

    /// <summary>Header the test pipeline reads to stamp the request's identity (see BuildPortal).</summary>
    private const string UserHeader = "X-Test-User";

    // The backing store, laid out exactly as production does it: the mesh-level "storage"
    // collection's BasePath, with each Space's content under content/{nodePath}/…. Built in a field
    // initializer because ConfigureMesh (which needs the path) runs from the base construction.
    private readonly string storageRoot = CreateStorageFixture();

    private WebApplication? portal;
    private HttpClient client = null!;

    private static string CreateStorageFixture()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "static-gate-store-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "content", Course, "PaidLesson"));
        Directory.CreateDirectory(Path.Combine(root, "content", PrivateSpace));
        File.WriteAllText(Path.Combine(root, "content", Course, "cover.png"), CoverBody);
        File.WriteAllText(Path.Combine(root, "content", Course, "PaidLesson", "lesson.mp4"), LessonBody);
        File.WriteAllText(Path.Combine(root, "content", PrivateSpace, "secret.pdf"), SecretBody);
        return root;
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // ConfigureMeshBase, NOT ConfigureMesh: the default seeds a root Public/Admin grant, which
        // would make every assertion below vacuous.
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Course) { Name = "Gated Course", NodeType = "Markdown" },
                new MeshNode("PaidLesson", Course) { Name = "Paid Lesson", NodeType = "Markdown" },
                new MeshNode(PrivateSpace) { Name = "Private Space", NodeType = "Markdown" },
                // ── the production paywall shape ──────────────────────────────────────────────
                // The public cover: the course root grants Anonymous (and Public) Viewer.
                AssignmentNodeFactory.UserRole(
                    WellKnownUsers.Anonymous, "Viewer", Course, accessObject: WellKnownUsers.Anonymous),
                AssignmentNodeFactory.UserRole(
                    WellKnownUsers.Public, "Viewer", Course, accessObject: WellKnownUsers.Public),
                // The paywall: a per-child DENY for the un-entitled subjects.
                AssignmentNodeFactory.UserRole(
                    WellKnownUsers.Anonymous, "Viewer", PaidLesson, denied: true,
                    accessObject: WellKnownUsers.Anonymous),
                AssignmentNodeFactory.UserRole(
                    WellKnownUsers.Public, "Viewer", PaidLesson, denied: true,
                    accessObject: WellKnownUsers.Public),
                // The entitlement: the buyer's own grant at the ROOT reaches the gated child,
                // because a deny binds only the subject it names.
                AssignmentNodeFactory.UserRole(Buyer, "Viewer", Course))
            // The mesh-level raw backing store — the exact registration memex makes
            // (Memex.Portal.Monolith/Program.cs, Memex.Portal.Distributed/Program.cs).
            .ConfigureHub(hub => hub.AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = "storage",
                SourceType = "FileSystem",
                BasePath = storageRoot,
                IsStatic = true,
            }))
            // A collection that is NOT mounted on /static. It resolves perfectly well for every
            // other consumer ($Content, the file browser, autocomplete) — /static must still
            // refuse to publish it.
            .ConfigureHub(hub => hub.AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = UnmountedCollection,
                SourceType = "FileSystem",
                BasePath = storageRoot,
            }));

    // Granular permissions — skip the harness's blanket PublicAdmin seed.
    /// <inheritdoc />
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
        // The endpoint resolves its mesh hub from the web app's provider.
        builder.Services.AddSingleton(Mesh);

        var app = builder.Build();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        app.Use(async (ctx, next) =>
        {
            var userId = ctx.Request.Headers[UserHeader].ToString();
            accessService.SetContext(new AccessContext
            {
                ObjectId = string.IsNullOrEmpty(userId) ? WellKnownUsers.Anonymous : userId,
                Name = string.IsNullOrEmpty(userId) ? WellKnownUsers.Anonymous : userId,
            });
            await next();
        });
        app.MapMeshWeaver();
        return app;
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string? asUser = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (asUser is not null)
            request.Headers.Add(UserHeader, asUser);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  Pattern 1 — /static/{collection}/{file} against the mesh-level backing store.
    //  This is the URL shape every icon, thumbnail and uploaded asset uses in production
    //  (MeshNodeImageHelper, MarkdownFileParser, BrandingResolver all build it), and the one
    //  that had no access control at all.
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 THE VULNERABILITY. An unauthenticated caller with nothing but the URL read any
    /// partition's uploaded file. It must now be refused.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AnonymousRequest_ForAPrivatePartitionsFile_IsDenied()
    {
        var response = await GetAsync($"/static/storage/content/{PrivateSpace}/secret.pdf");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an anonymous caller holds no grant on PrivateSpace — the URL is not the access control");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(SecretBody, "the bytes must never reach an unauthorized caller");
    }

    /// <summary>An authenticated caller without a grant is forbidden — signing in is not access.</summary>
    [Fact(Timeout = 30000)]
    public async Task SignedInCallerWithoutAGrant_IsForbidden()
    {
        var response = await GetAsync($"/static/storage/content/{PrivateSpace}/secret.pdf", asUser: Stranger);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "403 (not 401) — this caller is authenticated; signing in again would not help");
    }

    /// <summary>
    /// Anonymous access is a REAL case and must keep working: the course cover's asset carries an
    /// explicit Anonymous Viewer grant at the Space root. A gate that broke this would break every
    /// public landing page.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PublicCoverAsset_IsStillServedAnonymously()
    {
        var response = await GetAsync($"/static/storage/content/{Course}/cover.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Be(CoverBody);
    }

    /// <summary>
    /// 🚨 THE PAYWALL. The lesson's media sits one level below the anonymously-readable cover. The
    /// file is attributed to the DEEPEST node its path maps to (<c>GatedCourse/PaidLesson</c>), so
    /// the per-child deny applies — attributing it to the Space root instead would serve every paid
    /// video to the internet.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task GatedLessonAsset_IsDeniedToAnAnonymousVisitor()
    {
        var response = await GetAsync($"/static/storage/content/{PaidLesson}/lesson.mp4");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(LessonBody);
    }

    /// <summary>
    /// …and the over-strictness catcher: the BUYER's grant at the course root reaches the gated
    /// child (a deny binds only the subject it names), so the entitled caller is served.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task GatedLessonAsset_IsServedToTheEntitledBuyer()
    {
        var response = await GetAsync($"/static/storage/content/{PaidLesson}/lesson.mp4", asUser: Buyer);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Be(LessonBody);
    }

    /// <summary>
    /// An explicitly-public collection (<c>NodeTypeIcons</c> — SVGs compiled into MeshWeaver.Graph,
    /// needed on the login page before any identity exists) still serves anonymously. This is the
    /// deliberate opt-in; everything else defaults to gated.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ExplicitlyPublicCollection_IsServedAnonymously()
    {
        var response = await GetAsync("/static/NodeTypeIcons/box.svg");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.Public.Should().BeTrue(
            "only a declared-public asset class may be stored by a shared cache");
    }

    /// <summary>
    /// Access-controlled bytes must never be stored by a CDN / corporate proxy: a single authorized
    /// fetch would otherwise keep being replayed to callers this gate denies. The response is
    /// <c>private</c> and revalidates.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task GatedContent_IsNeverSharedCacheable()
    {
        var response = await GetAsync($"/static/storage/content/{Course}/cover.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cacheControl = response.Headers.CacheControl!;
        cacheControl.Private.Should().BeTrue("a shared cache must not store access-controlled content");
        cacheControl.Public.Should().BeFalse();
        cacheControl.MustRevalidate.Should().BeTrue("a revoked grant must take effect quickly");
    }

    /// <summary>
    /// A file the endpoint cannot attribute to any node (directly at the store root, below the
    /// per-node mount layout) is refused rather than served — the gate fails CLOSED.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task UnattributableFile_IsDenied()
    {
        var response = await GetAsync("/static/storage/loose.txt");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// 🚨 EXPLICIT MOUNT. <c>/static</c> publishes only collections that declare
    /// <see cref="ContentCollectionConfig.IsStatic"/>. The flag existed but was read NOWHERE, so
    /// every collection registered anywhere on the mesh hub was reachable by URL. An unmounted
    /// collection is 404 — for the admin too, because being unmounted is a hosting decision, not
    /// an access decision, and must not vary with identity.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task UnmountedCollection_IsNotServedAtAll()
    {
        var anonymous = await GetAsync($"/static/{UnmountedCollection}/content/{Course}/cover.png");
        anonymous.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a collection that is not mounted on /static is not on this route");

        var buyer = await GetAsync($"/static/{UnmountedCollection}/content/{Course}/cover.png", asUser: Buyer);
        buyer.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the mount is a hosting decision — it must not depend on who is asking");
        var body = await buyer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(CoverBody);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  Pattern 2 — /static/{address}/{collection}/{file}.
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The address-based shape resolved ANY address and served that hub's collections — a
    /// cross-partition read that never consulted the partition's policy. Anonymous is refused.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AddressPattern_AnonymousRequestForAPrivatePartition_IsDenied()
    {
        var response = await GetAsync($"/static/{PrivateSpace}/content/secret.pdf");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(SecretBody);
    }

    /// <summary>
    /// 🚨 REGRESSION GUARD for the cache short-circuit. The address pattern's ONLY brush with access
    /// control used to be the collection-config lookup (its <c>GetDataRequest</c> carries
    /// <c>[RequiresPermission(Read)]</c>) — and <c>collectionCache</c> skips that lookup on a hit.
    /// So once ANY caller warmed the cache for a collection, every later caller bypassed the check
    /// entirely. The gate now runs BEFORE the config lookup: warming the cache with an authorized
    /// request must not open the door for the next anonymous one.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AddressPattern_WarmingTheCollectionCache_DoesNotOpenTheGate()
    {
        // Warm whatever can be warmed as the entitled buyer…
        await GetAsync($"/static/{Course}/content/PaidLesson/lesson.mp4", asUser: Buyer);

        // …then the un-entitled visitor must still be refused.
        var response = await GetAsync($"/static/{Course}/content/PaidLesson/lesson.mp4");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(LessonBody);
    }

    /// <inheritdoc />
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        client?.Dispose();
        if (portal is not null)
            await portal.DisposeAsync();
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(storageRoot))
                Directory.Delete(storageRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leftover fixture directory under the test bin folder is harmless.
        }
    }
}
