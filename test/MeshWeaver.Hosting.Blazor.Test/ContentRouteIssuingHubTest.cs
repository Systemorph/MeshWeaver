using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
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
/// 🚨 THE <c>/api/content</c> ROUTE MUST NOT ISSUE ITS COLLECTION-CONFIG READ FROM THE ROUTER —
/// issue #1729, driven over REAL HTTP against a REAL monolith mesh.
///
/// <para><b>What broke.</b> <c>MapContentFiles</c> resolves <c>services.GetRequiredService&lt;
/// IMessageHub&gt;()</c>, which in the mesh's root container IS the root <c>mesh/{id}</c> hub — the
/// ROUTER — and handed it to <see cref="ContentFileResolver.Resolve"/>, which posted the owning
/// node's <c>GetDataRequest</c> from it. That makes the router an END of the delivery in BOTH
/// directions: the request reaches the per-node hub stamped <c>Sender = mesh/{id}</c> and the
/// <c>GetDataResponse</c> is addressed straight back at <c>mesh/{id}</c>.</para>
///
/// <para><b>Why that is fatal in production and invisible in a monolith.</b> Same-silo the reply
/// short-circuits on the routing service's local table, so one process looks perfectly healthy —
/// which is exactly why every existing content-route test (all monolith, all single-process)
/// stayed green. CROSS-silo the reply has to arrive over the cluster-wide memory stream, and it
/// does not: the request never answers, the caller waits out its full 60 s budget and the route
/// 500s with a <c>TimeoutException</c>. On memex-cloud (2 replicas) each pod therefore served
/// <c>/api/content</c> ONLY for the nodes whose per-node hub grain it happened to host and hung on
/// every other node, so round-robin made ~half of all requests to ANY given asset hang — broken
/// course/doc images for real users, and a red live-smoke gate on MeshWeaver.Education.</para>
///
/// <para><b>Why this test asserts the SENDER rather than reproducing the hang.</b> The hang needs
/// two silos in two PROCESSES. Orleans' in-process <c>TestCluster</c> does not reproduce it — a
/// two-silo variant of this test passes with and without the fix, which makes it worse than no
/// test at all. The sender is the wire-observable property that DECIDES whether the reply is
/// routable at all, and "the router is never an end of a delivery" is the standing rule the
/// framework already enforces everywhere else (<c>ROUTER_TRAFFIC</c>,
/// <see cref="MeshExtensions.NodeOperationIssuingHub"/>, <c>SessionHubResolver</c>). Pinning it
/// here is deterministic, single-process, and fails loudly the moment the route regresses.</para>
/// </summary>
public class ContentRouteIssuingHubTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "IssuerSpace";
    private const string CoverFile = "cover.txt";
    private const string CoverBody = "cover-bytes";

    private readonly string storageRoot = CreateStorageFixture();

    /// <summary>
    /// The sender of the collection-config <c>GetDataRequest</c> as the OWNING NODE HUB saw it —
    /// i.e. what a remote silo would have to address its reply to. Instance state, written on the
    /// node hub's action block and read after the HTTP round trip has completed, so no gate is
    /// needed: the response cannot exist unless the handler already ran.
    /// </summary>
    private Address? collectionConfigSender;

    private WebApplication? portal;
    private HttpClient client = null!;

    private static string CreateStorageFixture()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "content-issuer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "content", Space));
        File.WriteAllText(Path.Combine(root, "content", Space, CoverFile), CoverBody);
        return root;
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Space) { Name = "Issuer Space", NodeType = "Markdown" },
                // An explicit Anonymous Viewer grant, so the route runs its FULL happy path and the
                // assertion below is made about a request that genuinely served bytes — not about a
                // pipeline that stopped at the access gate.
                AssignmentNodeFactory.UserRole(
                    WellKnownUsers.Anonymous, "Viewer", Space,
                    accessObject: WellKnownUsers.Anonymous))
            .ConfigureDefaultNodeHub(config =>
                config.Address.ToString() == Space
                    // 🚨 The capture handler is registered FIRST and passes the delivery through
                    // unchanged, so the real HandleCollectionConfigRequest still answers it. It only
                    // observes; it never decides.
                    ? config
                        .WithHandler<GetDataRequest>((_, delivery) =>
                        {
                            if (delivery.Message.Reference is ContentCollectionReference)
                                collectionConfigSender = delivery.Sender;
                            return delivery;
                        })
                        .AddContentCollection(_ => new ContentCollectionConfig
                        {
                            Name = ContentCollectionsExtensions.DefaultCollectionName,
                            SourceType = "FileSystem",
                            BasePath = Path.Combine(storageRoot, "content", Space),
                            Address = config.Address,
                            IsEditable = true,
                            ExposeInChildren = true,
                            IsStatic = true,
                        })
                    : config);

    // Granular permissions only — the blanket PublicAdmin seed would make the Anonymous grant moot.
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        portal = BuildPortal();
        await portal.StartAsync(TestContext.Current.CancellationToken);
        client = portal.GetTestClient();
    }

    /// <summary>The REAL <c>MapMeshWeaver()</c> endpoints, behind the anonymous-stamping middleware
    /// production runs (the never-null <c>AccessContext</c> invariant).</summary>
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
            accessService.SetContext(new AccessContext
            {
                ObjectId = WellKnownUsers.Anonymous,
                Name = WellKnownUsers.Anonymous
            });
            await next();
        });
        app.MapMeshWeaver();
        return app;
    }

    /// <summary>
    /// 🚨 THE REGRESSION TEST FOR #1729. Serving a content file must not make the ROUTER an end of
    /// the delivery: the owning node hub must see the request from an off-router hub
    /// (<c>portal/nodeops-{meshId}</c>), which is routing-registered and can therefore be replied
    /// to from another silo.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ContentRead_IsNotIssuedFromTheRootMeshHub()
    {
        var response = await client.GetAsync(
            $"{ContentCollectionsExtensions.ContentFileRoutePrefix}/{Space}/{CoverFile}",
            TestContext.Current.CancellationToken);

        // Sanity: the happy path really ran, so the assertion below is about a live request.
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the partition carries an explicit Anonymous Viewer grant, so the file is served");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Be(CoverBody);

        collectionConfigSender.Should().NotBeNull(
            "the route must ask the owning node hub for its collection config — if nothing was "
            + "observed the request never reached the node and this test is no longer guarding "
            + "anything");

        collectionConfigSender!.Type.Should().NotBe(AddressExtensions.MeshType,
            "issue #1729: the collection-config GetDataRequest must NOT be issued on the root mesh "
            + "hub. The router is the mesh's ROUTING infrastructure, not a call target — a request "
            + "posted there is answered straight back at it, and that reply is unroutable from any "
            + "OTHER silo, so on a multi-replica portal the request never answers and /api/content "
            + "hangs for 60 s. ContentFileResolver.Resolve must issue on NodeOperationIssuingHub()");
    }
}
