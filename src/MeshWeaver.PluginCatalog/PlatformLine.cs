using MeshWeaver.Deployment;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 The PLATFORM LINE an instance is on — its major, as a string (<c>"3"</c>, <c>"4"</c>) — and the
/// one reading of it the plugin registry's per-line refs are chosen by (policy
/// <c>major-lines-run-separately</c>, Doc/Architecture/MajorLinesRunSeparately §4b). A consumer STATES
/// its line on every registry request (<see cref="RegistryPackageSource"/>); the registry serves each
/// source at the ref that source declares for that line (<see cref="ConfiguredPackageSource.ForLine"/>),
/// and at its configured ref for a line it declares nothing for.
///
/// <para>Read in this order, the first that names a major winning:</para>
/// <list type="number">
/// <item><see cref="ConfigKey"/> (<c>PluginCatalog:Line</c>) — an explicit override, for an instance
/// whose record must say something its pattern does not.</item>
/// <item>The major of <see cref="DeclaredPatternConfigKey"/> (<c>SelfUpdate:DefaultPattern</c>) — the
/// record's own <c>updatePattern</c> (<c>"3.*"</c> → 3), which is what decides which images the instance
/// runs and therefore which line it is on.</item>
/// <item>The major of the RUNNING platform (<see cref="PlatformBuildInfo.RunningPlatformVersion"/>) — a
/// fact about the binary itself, for an instance whose record names no pattern.</item>
/// </list>
/// <para>None ⇒ null, and the registry serves its configured refs exactly as before. A line nobody
/// declared a ref for is harmless for the same reason: it reads the configured ref.</para>
/// </summary>
public static class PlatformLine
{
    /// <summary>The explicit line override (<c>PluginCatalog:Line</c>).</summary>
    public const string ConfigKey = "PluginCatalog:Line";

    /// <summary>
    /// The line a REGISTRY assumes for a caller that states none (<c>PluginCatalog:UnstatedCallerLine</c>)
    /// — every such caller runs an image older than the line-stating client. Absent ⇒ the registry's
    /// OWN line, which is the line those callers were on when they last rolled.
    /// </summary>
    public const string UnstatedCallerLineKey = "PluginCatalog:UnstatedCallerLine";

    /// <summary>The record's update pattern as the portal reads it (<c>SelfUpdate:DefaultPattern</c>).</summary>
    public const string DeclaredPatternConfigKey = "SelfUpdate:DefaultPattern";

    /// <summary>The query parameter / body field a consumer states its line in.</summary>
    public const string WireName = "line";

    /// <summary>This instance's line from <paramref name="config"/> and the running platform.</summary>
    /// <param name="config">The instance configuration, or null.</param>
    /// <returns>The major as a string, or null when nothing names one.</returns>
    public static string? Of(IConfiguration? config) =>
        Resolve(config?[ConfigKey], config?[DeclaredPatternConfigKey], PlatformBuildInfo.RunningPlatformVersion);

    /// <summary>The pure rule behind <see cref="Of"/>.</summary>
    /// <param name="explicitLine">The <see cref="ConfigKey"/> value.</param>
    /// <param name="pattern">The declared update pattern (<c>3.*</c>, <c>3.1.*</c>, <c>4.*</c>).</param>
    /// <param name="runningVersion">The running platform version.</param>
    /// <returns>The major as a string, or null.</returns>
    public static string? Resolve(string? explicitLine, string? pattern, string? runningVersion) =>
        Normalize(explicitLine) ?? MajorOf(pattern) ?? MajorOf(runningVersion);

    /// <summary>A stated line in its canonical form — a non-negative integer without leading zeros,
    /// or null for anything else (blank, <c>*</c>, a word). Pure.</summary>
    /// <param name="line">The stated line.</param>
    /// <returns>The canonical line, or null.</returns>
    public static string? Normalize(string? line)
    {
        var s = (line ?? "").Trim();
        return s.Length > 0 && s.All(char.IsAsciiDigit) && int.TryParse(s, out var major)
            ? major.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>The leading major of a version or pattern (<c>3.*</c> → <c>"3"</c>, <c>3.1.0-ci.1</c> →
    /// <c>"3"</c>, <c>*</c> → null). Pure.</summary>
    /// <param name="versionOrPattern">A version string or an update pattern.</param>
    /// <returns>The major as a string, or null.</returns>
    public static string? MajorOf(string? versionOrPattern) =>
        PrebuiltAdoptionPolicy.MajorOf(versionOrPattern) is { } major
            ? major.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;

    /// <summary>The config section the per-line refs are read from (<c>PluginCatalog:LineRefs</c>).</summary>
    public const string LineRefsSection = DeploymentPortalConfig.LineRefsSection;

    /// <summary>The per-line refs <paramref name="config"/> declares for the source named
    /// <paramref name="sourceName"/> (<c>PluginCatalog:LineRefs:{segment}:{major}</c>, the segment being
    /// <see cref="PluginRepoMount.KeySegmentOf"/>). Empty when none.</summary>
    /// <param name="config">The configuration.</param>
    /// <param name="sourceName">The source's configured name.</param>
    /// <returns>Line → ref.</returns>
    public static System.Collections.Immutable.ImmutableSortedDictionary<string, string> RefsFor(
        IConfiguration config, string? sourceName)
    {
        var segment = PluginRepoMount.KeySegmentOf(sourceName);
        var builder = System.Collections.Immutable.ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (segment.Length == 0)
            return builder.ToImmutable();
        foreach (var entry in config.GetSection($"{LineRefsSection}:{segment}").GetChildren())
            if (Normalize(entry.Key) is { } line && !string.IsNullOrWhiteSpace(entry.Value))
                builder[line] = entry.Value.Trim();
        return builder.ToImmutable();
    }
}
