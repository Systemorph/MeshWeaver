using System.Text.Json;

namespace MeshWeaver.Plugin.Build;

/// <summary>
/// Resolves which framework version to compile against, and — the part that matters — records it
/// <b>in full</b>.
///
/// <para>🚨 <b>The <c>.ci.N</c> suffix is part of the identity, not noise.</b> The framework has two
/// channels: a RELEASED build carries a clean <c>3.0.0-rc2</c>, while every continuous build carries
/// <c>3.0.0-rc3.ci.&lt;run-number&gt;</c>, monotonic per CI run. Those continuous builds are where
/// new API lands first. A floor of <c>3.0.0-rc3</c> is satisfied by <c>3.0.0-rc3.ci.1</c> — semver
/// orders more dot-identifiers as GREATER — so truncating the suffix declares a plugin compatible
/// with thousands of builds that predate the API it was written against, and the failure surfaces
/// as a compile error inside a customer's bake rather than here.</para>
///
/// <para>Equally, a hard-coded default version silently compiles every plugin against whatever was
/// current when the default was written. Resolving the latest at build time is what keeps that
/// honest, which is why <see cref="Latest"/> exists rather than a constant.</para>
/// </summary>
public static class FrameworkVersionResolver
{
    /// <summary>The literal a caller passes to mean "resolve the newest available".</summary>
    public const string Latest = "latest";

    /// <summary>The package whose version IS the framework version.</summary>
    public const string FrameworkPackage = "MeshWeaver.Graph";

    /// <summary>
    /// Returns <paramref name="requested"/> unchanged, or — when it is <see cref="Latest"/> — the
    /// newest version of <see cref="FrameworkPackage"/> available from <paramref name="sources"/>.
    /// </summary>
    /// <param name="requested">An explicit version, or <see cref="Latest"/>.</param>
    /// <param name="sources">Package sources: v3 service-index URLs, or local directories.</param>
    /// <param name="http">Client for feed lookups.</param>
    public static string Resolve(string requested, IReadOnlyList<string> sources, HttpClient http)
    {
        if (!string.Equals(requested, Latest, StringComparison.OrdinalIgnoreCase))
            return requested;

        var candidates = sources
            .SelectMany(source => VersionsFrom(source, http))
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"--framework-version {Latest} could not find any {FrameworkPackage} version in: "
                + string.Join(", ", sources)
                + ". Resolving nothing must not silently fall back to a hard-coded version — that "
                + "is how every plugin ends up compiled against a framework nobody runs.");

        return candidates.OrderBy(v => v, NuGetVersionComparer.Instance).Last();
    }

    private static IEnumerable<string> VersionsFrom(string source, HttpClient http)
    {
        if (Directory.Exists(source))
            return Directory
                .EnumerateFiles(source, $"{FrameworkPackage}.*.nupkg")
                .Select(f => Path.GetFileNameWithoutExtension(f)[(FrameworkPackage.Length + 1)..]);

        try
        {
            return FeedVersions(source, http);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // One unreachable source must not decide the version — but it must not be silent
            // either, or "latest" quietly means "latest on the feeds that happened to answer".
            Console.Error.WriteLine($"warning: could not read package source '{source}': {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Reads a v3 feed: service index → the <c>PackageBaseAddress/3.0.0</c> resource → the
    /// package's version list. Derived from the index rather than assumed, because the flat
    /// container lives on a different host per feed (nuget.org serves it from
    /// <c>v3-flatcontainer</c>, GitHub Packages from its own).
    /// </summary>
    private static IEnumerable<string> FeedVersions(string serviceIndexUrl, HttpClient http)
    {
        using var indexResponse = http.Send(new HttpRequestMessage(HttpMethod.Get, serviceIndexUrl));
        indexResponse.EnsureSuccessStatusCode();
        using var index = JsonDocument.Parse(indexResponse.Content.ReadAsStream());

        var baseAddress = index.RootElement.TryGetProperty("resources", out var resources)
            ? resources.EnumerateArray()
                .Where(r => r.TryGetProperty("@type", out var t)
                            && t.GetString()?.StartsWith("PackageBaseAddress/3.0.0", StringComparison.Ordinal) == true)
                .Select(r => r.TryGetProperty("@id", out var id) ? id.GetString() : null)
                .FirstOrDefault(id => !string.IsNullOrEmpty(id))
            : null;

        if (baseAddress is null)
            return [];

        var versionsUrl = baseAddress.TrimEnd('/')
                          + "/" + FrameworkPackage.ToLowerInvariant() + "/index.json";
        using var versionsResponse = http.Send(new HttpRequestMessage(HttpMethod.Get, versionsUrl));
        if (!versionsResponse.IsSuccessStatusCode)
            return [];

        using var versions = JsonDocument.Parse(versionsResponse.Content.ReadAsStream());
        return versions.RootElement.TryGetProperty("versions", out var array)
            ? [.. array.EnumerateArray().Select(v => v.GetString()).Where(v => v is not null).Select(v => v!)]
            : [];
    }
}
