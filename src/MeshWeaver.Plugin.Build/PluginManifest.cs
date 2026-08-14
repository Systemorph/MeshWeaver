using System.Collections.Immutable;
using System.Text.Json;

namespace MeshWeaver.Plugin.Build;

/// <summary>
/// The package-shaped view of a plugin's <c>index.json</c>.
///
/// <para>The mesh's <c>PluginContent</c> is ALREADY a package manifest — it carries a version, a
/// framework floor, and caret-ranged dependencies on other plugins (<c>"requires":
/// ["Store@^1.0.0"]</c>). Nothing here is invented; the nuspec is a projection of what plugin
/// authors already write.</para>
/// </summary>
/// <param name="Name">Plugin directory / mesh root name, e.g. <c>ThreeBody</c>.</param>
/// <param name="PackageId">The NuGet id — <see cref="Name"/> under a single reserved prefix.</param>
/// <param name="Version">Package version.</param>
/// <param name="Description">Package description.</param>
/// <param name="MinMeshVersion">Declared framework floor, or null.</param>
/// <param name="Requires">Declared plugin dependencies, verbatim (<c>Store@^1.0.0</c> or bare <c>Store</c>).</param>
public sealed record PluginManifest(
    string Name,
    string PackageId,
    string Version,
    string Description,
    string? MinMeshVersion,
    ImmutableArray<string> Requires)
{
    /// <summary>
    /// The one reserved id prefix for plugin packages. A single prefix is what lets
    /// <c>packageSourceMapping</c> pin every plugin to the private feed with one rule — without it
    /// a typo'd id silently resolves against nuget.org and installs someone else's package.
    /// </summary>
    public const string IdPrefix = "MeshWeaver.Plugin.";

    /// <summary>
    /// Reads a plugin root's <c>index.json</c>.
    /// </summary>
    /// <param name="pluginDirectory">Directory containing <c>index.json</c>.</param>
    /// <param name="fallbackVersion">Version to use when the manifest declares none — most do not,
    /// so in CI this is the build-number-derived version that keeps releases monotonic.</param>
    public static PluginManifest Read(string pluginDirectory, string fallbackVersion)
    {
        var name = Path.GetFileName(pluginDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var indexPath = Path.Combine(pluginDirectory, "index.json");
        if (!File.Exists(indexPath))
            return new PluginManifest(name, IdPrefix + name, fallbackVersion, name, null, []);

        using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
        var root = doc.RootElement;
        var content = root.TryGetProperty("content", out var c) ? c : default;

        return new PluginManifest(
            name,
            IdPrefix + name,
            Normalize(GetString(content, "version")) ?? fallbackVersion,
            GetString(content, "description") ?? GetString(root, "description") ?? name,
            GetString(content, "minMeshVersion"),
            content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("requires", out var requires)
            && requires.ValueKind == JsonValueKind.Array
                ? [.. requires.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)]
                : []);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// A caret range as NuGet interval notation — "compatible with", where the upper bound is the
    /// next version that may break.
    ///
    /// <para>🚨 <b>Below 1.0.0 the leading non-zero component is the breaking one.</b>
    /// <c>^0.2.3</c> means <c>[0.2.3,0.3.0)</c> and <c>^0.0.3</c> means <c>[0.0.3,0.0.4)</c> — not
    /// <c>[…,1.0.0)</c>. Capping every caret at the next MAJOR silently widens a 0.x dependency
    /// across its actual breaking boundary, so a resolver would happily pick a release the author
    /// declared incompatible. Harmless today only because every plugin in the tree declares
    /// <c>^1.0.0</c>; it stops being harmless the first time someone ships a 0.x module.</para>
    /// </summary>
    private static string CaretRange(string lower)
    {
        var parts = lower.Split('.');
        if (parts.Length < 1 || !int.TryParse(parts[0], out var major))
            return lower;

        if (major > 0)
            return $"[{lower},{major + 1}.0.0)";

        if (parts.Length < 2 || !int.TryParse(parts[1], out var minor))
            return $"[{lower},1.0.0)";

        if (minor > 0)
            return $"[{lower},0.{minor + 1}.0)";

        // 0.0.x — every patch may break, so the range admits exactly one.
        return parts.Length >= 3 && int.TryParse(parts[2].Split('-')[0], out var patch)
            ? $"[{lower},0.0.{patch + 1})"
            : $"[{lower},0.1.0)";
    }

    /// <summary>
    /// Manifests carry two-part versions (<c>"1.3"</c>); NuGet requires three. Widening here rather
    /// than at the author keeps the mesh manifest the source of truth.
    /// </summary>
    private static string? Normalize(string? version) =>
        version is null ? null : version.Count(ch => ch == '.') == 1 ? version + ".0" : version;

    /// <summary>
    /// Projects <see cref="Requires"/> onto NuGet dependency ranges.
    ///
    /// <para><c>Store@^1.0.0</c> is a caret range — "compatible with 1.x" — which in NuGet notation
    /// is <c>[1.0.0,2.0.0)</c>. A bare <c>Store</c> declares no floor and becomes an unbounded
    /// dependency; that is the author's statement, not something to invent a bound for.</para>
    /// </summary>
    public IEnumerable<(string Id, string? Range)> ResolveDependencies()
    {
        foreach (var requirement in Requires)
        {
            var parts = requirement.Split('@', 2);
            var id = IdPrefix + parts[0].Trim();
            if (parts.Length == 1 || string.IsNullOrWhiteSpace(parts[1]))
            {
                yield return (id, null);
                continue;
            }

            var spec = parts[1].Trim();
            if (!spec.StartsWith('^'))
            {
                yield return (id, spec);
                continue;
            }

            yield return (id, CaretRange(spec[1..]));
        }
    }
}
