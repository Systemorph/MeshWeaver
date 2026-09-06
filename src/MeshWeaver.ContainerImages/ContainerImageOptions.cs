namespace MeshWeaver.ContainerImages;

/// <summary>
/// Configuration for the container-registry mirror, bound from <c>ContainerImages:*</c>.
///
/// <para>🚨 The mirror is OFF unless all three of <see cref="Upstream"/>, <see cref="Username"/>
/// and <see cref="Password"/> are present. An unconfigured mirror answers 404 on every route
/// rather than falling back to anything: a half-configured registry that served SOMETHING would
/// be indistinguishable from a working one right up until a pull returned the wrong bytes.</para>
/// </summary>
public sealed class ContainerImageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ContainerImages";

    /// <summary>Upstream registry host, e.g. <c>meshweaver.azurecr.io</c>. No scheme.</summary>
    public string? Upstream { get; set; }

    /// <summary>Upstream pull credential — the ONE copy the fleet keeps. Never logged.</summary>
    public string? Username { get; set; }

    /// <summary>Upstream pull credential. Never logged.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Repositories this mirror will serve, exact names. EMPTY MEANS NONE, never "all": a mirror
    /// that proxies any repository name a caller invents turns one upstream credential into an
    /// open read proxy for the whole registry.
    /// </summary>
    public string[] Repositories { get; set; } = [];

    /// <summary>
    /// The mesh path observed images are recorded under, e.g. <c>Platform/Images</c>. That node
    /// must already exist — a record is a CHILD of it, and creating a parent chain from a pull
    /// path is how a write lane NotFound-storms itself (#2229).
    ///
    /// <para>🚨 EMPTY MEANS RECORDING IS OFF, and the mirror still proxies normally. Recording is
    /// observational: it answers "what is in this image, and where did it come from" without a
    /// <c>docker run</c>, and it must never be able to fail a pull. Turning it off — like turning
    /// the whole mirror off — is a configuration change, never a migration.</para>
    /// </summary>
    public string? ImageRoot { get; set; }

    /// <summary>
    /// Largest manifest the mirror will hold in memory in order to record it. Manifests are
    /// small — the OCI spec caps them at 4 MiB and ACR's are single-digit KB — so this is a
    /// sanity bound, not a tuning knob.
    ///
    /// <para>🚨 It applies to MANIFESTS ONLY. A blob (a layer, hundreds of megabytes) is NEVER
    /// buffered under any setting: it streams upstream → socket → client. A manifest larger than
    /// this is still served, streamed, and simply not recorded.</para>
    /// </summary>
    public int MaxRecordedManifestBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Directory the read-through cache stores blobs and manifests in, keyed by content digest.
    ///
    /// <para>🚨 EMPTY MEANS THE CACHE IS OFF, and the mirror still proxies every pull — exactly
    /// the behaviour it had before the cache existed. Like <see cref="ImageRoot"/>, turning it on
    /// or off is a configuration change, never a migration: nothing in the cache is authoritative,
    /// so discarding the whole directory costs a re-fetch and nothing else.</para>
    ///
    /// <para>🚨 The cache is NOT an archive and must never be treated as one. It is bounded by
    /// <see cref="CacheMaxBytes"/> and evicts least-recently-used entries, so a digest that is
    /// resident today may not be tomorrow. It can only ever ADD availability — a miss falls
    /// through to the upstream — never subtract it.</para>
    /// </summary>
    public string? CacheDirectory { get; set; }

    /// <summary>
    /// Byte budget for <see cref="CacheDirectory"/>. A sweep runs after roughly an eighth of this
    /// has been added and evicts least-recently-used entries until the directory is back under
    /// 90 % of the budget, so the cache overshoots between sweeps by design rather than thrashing
    /// on every store.
    ///
    /// <para>Default 20 GiB — a handful of portal images and their shared base layers. This is a
    /// disk budget, not a tuning knob for correctness: every value serves the same bytes.</para>
    /// </summary>
    public long CacheMaxBytes { get; set; } = 20L * 1024 * 1024 * 1024;
}
