using System;
using System.Diagnostics;
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
/// 🚨 <c>/api/content</c> MUST DEGRADE FAST AND TRUTHFULLY WHEN THE OWNING HUB CANNOT ANSWER —
/// issues #1563 (the hub was unreachable) and #1693 (the hub threw during activation), driven over
/// REAL HTTP against a REAL monolith mesh.
///
/// <para><b>What was broken, and it is one shape wearing three faces.</b> The route's
/// collection-config read carried no budget of its own, so its only terminal was the hub's
/// <c>RequestTimeout</c> — 60 s, the framework's last-resort ceiling. And the failures that DID
/// come back fast were unclassified, so they landed on the route's fallback arm: HTTP 500 with a
/// <c>fail:</c>-level log. Every one of the three cases below therefore ended as an alert that told
/// an operator nothing and told the browser to give up:</para>
/// <list type="bullet">
///   <item><b>The hub throws while activating</b> — <c>"Hub activation failed for
///     AdvancedBusinessRules: Object reference not set to an instance of an object."</c> (#1693).
///     An availability fact about the target, reported as a defect in the route.</item>
///   <item><b>The hub is still starting</b> and its init gates have not opened, so the delivery is
///     parked and nobody answers for a minute (#1563 / #1748).</item>
///   <item><b>Nothing resolves at that path</b> — which must stay a plain, immediate 404.</item>
/// </list>
///
/// <para><b>What must hold now.</b> A degraded read answers <b>503</b> — retryable, because no
/// verdict was reached and the same request will succeed once the target is back — and it does so
/// in SECONDS, not in the hub's minute. A genuinely absent path stays 404. And the healthy path is
/// untouched, which is the control that stops "make it fail fast" from becoming "make it fail".</para>
///
/// <para><b>Why the elapsed-time assertions are the point.</b> Status alone would pass against the
/// old code plus a classification fix, while a real user still waited 60 s for a broken image. The
/// bound asserted here is deliberately loose — an order of magnitude below the 60 s budget rather
/// than a hair above the 10 s one — so it measures the fix without becoming a machine-speed
/// flake.</para>
/// </summary>
public class ContentRouteUnreachableHubTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A node whose hub builds and serves normally — the control.</summary>
    private const string HealthySpace = "HealthySpace";

    /// <summary>A node whose hub CONFIGURATION throws — the #1693 shape, reproduced exactly.</summary>
    private const string BrokenSpace = "BrokenSpace";

    /// <summary>A node whose hub comes up but never opens an init gate — the "still Starting" shape.</summary>
    private const string StartingSpace = "StartingSpace";

    private const string CoverFile = "cover.txt";
    private const string CoverBody = "cover-bytes";

    /// <summary>Header the test pipeline reads to stamp the request's identity (see BuildPortal).</summary>
    private const string UserHeader = "X-Test-User";

    /// <summary>
    /// The wall-clock ceiling every assertion below uses. The bug produced 60 s; the route's own
    /// budget is <see cref="ReadBudget.Default"/> (10 s). 25 s is comfortably above the budget plus
    /// a cold monolith's activation and comfortably below the failure — it proves the fix without
    /// pinning a machine-speed race.
    /// </summary>
    private static readonly TimeSpan WellUnderTheOldBudget = TimeSpan.FromSeconds(25);

    /// <summary>
    /// The ceiling for a failure the mesh reports IMMEDIATELY (a hub that could not be constructed,
    /// a path that resolves to nothing). These must not even reach the read budget — if one does,
    /// the classification regressed and the route is waiting where it should be answering.
    /// </summary>
    private static readonly TimeSpan Immediately = TimeSpan.FromSeconds(8);

    private readonly string storageRoot = CreateStorageFixture();

    private WebApplication? portal;
    private HttpClient client = null!;

    private static string CreateStorageFixture()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "unreachable-hub-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "content", HealthySpace));
        File.WriteAllText(Path.Combine(root, "content", HealthySpace, CoverFile), CoverBody);
        Directory.CreateDirectory(Path.Combine(root, "content", BrokenSpace));
        File.WriteAllText(Path.Combine(root, "content", BrokenSpace, CoverFile), CoverBody);
        Directory.CreateDirectory(Path.Combine(root, "content", StartingSpace));
        File.WriteAllText(Path.Combine(root, "content", StartingSpace, CoverFile), CoverBody);
        return root;
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(HealthySpace) { Name = "Healthy Space", NodeType = "Markdown" },
                new MeshNode(BrokenSpace) { Name = "Broken Space", NodeType = "Markdown" },
                new MeshNode(StartingSpace) { Name = "Starting Space", NodeType = "Markdown" })
            .ConfigureDefaultNodeHub(config =>
            {
                var address = config.Address.ToString();

                // 🚨 THE #1693 REPRODUCTION, at the layer where it actually happened: a
                // NullReferenceException thrown while the per-node hub's configuration is composed.
                // MessageHubConfiguration.Build runs every SyncBuildupAction inline, so a compiled
                // NodeType's own configuration lambda, the workspace construction AddData registers,
                // and every ConfigureDefaultNodeHub overlay all execute inside hub construction —
                // which is why a single null dereference anywhere in that chain becomes "hub
                // activation failed" and nothing more specific.
                if (address == BrokenSpace)
                    throw new NullReferenceException("Object reference not set to an instance of an object.");

                // A hub that BUILDS but never becomes ready: an initialization gate nothing ever
                // opens, so every delivery to it is deferred. This is the "target hub is still
                // Starting" case — the one where no failure is ever produced, so the CALLER'S budget
                // is the only thing that can end the wait.
                if (address == StartingSpace)
                    return config.WithInitializationGate("NeverOpens");

                return address == HealthySpace
                    ? config.AddContentCollection(_ => new ContentCollectionConfig
                    {
                        Name = ContentCollectionsExtensions.DefaultCollectionName,
                        SourceType = "FileSystem",
                        BasePath = Path.Combine(storageRoot, "content", HealthySpace),
                        Address = config.Address,
                        ExposeInChildren = true,
                        IsStatic = true,
                    })
                    : config;
            });

    /// <inheritdoc />
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        portal = BuildPortal();
        await portal.StartAsync(TestContext.Current.CancellationToken);
        client = portal.GetTestClient();
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (portal is not null)
            await portal.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>
    /// The portal's HTTP surface: the REAL <c>MapMeshWeaver()</c> endpoints over the test mesh,
    /// preceded by the identity middleware production's <c>UserContextMiddleware</c> provides.
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

    private async Task<(HttpStatusCode Status, TimeSpan Elapsed)> GetAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(UserHeader, TestUsers.Admin.ObjectId);
        var started = Stopwatch.StartNew();
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var elapsed = started.Elapsed;
        Output.WriteLine($"GET {url} → {(int)response.StatusCode} in {elapsed.TotalSeconds:F2}s");
        return (response.StatusCode, elapsed);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  The control — nothing below is worth anything if the healthy path regressed.
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A reachable hub still serves its bytes, promptly. A read budget that also breaks the working
    /// case is not a fix, and "fails fast" is trivially satisfiable by failing always.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AReachableHub_StillServesItsContent()
    {
        var (status, elapsed) = await GetAsync($"/api/content/{HealthySpace}/{CoverFile}");

        status.Should().Be(HttpStatusCode.OK);
        elapsed.Should().BeLessThan(Immediately);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  #1693 — the hub throws while activating.
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 A hub that throws while activating is an AVAILABILITY failure of the TARGET, not a defect
    /// in this route: the node exists, its hub blew up, and the next access re-runs construction
    /// from scratch. So the honest answer is a retryable 503 — never the 500 + <c>fail:</c> alert
    /// that filed #1693, and never a 404, which would tell a caller (and any cache between) that
    /// content which demonstrably exists does not.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AHubThatThrowsWhileActivating_Answers503_NotAFailLevel500()
    {
        var (status, elapsed) = await GetAsync($"/api/content/{BrokenSpace}/{CoverFile}");

        status.Should().Be(HttpStatusCode.ServiceUnavailable,
            "the node is there and its hub could not be built — no verdict was reached, so the "
            + "answer is 'retry', not 'this is broken' and not 'this does not exist'");
        elapsed.Should().BeLessThan(Immediately,
            "hub construction fails synchronously and the mesh reports it at once — this case must "
            + "not even reach the read budget, let alone the hub's 60 s RequestTimeout");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  #1563 / #1748 — the hub is still starting and nobody ever answers.
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 THE ORIGINAL BUG. The target hub is up but not ready — its init gates have not opened — so
    /// the delivery is parked and NO failure is ever produced. Nothing but the caller's own budget
    /// can end that wait, and without one the route sat for the hub's full 60 s and then answered
    /// 500 with <c>"No response received in hub … the target hub was not found"</c>.
    ///
    /// <para>The elapsed bound is the assertion that matters here: the status could be made right by
    /// classification alone while a user still stared at a broken image for a minute.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AHubThatNeverBecomesReady_Answers503_WellInsideTheOldSixtySecondBudget()
    {
        var (status, elapsed) = await GetAsync($"/api/content/{StartingSpace}/{CoverFile}");

        status.Should().Be(HttpStatusCode.ServiceUnavailable,
            "the read reached no verdict — 503 tells the caller to retry, which is exactly what a "
            + "still-starting hub deserves");
        elapsed.Should().BeLessThan(WellUnderTheOldBudget,
            "the whole point: the route now carries its own 10 s read budget instead of inheriting "
            + "the hub's 60 s RequestTimeout as its only terminal");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  Nothing there at all.
    // ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A path that resolves to no node is answered immediately, and as a plain 404 — the same body a
    /// refusal produces, so no arm of the failure mapping can be used as an existence oracle over a
    /// fully predictable URL scheme. Pinned alongside the degraded cases because "unreachable" and
    /// "absent" are the two facts this change exists to keep apart.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task APathThatResolvesToNoNode_Is404_Immediately()
    {
        var (status, elapsed) = await GetAsync($"/api/content/NoSuchSpaceAnywhere/{CoverFile}");

        status.Should().Be(HttpStatusCode.NotFound);
        elapsed.Should().BeLessThan(Immediately,
            "absence is known without asking any hub — it must never wait on a budget");
    }
}
