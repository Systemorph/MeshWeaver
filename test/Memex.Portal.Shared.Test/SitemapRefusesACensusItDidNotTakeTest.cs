using System.Reactive.Linq;
using Memex.Portal.Shared.Api;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The rule of <see cref="PublishedSurface.AssertsWhatItDidNotCheck"/>, on its own — pure, so the
/// one line that decides between "200 with this list" and "503" is pinned without a mesh.
///
/// <para>The two middle rows are the whole point. A PARTIAL surface stays a 200 (a sitemap is a
/// hint and losing one root costs one URL), and an EMPTY surface stays a 200 when the emptiness
/// was established (a portal that publishes nothing is entitled to say so). Only the fourth
/// combination — nothing found, and something undecided — is withheld, because zero is the only
/// value a reader takes as a statement.</para>
/// </summary>
public class PublishedSurfaceRuleTest
{
    private static PublishedPage APage =>
        new(new MeshNode("Guide", "PublicSpace") { Name = "Guide", NodeType = "Markdown" },
            "PublicSpace/Guide");

    [Fact]
    public void PagesAndDecided_IsACensus() =>
        Assert.False(PublishedSurface.Decided([APage]).AssertsWhatItDidNotCheck);

    [Fact]
    public void PagesButUndecided_StillPublishes_BecausePartialIsWithinContract() =>
        Assert.False(new PublishedSurface([APage], "one root reached no verdict").AssertsWhatItDidNotCheck);

    [Fact]
    public void EmptyAndDecided_StillPublishes_BecauseTheEmptinessWasEstablished() =>
        Assert.False(PublishedSurface.Decided([]).AssertsWhatItDidNotCheck);

    [Fact]
    public void EmptyAndUndecided_IsTheOneCaseWithheld() =>
        Assert.True(new PublishedSurface([], "every root reached no verdict").AssertsWhatItDidNotCheck);
}

/// <summary>
/// 🚨 Pins MeshWeaver#4751: when the anonymous gate reaches NO VERDICT on every candidate root,
/// the sitemap must refuse rather than serve a well-formed <c>urlset</c> declaring zero public
/// roots.
///
/// <para><b>What was wrong.</b> <c>SeoEndpoints</c> projected the gate's TRI-state through
/// <see cref="AnonymousGate.AllowAnonymous"/>, whose own documentation permits that only "where
/// 'unknown' and 'not public' lead to the SAME correct action and nothing is asserted to a human".
/// Per PAGE that holds — <c>PagesOf</c> still uses the bool, and always emits its root regardless,
/// so it can only ever cost URLs. Per ROOT it does not, because the roots ARE the document: omit
/// every one and what comes out is a 200 that says this deployment publishes nothing. Nine
/// occurrences over 27 hours on memex.meshweaver.cloud, and the two <c>.Catch(_ =&gt; [])</c> sinks
/// beneath discarded their exception, so not one of them left a line naming a cause.</para>
///
/// <para><b>The rig.</b> A genuinely public root — the same seeding as
/// <see cref="SitemapRootWindowTest"/>, so the DECIDED reading of this mesh publishes two URLs —
/// plus a mesh-hub permission evaluator that COMPLETES WITHOUT EMITTING for the anonymous subject.
/// That is a legal <see cref="EffectivePermissionsDelegate"/> and exactly the shape #2742 named;
/// <c>CheckPermissionOutcome</c> classifies it <see cref="PermissionCheckOutcome.IsUndetermined"/>,
/// which is what a degraded permission fold produces in production. Only the MESH hub's evaluator
/// is replaced, so seeding and every node read behave normally.</para>
///
/// <para><b>Falsification.</b> Run against the pre-fix enumeration this fails on both assertions:
/// the surface reads empty with nothing to say about why, and <c>BuildSitemap</c> returns a
/// well-formed empty <c>urlset</c> instead of faulting.</para>
/// </summary>
public class SitemapUndecidedGateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            // AFTER AddRowLevelSecurity, and on the MESH hub alone: WithPermissionEvaluator is a
            // Set, so this wins for the hub SeoEndpoints asks, and nothing else changes. The
            // evaluator RLS just installed is wrapped rather than replaced — only the anonymous
            // fold is degraded, so seeding and every other check behave exactly as they would.
            .ConfigureHub(c =>
            {
                var configured = c.Get<EffectivePermissionsDelegate>()
                    ?? throw new InvalidOperationException(
                        "AddRowLevelSecurity did not install an evaluator on the mesh hub, so this "
                        + "rig would degrade every check rather than the anonymous one — which "
                        + "would make the test pass for a reason it is not about.");
                return c.WithPermissionEvaluator((hub, path, userId) =>
                    userId == WellKnownUsers.Anonymous
                        ? Observable.Empty<Permission>()
                        : configured(hub, path, userId));
            })
            .AddMeshNodes(Nodes().ToArray());

    private static IEnumerable<MeshNode> Nodes()
    {
        yield return new MeshNode("PublicSpace") { Name = "Public Space", NodeType = "Space" };
        yield return AssignmentNodeFactory.UserRole(
            WellKnownUsers.Anonymous, "Viewer", "PublicSpace",
            accessObject: WellKnownUsers.Anonymous);
        yield return new MeshNode("Guide", "PublicSpace") { Name = "Guide", NodeType = "Markdown" };
    }

    // Granular permissions — no blanket admin seed, so the anonymous subject holds exactly what
    // the nodes above grant, and the evaluator above decides what it can say about them.
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact]
    public async Task AnUndecidedGate_CarriesItsReasonOutInsteadOfLookingLikeAnEmptyPortal()
    {
        var surface = await SeoEndpoints.EnumeratePublished(Mesh)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the published surface is enumerated", TestContext.Current.CancellationToken);

        Assert.Empty(surface.Pages);
        Assert.NotNull(surface.Undecided);
        // It names the root it could not decide, so the 503's log line points somewhere.
        Assert.Contains("PublicSpace", surface.Undecided);
        Assert.True(surface.AssertsWhatItDidNotCheck);
    }

    [Fact]
    public async Task TheSitemap_FaultsRatherThanRenderAZeroRootUrlset()
    {
        // ObserveCompletion, not a direct `await` on the observable (#4754) and not Emit:
        // Emit folds a source fault into ObservableAssertionException, which would turn an
        // assertion about WHICH exception into one about a message substring.
        var thrown = await Assert.ThrowsAsync<SitemapUndecidedException>(
            () => SeoEndpoints.BuildSitemap(Mesh, "https://www.example.test")
                .Timeout(TestTimeouts.Convergence)
                .ObserveCompletion(
                    ex => Output.WriteLine($"sitemap faulted after the assertion settled: {ex}"),
                    TestContext.Current.CancellationToken));

        Assert.Contains("PublicSpace", thrown.Reason);
    }

    /// <summary>
    /// 🚨 The crawler-facing half, driven through the route's OWN decision rather than a
    /// re-implementation beside it: <b>503</b> and a <c>Retry-After</c>. Both are the contract —
    /// a 503 without the header tells a crawler nothing about when to come back, and the whole
    /// argument for refusing here rather than serving an empty urlset is that 503 + Retry-After is
    /// the wire's way of saying "ask again later". Without this test the status could regress to a
    /// 200 or a 500, or the header could vanish, with every other assertion still green.
    /// </summary>
    [Fact]
    public async Task TheRoute_Answers503WithARetryAfter_NotAnEmptySitemap()
    {
        var http = new DefaultHttpContext();

        var result = await SeoEndpoints.SitemapResult(Mesh, http, "https://www.example.test")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the route decides", TestContext.Current.CancellationToken);

        var status = Assert.IsType<StatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
        Assert.Equal("300", http.Response.Headers.RetryAfter.ToString());
    }
}

/// <summary>
/// 🚨 The OTHER side of the same change, and the reason the rule above is narrow: a portal that
/// genuinely publishes nothing must still answer with an empty sitemap, not 503.
///
/// <para>Without this, "never serve a zero-root sitemap" would read as "always 503 when empty",
/// which turns every private deployment into a permanent outage on that route and would make the
/// synthetic probe that found #4751 unable to fail for the real reason. Here the gate DECIDES —
/// the ordinary evaluator, no anonymous grant anywhere — so the emptiness is a census and is
/// published as one.</para>
/// </summary>
public class SitemapDecidedEmptyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                new MeshNode("PrivateSpace") { Name = "Private Space", NodeType = "Space" },
                new MeshNode("Page", "PrivateSpace") { Name = "Page", NodeType = "Markdown" });

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact]
    public async Task NothingPublic_IsACensusOfZero_AndIsPublishedAsOne()
    {
        var surface = await SeoEndpoints.EnumeratePublished(Mesh)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the published surface is enumerated", TestContext.Current.CancellationToken);

        Assert.Empty(surface.Pages);
        Assert.Null(surface.Undecided);
        Assert.False(surface.AssertsWhatItDidNotCheck);

        var xml = await SeoEndpoints.BuildSitemap(Mesh, "https://www.example.test")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a decided empty surface still renders a sitemap", TestContext.Current.CancellationToken);

        Assert.Contains("urlset", xml);
        Assert.DoesNotContain("<loc>", xml);
    }

    /// <summary>
    /// The endpoint-level positive control for
    /// <see cref="SitemapUndecidedGateTest.TheRoute_Answers503WithARetryAfter_NotAnEmptySitemap"/>:
    /// the same route, the same empty page list, and a 200 — because here the emptiness was
    /// decided. Paired deliberately, so "never serve a zero-root sitemap" cannot quietly become
    /// "always 503 when empty" without one of the two going red.
    /// </summary>
    [Fact]
    public async Task TheRoute_Serves200WithAnEmptyUrlset_WhenTheEmptinessWasDecided()
    {
        var http = new DefaultHttpContext();

        var result = await SeoEndpoints.SitemapResult(Mesh, http, "https://www.example.test")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the route decides", TestContext.Current.CancellationToken);

        var content = Assert.IsType<ContentHttpResult>(result);
        Assert.Equal("application/xml", content.ContentType);
        Assert.Contains("urlset", content.ResponseContent!);
        Assert.DoesNotContain("<loc>", content.ResponseContent!);
        // No Retry-After: nothing here is temporary.
        Assert.True(StringValues.IsNullOrEmpty(http.Response.Headers.RetryAfter));
    }
}
