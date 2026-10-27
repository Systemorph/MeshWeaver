namespace MeshWeaver.Cli;

/// <summary>
/// The images <c>memex build plugin</c> runs when the caller names none: DECLARED in configuration,
/// never baked into the CLI and never derived from the CLI's own version (the CLI and the platform
/// images are versioned independently).
///
/// <para>Resolved per image, in priority order: the command-line flag (<c>--image</c> /
/// <c>--platform-image</c>, a full reference, e.g. a digest pin) → the registry, tag and platform
/// major from <c>$MEMEX_IMAGE_REGISTRY</c> / <c>$MEMEX_IMAGE_TAG</c> / <c>$MEMEX_IMAGE_MAJOR</c> →
/// <c>imageRegistry</c> / <c>imageTag</c> / <c>imageMajor</c> in <c>~/.memex/config.json</c> →
/// <see cref="DefaultRegistry"/>, <see cref="DefaultTag"/> and <see cref="DefaultMajor"/>.</para>
///
/// <para>🚨 <b>The two images must come from ONE set</b> — the gate refuses a tester and a portal
/// that resolve different framework identities. A set version (<c>3.1.11786</c>) is carried by both
/// repositories, so it names both. The moving <c>latest</c> label is NOT one set across the two:
/// CD moves <c>memex-portal-ai:latest</c> on the ARMED set but <c>mw-plugin-test:latest</c> at
/// PROMOTION, which can run ahead of arming. The tester's tag that moves on the same armed set is
/// <c>mw-plugin-test:&lt;major&gt;-latest</c>, so a portal tag of <c>latest</c> pairs with that
/// (<see cref="TesterTagFor"/>).</para>
/// </summary>
public sealed record ImageReferences(string Registry, string Tag, string Major)
{
    /// <summary>The fleet registry: <c>cr.meshweaver.cloud</c>.</summary>
    public const string DefaultRegistry = "cr.meshweaver.cloud";

    /// <summary>The neutral moving label used when nothing is configured: <c>latest</c>.</summary>
    public const string DefaultTag = "latest";

    /// <summary>
    /// The platform major whose armed set a <c>latest</c> portal pairs with on the tester side
    /// (<c>mw-plugin-test:&lt;major&gt;-latest</c>). A declared platform line, configurable — never
    /// the CLI's own version.
    /// </summary>
    public const string DefaultMajor = "3";

    /// <summary>The tester image repository (it EXECUTES the build).</summary>
    public const string TesterRepository = "mw-plugin-test";

    /// <summary>The portal image repository (it SUPPLIES the reference set).</summary>
    public const string PlatformRepository = "memex-portal-ai";

    /// <summary>Environment variable naming the registry.</summary>
    public const string EnvRegistry = "MEMEX_IMAGE_REGISTRY";

    /// <summary>Environment variable naming the tag.</summary>
    public const string EnvTag = "MEMEX_IMAGE_TAG";

    /// <summary>Environment variable naming the platform major.</summary>
    public const string EnvMajor = "MEMEX_IMAGE_MAJOR";

    /// <summary>The tester's tag for the same set as the portal's <see cref="Tag"/>.</summary>
    public string TesterTag => TesterTagFor(Tag, Major);

    /// <summary>The tester image reference used when <c>--image</c> is not given.</summary>
    public string TesterImage => $"{Registry}/{TesterRepository}:{TesterTag}";

    /// <summary>The portal image reference used when <c>--platform-image</c> is not given.</summary>
    public string PlatformImage => $"{Registry}/{PlatformRepository}:{Tag}";

    /// <summary>
    /// The tester tag naming the same set as portal tag <paramref name="portalTag"/>: the portal's
    /// <c>latest</c> (moved on the armed set) pairs with <c>&lt;major&gt;-latest</c> (moved on the
    /// same armed set); any other tag — a set version, a line pointer — names both repositories
    /// as written.
    /// </summary>
    public static string TesterTagFor(string portalTag, string major) =>
        string.Equals(portalTag, DefaultTag, StringComparison.Ordinal) ? $"{major}-latest" : portalTag;

    /// <summary>Resolves from the process environment and <c>~/.memex/config.json</c>.</summary>
    public static ImageReferences Resolve() =>
        Resolve(Environment.GetEnvironmentVariable, MemexConfig.ReadConfigFile());

    /// <summary>Resolves from the given environment lookup and config file (pure; the test seam).</summary>
    public static ImageReferences Resolve(Func<string, string?> environment, MemexConfig.ConfigFile? file)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var registry = Pick(environment(EnvRegistry), file?.ImageRegistry) ?? DefaultRegistry;
        var tag = Pick(environment(EnvTag), file?.ImageTag) ?? DefaultTag;
        var major = Pick(environment(EnvMajor), file?.ImageMajor) ?? DefaultMajor;
        return new ImageReferences(registry.TrimEnd('/'), tag, major);
    }

    /// <summary>The flag's value when one was given, else <paramref name="fallback"/>.</summary>
    public static string Or(string? flag, string fallback) =>
        string.IsNullOrWhiteSpace(flag) ? fallback : flag.Trim();

    private static string? Pick(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c)) return c.Trim();
        return null;
    }
}
