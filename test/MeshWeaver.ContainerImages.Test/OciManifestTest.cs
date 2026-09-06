using System.Text;
using Xunit;

namespace MeshWeaver.ContainerImages.Test;

/// <summary>
/// The manifest parser is what turns registry bytes into a CLOSURE. Everything the mirror can
/// later answer about an image — which blobs, how big, which platforms — comes from here, so the
/// property that matters is not "it parses the happy case" but that it REFUSES anything it does
/// not model. A half-parsed closure is worse than none: it answers "what is in this image?"
/// confidently, and short.
/// </summary>
public class OciManifestTest
{
    private const string Index = """
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
          "size": 1235,
          "platform": { "architecture": "arm64", "os": "linux", "variant": "v8" }
        }
      ]
    }
    """;

    private const string Manifest = """
    {
      "schemaVersion": 2,
      "mediaType": "application/vnd.oci.image.manifest.v1+json",
      "config": {
        "mediaType": "application/vnd.oci.image.config.v1+json",
        "digest": "sha256:c0c0000000000000000000000000000000000000000000000000000000000000",
        "size": 700
      },
      "layers": [
        {
          "mediaType": "application/vnd.oci.image.layer.v1.tar+gzip",
          "digest": "sha256:aaaa000000000000000000000000000000000000000000000000000000000000",
          "size": 30000000
        },
        {
          "mediaType": "application/vnd.oci.image.layer.v1.tar+gzip",
          "digest": "sha256:bbbb000000000000000000000000000000000000000000000000000000000000",
          "size": 270000000
        }
      ]
    }
    """;

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void AnIndex_NamesItsPlatforms_AndCarriesNoLayersOfItsOwn()
    {
        Assert.True(OciManifestDocument.TryParse(Bytes(Index), out var document));

        Assert.True(document.IsIndex);
        Assert.Equal("application/vnd.oci.image.index.v1+json", document.MediaType);
        Assert.Null(document.Config);
        Assert.Empty(document.Layers);
        Assert.Equal(2, document.Manifests.Length);

        Assert.Equal("linux", document.Manifests[0].Os);
        Assert.Equal("amd64", document.Manifests[0].Architecture);
        Assert.Null(document.Manifests[0].Variant);
        Assert.Equal("v8", document.Manifests[1].Variant);
        // An index's closure is REACHED through these digests, not held — so it declares none.
        Assert.Equal(0, document.ClosureSize);
    }

    [Fact]
    public void AManifest_IsTheClosure_ConfigPlusEveryLayerByDigestAndSize()
    {
        Assert.True(OciManifestDocument.TryParse(Bytes(Manifest), out var document));

        Assert.False(document.IsIndex);
        Assert.NotNull(document.Config);
        Assert.Equal(
            "sha256:c0c0000000000000000000000000000000000000000000000000000000000000",
            document.Config!.Digest);
        Assert.Equal(2, document.Layers.Length);
        Assert.Equal(300_000_700, document.ClosureSize);
    }

    /// <summary>
    /// Every shape the mirror does NOT model. Each is still SERVED — the refusal is about
    /// recording, never about the pull — but none may produce a partially-populated document.
    /// </summary>
    [Theory]
    // A Docker schema-1 manifest: fsLayers, no `layers`, no `config`.
    [InlineData("""{"schemaVersion":1,"fsLayers":[{"blobSum":"sha256:ab"}]}""")]
    // An index naming no platforms — no client can resolve it, and recording it as an image with
    // an empty closure would be a lie about a thing that does not work.
    [InlineData("""{"mediaType":"application/vnd.oci.image.index.v1+json","manifests":[]}""")]
    // Layers without a config: not a manifest shape.
    [InlineData("""{"layers":[{"digest":"sha256:ab","size":1}]}""")]
    // A config that names no digest cannot anchor a closure.
    [InlineData("""{"config":{"size":1},"layers":[]}""")]
    // Not an object.
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    // Not JSON at all — a truncated body, or an error page an intermediary substituted.
    [InlineData("<html>502</html>")]
    [InlineData("")]
    public void RefusesEveryShapeItDoesNotModel(string json) =>
        Assert.False(OciManifestDocument.TryParse(Bytes(json), out _));

    /// <summary>
    /// A layerless manifest IS legal — an attestation, a scratch image — so an empty
    /// <c>layers</c> array beside a real config is accepted rather than refused. The refusal list
    /// above is about shapes that cannot be modelled, not about images that happen to be small.
    /// </summary>
    [Fact]
    public void AcceptsALayerlessManifest_WhichIsLegal()
    {
        Assert.True(OciManifestDocument.TryParse(
            Bytes("""{"config":{"digest":"sha256:ab","size":7},"layers":[]}"""),
            out var document));
        Assert.Empty(document.Layers);
        Assert.Equal(7, document.ClosureSize);
    }

    /// <summary>
    /// A descriptor without a digest names nothing, so it is DROPPED rather than recorded as a
    /// blank entry — a closure listing an unnamed blob would be unusable and look complete.
    /// </summary>
    [Fact]
    public void DropsADescriptorThatNamesNoDigest()
    {
        Assert.True(OciManifestDocument.TryParse(
            Bytes("""
            {
              "config": { "digest": "sha256:ab", "size": 1 },
              "layers": [
                { "digest": "sha256:cd", "size": 2 },
                { "size": 3 }
              ]
            }
            """),
            out var document));
        Assert.Single(document.Layers);
        Assert.Equal("sha256:cd", document.Layers[0].Digest);
    }
}
