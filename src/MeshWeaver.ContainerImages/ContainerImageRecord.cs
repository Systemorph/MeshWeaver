using System.Collections.Immutable;

namespace MeshWeaver.ContainerImages;

/// <summary>
/// One blob of an image's closure, by digest. Content-addressed, so the same entry in two
/// repositories names the same bytes.
/// </summary>
/// <param name="Digest">The content digest, <c>algorithm:hex</c>.</param>
/// <param name="MediaType">The blob's media type.</param>
/// <param name="Size">Size in bytes, as the manifest declares it.</param>
public record ContainerImageBlob(string Digest, string MediaType, long Size);

/// <summary>
/// One platform an index resolves to. The DIGEST is the reference: the record holding that
/// platform's actual closure is <see cref="ContainerImageRecord"/> for the same repository at this
/// digest, recorded when anything pulls it (which every real pull does — a client fetches the
/// index and then the manifest for its own architecture).
/// </summary>
/// <param name="Digest">The per-platform manifest's digest.</param>
/// <param name="MediaType">That manifest's media type.</param>
/// <param name="Os">Operating system, e.g. <c>linux</c>.</param>
/// <param name="Architecture">Architecture, e.g. <c>amd64</c>.</param>
/// <param name="Variant">Architecture variant, e.g. <c>v8</c>. Usually null.</param>
public record ContainerImagePlatform(
    string Digest, string MediaType, string? Os, string? Architecture, string? Variant);

/// <summary>
/// What the mirror saw when an image was pulled through it: the image's CLOSURE and its
/// PROVENANCE, as mesh data rather than something recoverable only by pulling a tarball.
///
/// <para><b>Why this record exists</b> (<c>Doc/Architecture/ContainerRegistryInMemex</c>, issue
/// #3353): an image's contents were opaque until something failed to compile against them —
/// #3328 was, at bottom, "nobody can see what is in <c>/app</c>", and it was diagnosed with
/// <c>docker run … ls /app</c>. Every pull that flows through the mirror now leaves the manifest's
/// answer behind as a node.</para>
///
/// <para>🚨 <b>Scope, stated so nothing over-reads it.</b> This is the OCI-level closure: the
/// config blob and the layer blobs, by digest and size. It is NOT yet the /app ASSEMBLY closure
/// that <c>check-platform-reference-set.sh</c> asserts over — that lives INSIDE a layer tarball
/// and needs a layer scan, which is a separate increment (see the doc page). A consumer that
/// needs "which assemblies" must still extract; a consumer that needs "which bytes, from where,
/// resolved from which tag" is answered here.</para>
/// </summary>
public record ContainerImageRecord
{
    /// <summary>
    /// The upstream registry these bytes came from, e.g. <c>meshweaver.azurecr.io</c>. The root of
    /// the provenance chain, and the reason a record can never be mistaken for something the mesh
    /// itself produced.
    /// </summary>
    public string Registry { get; init; } = "";

    /// <summary>The repository, slashes intact, e.g. <c>memex-portal-ai</c>.</summary>
    public string Repository { get; init; } = "";

    /// <summary>
    /// The reference AS REQUESTED — a tag (<c>ci.7794</c>) or a digest. Keeping the requested form
    /// is what makes the tag → digest resolution below a recorded FACT rather than an inference.
    /// </summary>
    public string Reference { get; init; } = "";

    /// <summary>
    /// The manifest's content digest, computed over the bytes the upstream served rather than
    /// taken on trust from a header. This is the value a pin should carry, and recording it
    /// against <see cref="Reference"/> is what lets a pin bump move ONE reference: the tag is
    /// resolved here, once, instead of being copied as a digest literal into every consumer.
    /// </summary>
    public string Digest { get; init; } = "";

    /// <summary>The manifest's media type — an index type or a single-platform manifest type.</summary>
    public string? MediaType { get; init; }

    /// <summary>True when this is a multi-architecture index; then <see cref="Platforms"/> is
    /// populated and <see cref="Layers"/> is empty.</summary>
    public bool IsIndex { get; init; }

    /// <summary>When the mirror served this manifest (UTC). Point-in-time, not a first-seen
    /// watermark: the store already versions the node, so the history is the node's history.</summary>
    public DateTimeOffset ObservedAt { get; init; }

    /// <summary>Who pulled it — the caller the mirror authenticated. Null when the authenticator
    /// named no caller.</summary>
    public string? ObservedBy { get; init; }

    /// <summary>The image config blob's digest, on a single-platform manifest. Null on an index.</summary>
    public string? ConfigDigest { get; init; }

    /// <summary>Total bytes of this manifest's own closure (config + layers). Zero on an index,
    /// whose closure is reached through <see cref="Platforms"/>.</summary>
    public long ClosureSize { get; init; }

    /// <summary>The layer blobs, in order. Empty on an index.</summary>
    public ImmutableArray<ContainerImageBlob> Layers { get; init; } = [];

    /// <summary>The platforms an index names. Empty on a single-platform manifest.</summary>
    public ImmutableArray<ContainerImagePlatform> Platforms { get; init; } = [];
}
