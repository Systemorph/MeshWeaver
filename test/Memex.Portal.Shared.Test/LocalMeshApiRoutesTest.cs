using Memex.LocalMesh;
using Memex.Portal.Shared.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The headless sidecar (<c>Memex.LocalMesh</c>) serves the SAME JS shells the portal does — the
/// React Native app switches between them by instance URL — so it must map the same
/// <c>/api/mesh/*</c> verbs those shells call.
///
/// <para>🚨 A missing route here is invisible from the client. <c>Memex.LocalMesh</c> ends with
/// <c>MapFallbackToFile("index.html")</c>, so an unmapped <c>/api/mesh/*</c> POST does not 404 — it
/// answers the SPA's HTML with a <b>200</b>, and the caller fails inside <c>JSON.parse</c> with
/// nothing naming the missing endpoint. That is issue #1474: only <c>render-markdown</c> was mapped,
/// so the file browser was broken against the sidecar with no diagnosable symptom.</para>
///
/// <para>The route table is asserted directly (no mesh, no SQLite, no gRPC) because the defect IS
/// the absent route. <c>clients/grpc-web/src/restContract.test.ts</c> holds the other end: every verb
/// the client SDK posts to must appear in BOTH backends' endpoint maps.</para>
/// </summary>
public class LocalMeshApiRoutesTest
{
    /// <summary>The verbs a JS shell reaches over HTTP because the gRPC bus does not carry them.</summary>
    public static TheoryData<string> MeshRestVerbs =>
    [
        "/api/mesh/render-markdown",
        "/api/mesh/query-nodes",
        "/api/mesh/content/list",
        "/api/mesh/upload",
    ];

    [Theory]
    [MemberData(nameof(MeshRestVerbs))]
    public void LocalMeshSidecar_MapsTheVerbTheShellsCall(string route)
    {
        MappedRoutes().Should().Contain(route,
            "the local sidecar serves the same shells the portal does, and an unmapped /api/mesh/* " +
            "route falls through to the SPA fallback with a 200 instead of failing visibly");
    }

    [Fact]
    public void TheRouteScrape_FindsEndpoints()
    {
        // Guard the guard: if MapLocalMeshApi stopped registering anything (or the inspection broke),
        // every assertion above would fail loudly rather than a single one passing vacuously.
        MappedRoutes().Should().HaveCountGreaterThan(3);
    }

    [Fact]
    public void TheSidecarVerbs_AreAllOnThePortalToo()
    {
        // Both hosts speak the same prefix, so a shell cannot need a different URL per backend.
        MappedRoutes().Should().OnlyContain(r => r.StartsWith(MeshApiEndpoints.RoutePrefix + "/", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> MappedRoutes()
    {
        var app = WebApplication.CreateBuilder().Build();
        app.MapLocalMeshApi();
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))
            .ToList();
    }
}
