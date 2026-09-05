namespace MeshWeaver.ContainerRegistry;

/// <summary>
/// Configuration for the container-registry mirror, bound from <c>ContainerRegistry:*</c>.
///
/// <para>🚨 The mirror is OFF unless all three of <see cref="Upstream"/>, <see cref="Username"/>
/// and <see cref="Password"/> are present. An unconfigured mirror answers 404 on every route
/// rather than falling back to anything: a half-configured registry that served SOMETHING would
/// be indistinguishable from a working one right up until a pull returned the wrong bytes.</para>
/// </summary>
public sealed class ContainerRegistryOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ContainerRegistry";

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
}
