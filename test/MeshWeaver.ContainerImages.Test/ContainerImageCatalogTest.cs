using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace MeshWeaver.ContainerImages.Test;

/// <summary>
/// What the mirror records, as data. These are the acceptance criteria of issue #3353 expressed
/// as assertions over a manifest the mirror served — no <c>docker run</c>, no tarball, no ACR.
/// </summary>
public class ContainerImageCatalogTest
{
    private const string Registry = "meshweaver.azurecr.io";
    private const string Repository = "memex-portal-ai";

    private const string PortalManifest = """
    {
      "schemaVersion": 2,
      "mediaType": "application/vnd.oci.image.manifest.v1+json",
      "config": {
        "mediaType": "application/vnd.oci.image.config.v1+json",
        "digest": "sha256:c000000000000000000000000000000000000000000000000000000000000000",
        "size": 4096
      },
      "layers": [
        {
          "mediaType": "application/vnd.oci.image.layer.v1.tar+gzip",
          "digest": "sha256:a000000000000000000000000000000000000000000000000000000000000000",
          "size": 84000000
        },
        {
          "mediaType": "application/vnd.oci.image.layer.v1.tar+gzip",
          "digest": "sha256:b000000000000000000000000000000000000000000000000000000000000000",
          "size": 216000000
        }
      ]
    }
    """;

    private const string PortalIndex = """
    {
      "schemaVersion": 2,
      "mediaType": "application/vnd.oci.image.index.v1+json",
      "manifests": [
        {
          "mediaType": "application/vnd.oci.image.manifest.v1+json",
          "digest": "sha256:1111111111111111111111111111111111111111111111111111111111111111",
          "size": 1234,
          "platform": { "architecture": "amd64", "os": "linux" }
        },
        {
          "mediaType": "application/vnd.oci.image.manifest.v1+json",
          "digest": "sha256:2222222222222222222222222222222222222222222222222222222222222222",
          "size": 1234,
          "platform": { "architecture": "arm64", "os": "linux" }
        }
      ]
    }
    """;

    private static ContainerImageRecord Describe(string reference, string json) =>
        ContainerImageCatalog.Describe(
            Registry, Repository, reference, Encoding.UTF8.GetBytes(json),
            observedBy: "instance/ci", observedAt: DateTimeOffset.UnixEpoch)!;

    /// <summary>
    /// ACCEPTANCE (1): the closure of a promoted image is answerable from the record — every blob
    /// by digest and size, with no <c>docker run</c> and nothing extracted.
    /// </summary>
    [Fact]
    public void TheClosureOfAPromotedImage_IsAnsweredFromTheRecordAlone()
    {
        var record = Describe("ci.7794", PortalManifest);

        Assert.False(record.IsIndex);
        Assert.Equal(
            "sha256:c000000000000000000000000000000000000000000000000000000000000000",
            record.ConfigDigest);
        Assert.Equal(2, record.Layers.Length);
        Assert.Equal(300_004_096, record.ClosureSize);
        Assert.All(record.Layers, l => Assert.StartsWith("sha256:", l.Digest));
    }

    /// <summary>
    /// ACCEPTANCE (3): a pin bump moves ONE reference. A consumer holds the TAG; the record
    /// resolves it to a digest, so nothing downstream has to carry a digest literal.
    /// </summary>
    [Fact]
    public void ATagIsResolvedToADigest_SoAConsumerCarriesTheTagAndNotTheDigest()
    {
        var record = Describe("ci.7794", PortalManifest);

        Assert.Equal("ci.7794", record.Reference);
        Assert.StartsWith("sha256:", record.Digest);
        // The node id is derived from the tag alone, so "the current sealed set" is one path a
        // workflow can name — not six literals it has to keep in step.
        Assert.Equal(
            ContainerImageCatalog.NodeId(Repository, "ci.7794"),
            ContainerImageCatalog.NodeId(record.Repository, record.Reference));
    }

    /// <summary>
    /// 🚨 The digest is COMPUTED over the bytes, never taken from a header. A manifest IS its
    /// content hash, so a recorded digest that disagreed with the bytes would silently break
    /// every pin derived from it.
    /// </summary>
    [Fact]
    public void TheDigestIsComputedOverTheServedBytes()
    {
        var bytes = Encoding.UTF8.GetBytes(PortalManifest);
        var expected = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.Equal(expected, Describe("ci.7794", PortalManifest).Digest);
    }

    /// <summary>
    /// PROVENANCE (2 of the design's gains): where the bytes came from and who pulled them are
    /// typed fields, not a tag-naming convention to string-split.
    /// </summary>
    [Fact]
    public void ProvenanceIsRecordedAsFields_NotEncodedInAName()
    {
        var record = Describe("ci.7794", PortalManifest);

        Assert.Equal(Registry, record.Registry);
        Assert.Equal(Repository, record.Repository);
        Assert.Equal("instance/ci", record.ObservedBy);
        Assert.Equal(DateTimeOffset.UnixEpoch, record.ObservedAt);
    }

    /// <summary>
    /// An index records its PLATFORMS. Each platform digest is the reference to the record
    /// carrying that platform's own layers — written when anything pulls it, which every real
    /// pull does. The mirror never fetches speculatively, so recording costs no extra upstream
    /// request and adds no pull latency.
    /// </summary>
    [Fact]
    public void AnIndexRecordsItsPlatformsAsReferencesToTheirOwnRecords()
    {
        var record = Describe("ci.7794", PortalIndex);

        Assert.True(record.IsIndex);
        Assert.Empty(record.Layers);
        Assert.Equal(2, record.Platforms.Length);
        Assert.Contains(record.Platforms, p => p.Architecture == "amd64" && p.Os == "linux");
        Assert.Contains(record.Platforms, p => p.Architecture == "arm64");

        // The reference resolves: the platform's digest IS the reference of its own record.
        var platform = record.Platforms[0];
        Assert.Equal(
            ContainerImageCatalog.NodeId(Repository, platform.Digest),
            ContainerImageCatalog.NodeId(record.Repository, platform.Digest));
    }

    /// <summary>Bytes the mirror does not model produce NO record — it still serves them.</summary>
    [Fact]
    public void BytesThatAreNotAManifest_ProduceNoRecord() =>
        Assert.Null(ContainerImageCatalog.Describe(
            Registry, Repository, "latest", Encoding.UTF8.GetBytes("<html>502</html>"),
            null, DateTimeOffset.UnixEpoch));

    // ── node ids ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The common case stays verbatim, so a consumer that knows the tag knows the path.</summary>
    [Fact]
    public void ANodeIdIsPredictableForAPlainRepositoryAndTag() =>
        Assert.Equal("memex-portal-ai--ci.7794",
            ContainerImageCatalog.NodeId("memex-portal-ai", "ci.7794"));

    /// <summary>A digest reference is a legal id: the colon becomes a dash.</summary>
    [Fact]
    public void ADigestReferenceBecomesALegalSegment()
    {
        var id = ContainerImageCatalog.NodeId("memex-portal-ai", "sha256:abc123");
        Assert.DoesNotContain(':', id);
        Assert.DoesNotContain('/', id);
        Assert.StartsWith("memex-portal-ai--sha256-abc123", id);
    }

    /// <summary>
    /// 🚨 The reduction to one path segment is LOSSY, and two images sharing a node would make
    /// the closure answer for one of them WRONG — the single failure this data may not have. Pairs
    /// that collide after sanitisation must stay distinct.
    /// </summary>
    [Theory]
    [InlineData("team/service", "a-b")]
    [InlineData("team-service", "a/b")]
    [InlineData("team/service", "a/b")]
    [InlineData("team-service", "a-b")]
    public void LossilySanitisedPairsNeverCollide(string repository, string reference)
    {
        var ids = new[]
        {
            ContainerImageCatalog.NodeId("team/service", "a-b"),
            ContainerImageCatalog.NodeId("team-service", "a/b"),
            ContainerImageCatalog.NodeId("team/service", "a/b"),
            ContainerImageCatalog.NodeId("team-service", "a-b"),
        };
        Assert.Equal(4, ids.Distinct().Count());
        // …and the id for this pair is one of them, deterministically.
        Assert.Contains(ContainerImageCatalog.NodeId(repository, reference), ids);
    }

    [Fact]
    public void ANodeIdIsDeterministic() =>
        Assert.Equal(
            ContainerImageCatalog.NodeId("a/b", "sha256:ff"),
            ContainerImageCatalog.NodeId("a/b", "sha256:ff"));

    /// <summary>
    /// Whatever the input, the id is ONE path segment carrying only legal characters, and is never
    /// a relative-path token — otherwise a repository name could steer where the record is written.
    ///
    /// <para>Note what is NOT asserted: that the id contains no <c>..</c> substring. A dot is legal
    /// inside a segment (every tag has one), and traversal needs a segment that IS <c>..</c> — which
    /// the mandatory <c>--</c> separator makes impossible. Asserting the substring would be
    /// asserting a property that is neither true nor needed.</para>
    /// </summary>
    [Theory]
    [InlineData("a/b/c", "latest")]
    [InlineData("../escape", "latest")]
    [InlineData("repo", "../../secret")]
    [InlineData("repo", "sha256:0123456789abcdef")]
    [InlineData("", "")]
    public void ANodeIdIsAlwaysOneLegalSegment(string repository, string reference)
    {
        var id = ContainerImageCatalog.NodeId(repository, reference);
        Assert.NotEmpty(id);
        Assert.DoesNotContain('/', id);
        Assert.All(id, c => Assert.True(
            char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-',
            $"'{c}' is not legal in a node id"));
        Assert.NotEqual(".", id);
        Assert.NotEqual("..", id);
    }
}
