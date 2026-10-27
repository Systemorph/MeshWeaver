namespace MeshWeaver.Cli;

/// <summary>
/// The images <c>memex build plugin</c> runs when the caller names none: DECLARED in configuration,
/// never baked into the CLI and never derived from the CLI's own version (the CLI and the platform
/// images are versioned independently).
///
/// <para>Resolved per image, in priority order: the command-line flag (<c>--image</c> /
/// <c>--platform-image</c>, a full reference, e.g. a digest pin) → the registry and tag from
/// <c>$MEMEX_IMAGE_REGISTRY</c> / <c>$MEMEX_IMAGE_TAG</c> → <c>imageRegistry</c> / <c>imageTag</c>
/// in <c>~/.memex/config.json</c> → <see cref="DefaultRegistry"/> and <see cref="DefaultTag"/>.
/// ONE tag names both images, so a configured set label pairs the tester and the portal of the
/// same wave; the gate still verifies that they resolve one framework identity and refuses when
/// they do not.</para>
/// </summary>
public sealed record ImageReferences(string Registry, string Tag)
{
    /// <summary>The fleet registry: <c>cr.meshweaver.cloud</c>.</summary>
    public const string DefaultRegistry = "cr.meshweaver.cloud";

    /// <summary>The neutral moving label used when nothing is configured: <c>latest</c>.</summary>
    public const string DefaultTag = "latest";

    /// <summary>The tester image repository (it EXECUTES the build).</summary>
    public const string TesterRepository = "mw-plugin-test";

    /// <summary>The portal image repository (it SUPPLIES the reference set).</summary>
    public const string PlatformRepository = "memex-portal-ai";

    /// <summary>Environment variable naming the registry.</summary>
    public const string EnvRegistry = "MEMEX_IMAGE_REGISTRY";

    /// <summary>Environment variable naming the tag.</summary>
    public const string EnvTag = "MEMEX_IMAGE_TAG";

    /// <summary>The tester image reference used when <c>--image</c> is not given.</summary>
    public string TesterImage => $"{Registry}/{TesterRepository}:{Tag}";

    /// <summary>The portal image reference used when <c>--platform-image</c> is not given.</summary>
    public string PlatformImage => $"{Registry}/{PlatformRepository}:{Tag}";

    /// <summary>Resolves from the process environment and <c>~/.memex/config.json</c>.</summary>
    public static ImageReferences Resolve() =>
        Resolve(Environment.GetEnvironmentVariable, MemexConfig.ReadConfigFile());

    /// <summary>Resolves from the given environment lookup and config file (pure; the test seam).</summary>
    public static ImageReferences Resolve(Func<string, string?> environment, MemexConfig.ConfigFile? file)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var registry = Pick(environment(EnvRegistry), file?.ImageRegistry) ?? DefaultRegistry;
        var tag = Pick(environment(EnvTag), file?.ImageTag) ?? DefaultTag;
        return new ImageReferences(registry.TrimEnd('/'), tag);
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
