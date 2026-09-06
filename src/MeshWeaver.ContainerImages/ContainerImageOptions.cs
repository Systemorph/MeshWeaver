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
}
