using System.Text.Json;

using MeshWeaver.Plugin.Packaging;

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

        // 🚨 The floor is the newest version at which EVERY referenced package is published —
        // not the newest MeshWeaver.Graph. Every emitted project references all of
        // CompilationEnvironment.PackageIds, so a version where one of them has no build is not a
        // floor any plugin can honestly claim: NuGet resolves the missing one to whatever else it
        // can find and the restore dies with an NU1605 downgrade before compiling anything.
        //
        // This is not hypothetical. When MeshWeaver.AI's source moved out of the platform repo,
        // the platform stopped publishing it. The framework released 3.0.0-rc8, MeshWeaver.AI
        // stopped at 3.0.0-rc7, and 33 of 33 code-bearing packages failed to pack.
        //
        // Intersecting rather than pinning each package separately is deliberate: a mixed floor
        // resolves rc7-era transitives alongside rc8 ones, and a type that moved between
        // assemblies then exists in both — CS0433, reported against innocent plugin source.
        var candidates = CompilationEnvironment.PackageIds
            .Select(package => sources
                .SelectMany(source => VersionsFrom(package, source, http))
                .ToHashSet(StringComparer.OrdinalIgnoreCase))
            // A package no source can answer for constrains nothing — the alternative is that one
            // unreachable feed empties the intersection and resolves nothing at all.
            .Where(versions => versions.Count > 0)
            .Aggregate(
                (IEnumerable<string>?)null,
                (intersection, versions) => intersection is null
                    ? versions
                    : intersection.Intersect(versions, StringComparer.OrdinalIgnoreCase))
            ?.ToList()
            ?? [];

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"--framework-version {Latest} could not find a version published for ALL of "
                + string.Join(", ", CompilationEnvironment.PackageIds) + " in: "
                + string.Join(", ", sources)
                + ". Resolving nothing must not silently fall back to a hard-coded version — that "
                + "is how every plugin ends up compiled against a framework nobody runs.");

        return candidates.OrderBy(v => v, NuGetVersionComparer.Instance).Last();
    }


    private static IEnumerable<string> VersionsFrom(string packageId, string source, HttpClient http)
    {
        if (Directory.Exists(source))
            return Directory
                .EnumerateFiles(source, $"{packageId}.*.nupkg")
                .Select(f => Path.GetFileNameWithoutExtension(f)[(packageId.Length + 1)..]);

        try
        {
            return FeedVersions(packageId, source, http);
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
    private static IEnumerable<string> FeedVersions(string packageId, string serviceIndexUrl, HttpClient http)
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
                          + "/" + packageId.ToLowerInvariant() + "/index.json";
        using var versionsResponse = http.Send(new HttpRequestMessage(HttpMethod.Get, versionsUrl));
        if (!versionsResponse.IsSuccessStatusCode)
            return [];

        using var versions = JsonDocument.Parse(versionsResponse.Content.ReadAsStream());
        return versions.RootElement.TryGetProperty("versions", out var array)
            ? [.. array.EnumerateArray().Select(v => v.GetString()).Where(v => v is not null).Select(v => v!)]
            : [];
    }
}
