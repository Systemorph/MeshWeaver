using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AccessControl.Test;

/// <summary>
/// Regression tests for issue #434 — the Access &amp; Control tab's per-assignment subject
/// thumbnails must treat a permission-denied subject read as a BENIGN fallback, not an error
/// toast.
///
/// <para>Each <c>AccessAssignment</c> row renders a <see cref="MeshNodeThumbnailControl"/> bound
/// to the SUBJECT's node path. The subject frequently lives in a partition the viewer is not a
/// member of (two users can share access to a node without being able to read each other's user
/// partitions), so the RLS gate denies the read BY DESIGN. Before the fix,
/// <c>MeshNodeThumbnailView.BindData</c> forwarded that denial straight to <c>SurfaceError</c> →
/// a log line + a <c>PortalErrorSink.Report</c> modal + a <c>StateHasChanged</c>, once per row
/// per recompose — the repeated toasts + flicker in the issue. The fix routes the onError through
/// <see cref="MeshNodeThumbnailControl.ShouldSurfaceStreamError"/>: an access-denied read renders
/// the seeded initials-avatar fallback and logs at Debug, and only a genuine infra fault surfaces.</para>
/// </summary>
public class AccessThumbnailDeniedFallbackTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                new MeshNode("PeerHome") { Name = "Peer Home" },
                new MeshNode("PeerHome/Subject") { Name = "Peer Subject" })
            .ConfigureDefaultNodeHub(c => c.AddData(d => d));

    private async Task EnsureHubStarted(Address address)
    {
        var client = GetClient();
        await client.Observe(new PingRequest(), o => o.WithTarget(address)).Should().Emit();
    }

    /// <summary>
    /// Integration (RLS, real denial): a non-member viewer subscribes to the subject's node stream —
    /// exactly what <c>MeshNodeThumbnailView.BindData</c> does — and the read is denied. The fix
    /// classifies that real <see cref="UnauthorizedAccessException"/> as a benign, expected fallback:
    /// <see cref="MeshNodeThumbnailControl.ShouldSurfaceStreamError"/> is <c>false</c>, so NO error
    /// toast / SurfaceError fires. Revert-proven: on current main BindData surfaces the denial
    /// unconditionally, and the <c>ShouldSurfaceStreamError</c> seam does not exist.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DeniedSubjectRead_IsClassifiedBenign_NoToast()
    {
        // Admin seeds the subject; then switch to a viewer who is NOT a member of its partition.
        await EnsureHubStarted(new Address("PeerHome/Subject"));
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "viewer-bob", Name = "Bob" });

        Exception? surfaced = null;
        MeshNode? emitted = null;
        try
        {
            emitted = await Mesh.GetMeshNodeStream("PeerHome/Subject")
                .Where(n => n is not null)
                .FirstAsync()
                .Timeout(10.Seconds())
                .ToTask();
        }
        catch (Exception ex)
        {
            surfaced = ex;
            Output.WriteLine($"Surfaced: {ex.GetType().Name}: {ex.Message}");
        }

        emitted.Should().BeNull("a denied subject read must not emit a node value");
        surfaced.Should().NotBeNull(
            "reading a subject in a partition the viewer isn't a member of is denied by design — "
            + "this is the toast source in #434");
        surfaced.Should().NotBeOfType<TimeoutException>(
            "the denial must surface as an access error, not hang to a timeout");

        // The #434 root-cause fix — a denied subject read is BENIGN, never an error toast.
        MeshNodeThumbnailControl.ShouldSurfaceStreamError(surfaced).Should().BeFalse(
            "an access-denied subject read is an expected user-action failure — the thumbnail renders "
            + "its seeded fallback card, so BindData must NOT call SurfaceError (no log/PortalErrorSink/StateHasChanged)");
        AreaErrorClassifier.IsExpectedUserActionFailure(surfaced).Should().BeTrue(
            "the real RLS denial the mesh throws must classify as a benign, expected user-action failure");
    }

    /// <summary>
    /// Pure boundary test of the decision seam: access-denied is swallowed (benign fallback),
    /// a genuine infrastructure fault surfaces once.
    /// </summary>
    [Fact]
    public void ShouldSurfaceStreamError_SwallowsAccessDenied_SurfacesInfraFaults()
    {
        MeshNodeThumbnailControl.ShouldSurfaceStreamError(
                new UnauthorizedAccessException("User 'bob' lacks Read permission on 'PeerHome/Subject'"))
            .Should().BeFalse("access-denied is denied-by-design — render the fallback, no toast");

        MeshNodeThumbnailControl.ShouldSurfaceStreamError(new InvalidOperationException("boom"))
            .Should().BeTrue("a genuine infrastructure fault must surface once");
    }
}
