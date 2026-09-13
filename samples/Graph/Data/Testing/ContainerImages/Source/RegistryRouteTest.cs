// <meshweaver>
// Id: Testing/ContainerImages/RegistryRouteTest
// DisplayName: Testing/ContainerImages/RegistryRouteTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using MeshWeaver.ContainerImages;

/// <summary>
/// The route parser is the mirror's attack surface: it decides which repository name the
/// allowlist is checked against, so every way of making those two disagree is a way past the
/// allowlist.
/// </summary>
public class RegistryRouteTest
{
    [MeshTheory]
    [MeshInlineData("memex-portal-ai/manifests/latest", "memex-portal-ai", "manifests", "latest")]
    [MeshInlineData("memex-portal-ai/blobs/sha256:abc", "memex-portal-ai", "blobs", "sha256:abc")]
    [MeshInlineData("memex-portal-ai/tags/list", "memex-portal-ai", "tags", "list")]
    // The spec allows slashes in a repository name, and the kind/reference are the LAST two
    // segments — so this must be repository "team/service", not "team".
    [MeshInlineData("team/service/manifests/v1", "team/service", "manifests", "v1")]
    [MeshInlineData("a/b/c/blobs/sha256:d", "a/b/c", "blobs", "sha256:d")]
    public void ParsesAPullRoute_TakingKindAndReferenceFromTheEnd(
        string rest, string repository, string kind, string reference)
    {
        Assert.True(RegistryRoute.TryParse(rest, out var route));
        Assert.Equal(repository, route.Repository);
        Assert.Equal(kind, route.Kind);
        Assert.Equal(reference, route.Reference);
    }

    [MeshTheory]
    [MeshInlineData("")]
    [MeshInlineData("memex-portal-ai")]                       // no kind/reference
    [MeshInlineData("memex-portal-ai/manifests")]             // no reference
    [MeshInlineData("memex-portal-ai/tags/latest")]           // tags only serves `list`
    [MeshInlineData("memex-portal-ai/blobs/uploads/")]        // push: not served
    [MeshInlineData("memex-portal-ai/manifests/../../secret/manifests/x")] // traversal
    [MeshInlineData("../manifests/latest")]
    public void RefusesAnythingThatIsNotAPullRoute(string rest) =>
        Assert.False(RegistryRoute.TryParse(rest, out _));

    /// <summary>
    /// The traversal case stated as the property that matters: whatever comes back as
    /// <c>Repository</c> is what the allowlist is checked against, so it must never contain a
    /// segment that could walk somewhere else.
    /// </summary>
    [MeshFact]
    public void AParsedRepository_NeverContainsATraversalSegment()
    {
        foreach (var rest in new[]
                 {
                     "ok/manifests/latest", "a/b/manifests/latest",
                     "a/../b/manifests/latest", "./a/blobs/sha256:x",
                 })
        {
            if (!RegistryRoute.TryParse(rest, out var route))
                continue;
            Assert.DoesNotContain("..", route.Repository.Split('/'));
            Assert.DoesNotContain(".", route.Repository.Split('/'));
        }
    }
}

/// <summary>
/// The push family, refused by construction. A blob is always addressed by digest, so requiring
/// the digest shape rejects every upload route without blocklisting names one at a time.
/// </summary>
public class BlobsAreDigestAddressedTest
{
    [MeshTheory]
    [MeshInlineData("repo/blobs/uploads/")]          // POST target for a push, seen as a GET
    [MeshInlineData("repo/blobs/uploads/abc-123")]   // PATCH/PUT target mid-upload
    [MeshInlineData("repo/blobs/latest")]            // a tag is not a blob reference
    [MeshInlineData("repo/blobs/sha256:")]           // empty hex
    [MeshInlineData("repo/blobs/sha256:NOTHEX")]     // uppercase / non-hex
    [MeshInlineData("repo/blobs/:abc")]              // empty algorithm
    public void RefusesABlobReferenceThatIsNotADigest(string rest) =>
        Assert.False(RegistryRoute.TryParse(rest, out _));

    [MeshTheory]
    [MeshInlineData("repo/blobs/sha256:0123456789abcdef")]
    [MeshInlineData("repo/blobs/sha512:beef")]
    public void AcceptsADigest(string rest) => Assert.True(RegistryRoute.TryParse(rest, out _));
}
