using Memex.Portal.Shared.Courses;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The course-asset gate's pure logic (<see cref="CourseAssetGate"/>): the
/// <c>/assets/{Space}/{path…}</c> parse, the Subdirectory → repo-file-path mapping, and the
/// entitlement decision matrix — admins always pass, Read is the floor, a paid course
/// (any entitlement entries) additionally requires the viewer's own entitlement, and
/// denials split 401 (anonymous) vs 403 (authenticated).
/// </summary>
public class CourseAssetGateTest
{
    // ── Path parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void TryParsePath_SplitsSpaceAndRelativePath()
    {
        CourseAssetGate.TryParsePath(
                "AgenticEngineering/content/videos/Module1.mp4", out var space, out var rest)
            .Should().BeTrue();
        space.Should().Be("AgenticEngineering");
        rest.Should().Be("content/videos/Module1.mp4");
    }

    [Fact]
    public void TryParsePath_ToleratesSurroundingSlashes()
    {
        CourseAssetGate.TryParsePath("/Chess/openings.pgn", out var space, out var rest)
            .Should().BeTrue();
        space.Should().Be("Chess");
        rest.Should().Be("openings.pgn");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SpaceOnly")]                       // no file segment
    [InlineData("Space//file.mp4")]                 // empty segment
    [InlineData("Space/../secrets.txt")]            // traversal
    [InlineData("Space/videos/./file.mp4")]         // dot segment
    [InlineData("../Space/file.mp4")]               // traversal in the space slot
    [InlineData("_Entitlements/file.mp4")]          // satellite-shaped space
    [InlineData("My Space/file.mp4")]               // whitespace in the space slot (query injection)
    [InlineData("Space nodeType:User/file.mp4")]    // query-injection shape
    [InlineData("Space/my video.mp4")]              // whitespace in a file segment
    [InlineData("Space/videos/a\tb.mp4")]           // any whitespace, not just ' '
    public void TryParsePath_RejectsMalformedPaths(string? path)
    {
        CourseAssetGate.TryParsePath(path, out _, out _).Should().BeFalse();
    }

    // ── Repo-path mapping ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "videos/Module1.mp4", "videos/Module1.mp4")]
    [InlineData("", "videos/Module1.mp4", "videos/Module1.mp4")]
    [InlineData("   ", "videos/Module1.mp4", "videos/Module1.mp4")]
    [InlineData("content", "videos/Module1.mp4", "content/videos/Module1.mp4")]
    [InlineData("/content/", "videos/Module1.mp4", "content/videos/Module1.mp4")]
    [InlineData("courses/advanced", "intro.mp4", "courses/advanced/intro.mp4")]
    public void MapToRepoPath_PrefixesTheConfiguredSubdirectory(
        string? subdirectory, string relativePath, string expected)
    {
        CourseAssetGate.MapToRepoPath(subdirectory, relativePath).Should().Be(expected);
    }

    // ── Entitlement decision matrix ──────────────────────────────────────────

    [Fact]
    public void Decide_CourseAdmins_AlwaysPass()
    {
        // Update on the Space wins regardless of paid-ness or entitlement — even
        // without an explicit Read flag.
        CourseAssetGate.Decide(Permission.Update, isAuthenticated: true, isPaid: true, isEntitled: false)
            .Should().Be(CourseAssetGate.Decision.Allowed);
        CourseAssetGate.Decide(Permission.Read | Permission.Update, true, true, false)
            .Should().Be(CourseAssetGate.Decision.Allowed);
    }

    [Fact]
    public void Decide_FreeCourse_ReadSuffices()
    {
        CourseAssetGate.Decide(Permission.Read, isAuthenticated: true, isPaid: false, isEntitled: false)
            .Should().Be(CourseAssetGate.Decision.Allowed);
        // Public free course: anonymous with a Read grant passes too.
        CourseAssetGate.Decide(Permission.Read, isAuthenticated: false, isPaid: false, isEntitled: false)
            .Should().Be(CourseAssetGate.Decision.Allowed);
    }

    [Fact]
    public void Decide_PaidCourse_RequiresEntitlement()
    {
        CourseAssetGate.Decide(Permission.Read, isAuthenticated: true, isPaid: true, isEntitled: true)
            .Should().Be(CourseAssetGate.Decision.Allowed);
        CourseAssetGate.Decide(Permission.Read, isAuthenticated: true, isPaid: true, isEntitled: false)
            .Should().Be(CourseAssetGate.Decision.Forbidden);
        // Anonymous on a paid course → 401 (log in), not 403.
        CourseAssetGate.Decide(Permission.Read, isAuthenticated: false, isPaid: true, isEntitled: false)
            .Should().Be(CourseAssetGate.Decision.NotAuthenticated);
    }

    [Fact]
    public void Decide_WithoutRead_DeniesByAuthentication()
    {
        CourseAssetGate.Decide(Permission.None, isAuthenticated: false, isPaid: false, isEntitled: false)
            .Should().Be(CourseAssetGate.Decision.NotAuthenticated);
        CourseAssetGate.Decide(Permission.None, isAuthenticated: true, isPaid: false, isEntitled: false)
            .Should().Be(CourseAssetGate.Decision.Forbidden);
        // Non-Read flags alone (e.g. Comment) don't open the gate.
        CourseAssetGate.Decide(Permission.Comment, isAuthenticated: true, isPaid: false, isEntitled: false)
            .Should().Be(CourseAssetGate.Decision.Forbidden);
    }

    [Fact]
    public void Decide_EntitlementWithoutRead_StaysDenied()
    {
        // An entitlement never substitutes for Read on the Space itself.
        CourseAssetGate.Decide(Permission.None, isAuthenticated: true, isPaid: true, isEntitled: true)
            .Should().Be(CourseAssetGate.Decision.Forbidden);
    }
}
