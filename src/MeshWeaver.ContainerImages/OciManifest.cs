using System.Collections.Immutable;
using System.Text.Json;

namespace MeshWeaver.ContainerImages;

/// <summary>
/// One entry of an image's closure: a content-addressed blob, named by digest and sized. This is
/// the OCI Distribution spec's <c>descriptor</c>, reduced to the fields the mirror records.
/// </summary>
/// <param name="MediaType">The blob's media type, e.g.
/// <c>application/vnd.oci.image.layer.v1.tar+gzip</c>.</param>
/// <param name="Digest">The content digest, <c>algorithm:hex</c>. An IMMUTABLE id: two images
/// naming the same digest name the same bytes, which is why layer dedup across repositories is
/// free and why a closure recorded once stays true.</param>
/// <param name="Size">The blob's size in bytes as the manifest declares it.</param>
public sealed record OciDescriptor(string MediaType, string Digest, long Size)
{
    /// <summary>Operating system, for an index entry (<c>linux</c>). Null on a layer or config.</summary>
    public string? Os { get; init; }

    /// <summary>Architecture, for an index entry (<c>amd64</c>, <c>arm64</c>). Null otherwise.</summary>
    public string? Architecture { get; init; }

    /// <summary>Architecture variant, for an index entry (<c>v8</c>). Null otherwise.</summary>
    public string? Variant { get; init; }
}

/// <summary>
/// A parsed OCI manifest or image index — the ONE place the mirror interprets registry bytes.
///
/// <para>Two shapes share one route. An <b>index</b> (<c>manifests</c>) is the multi-architecture
/// entry point a tag usually resolves to: it names one manifest per platform and carries no layers
/// of its own. A <b>manifest</b> (<c>config</c> + <c>layers</c>) is one platform's image and IS
/// the closure — the config blob plus every layer blob, each by digest.</para>
///
/// <para>🚨 Parsing is <b>total and refusing</b>: anything that is not one of those two shapes —
/// a Docker v1 <c>fsLayers</c> manifest, a signature artifact, a truncated body — returns
/// <c>false</c> rather than a half-populated document. A half-populated closure is worse than no
/// closure: it would answer "what is in this image?" with a confident, wrong, shorter list.</para>
/// </summary>
public sealed record OciManifestDocument
{
    /// <summary>The manifest's own declared media type, when it carries one.</summary>
    public string? MediaType { get; init; }

    /// <summary>The image config blob — present on a single-platform manifest, null on an index.</summary>
    public OciDescriptor? Config { get; init; }

    /// <summary>The layer blobs, in order. Empty on an index.</summary>
    public ImmutableArray<OciDescriptor> Layers { get; init; } = [];

    /// <summary>The per-platform manifests this index names. Empty on a single-platform manifest.</summary>
    public ImmutableArray<OciDescriptor> Manifests { get; init; } = [];

    /// <summary>True when this is a multi-architecture index rather than one platform's image.</summary>
    public bool IsIndex => !Manifests.IsDefaultOrEmpty;

    /// <summary>
    /// Total bytes of this manifest's own closure — the config blob plus every layer. Zero on an
    /// index, whose closure is reached through <see cref="Manifests"/> rather than held directly.
    /// </summary>
    public long ClosureSize =>
        (Config?.Size ?? 0)
        + (Layers.IsDefaultOrEmpty ? 0 : Layers.Sum(l => l.Size));

    /// <summary>
    /// Parses manifest bytes. Returns false for anything that is not an OCI/Docker v2 manifest or
    /// index — see the refusal note on the type.
    /// </summary>
    /// <param name="json">The manifest body exactly as the upstream served it.</param>
    /// <param name="document">The parsed document; undefined when this returns false.</param>
    public static bool TryParse(byte[] json, out OciManifestDocument document)
    {
        document = new OciManifestDocument();
        if (json.Length == 0)
            return false;

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // Not JSON at all. The mirror still SERVES the bytes — recording is observational and
            // never gates a pull — it simply records nothing about them.
            return false;
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var mediaType = root.TryGetProperty("mediaType", out var mt)
                            && mt.ValueKind == JsonValueKind.String
                ? mt.GetString()
                : null;

            if (root.TryGetProperty("manifests", out var manifests)
                && manifests.ValueKind == JsonValueKind.Array)
            {
                var entries = ReadDescriptors(manifests, withPlatform: true);
                if (entries.IsEmpty)
                    return false;
                document = new OciManifestDocument { MediaType = mediaType, Manifests = entries };
                return true;
            }

            if (root.TryGetProperty("layers", out var layers)
                && layers.ValueKind == JsonValueKind.Array
                && root.TryGetProperty("config", out var config)
                && TryReadDescriptor(config, withPlatform: false, out var configDescriptor))
            {
                document = new OciManifestDocument
                {
                    MediaType = mediaType,
                    Config = configDescriptor,
                    // A layerless manifest is legal (an attestation, an empty scratch image), so
                    // an empty array is NOT a refusal here — unlike an index naming no platforms,
                    // which cannot be resolved by any client.
                    Layers = ReadDescriptors(layers, withPlatform: false),
                };
                return true;
            }

            return false;
        }
    }

    private static ImmutableArray<OciDescriptor> ReadDescriptors(JsonElement array, bool withPlatform)
    {
        var builder = ImmutableArray.CreateBuilder<OciDescriptor>();
        foreach (var element in array.EnumerateArray())
            if (TryReadDescriptor(element, withPlatform, out var descriptor))
                builder.Add(descriptor);
        return builder.ToImmutable();
    }

    private static bool TryReadDescriptor(
        JsonElement element, bool withPlatform, out OciDescriptor descriptor)
    {
        descriptor = null!;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        // 🚨 The DIGEST is the only field that must be present and well-shaped: it is the id the
        // closure is expressed in. A descriptor without one names nothing and is dropped rather
        // than recorded as a blank entry.
        if (!element.TryGetProperty("digest", out var digest)
            || digest.ValueKind != JsonValueKind.String
            || digest.GetString() is not { Length: > 0 } digestValue)
            return false;

        var mediaType = element.TryGetProperty("mediaType", out var mt)
                        && mt.ValueKind == JsonValueKind.String
            ? mt.GetString() ?? string.Empty
            : string.Empty;
        var size = element.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var sizeValue)
            ? sizeValue
            : 0;

        descriptor = new OciDescriptor(mediaType, digestValue, size);
        if (withPlatform
            && element.TryGetProperty("platform", out var platform)
            && platform.ValueKind == JsonValueKind.Object)
        {
            descriptor = descriptor with
            {
                Os = ReadString(platform, "os"),
                Architecture = ReadString(platform, "architecture"),
                Variant = ReadString(platform, "variant"),
            };
        }
        return true;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
