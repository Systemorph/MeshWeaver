using System.Text.Json;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.Plugin.Build;

/// <summary>
/// The <c>module-fetch</c> mode — the CONSUMING half of the release lane, and the mechanism that
/// lets a repo stop rebuilding its upstreams.
///
/// <para><b>Why this exists at all.</b> <c>Doc/Architecture/ReleaseGates</c> states the rule: a repo
/// builds only what it OWNS and consumes every dependency as a RELEASED ARTIFACT. Until now there
/// was no way to obey it — the availability gate could tell a build that its upstream had
/// published, but nothing could hand it the bytes, so every satellite cloned its upstream and let
/// the mesh Roslyn-compile it. That is a rebuild of someone else's release, it costs an ALC per
/// recompile, and it produces bytes nobody gated.</para>
///
/// <para><b>The mechanism belongs in the TECH, not in five repos' YAML.</b> A hand-rolled
/// <c>curl | unzip</c> per repo would drift, and it would skip the checks that make a fetch safe:
/// the registry is the only distributor (one credential model, one entitlement check), the bundle's
/// declared paths are validated before anything is written, and the package's own version is what
/// gets recorded. One implementation, called the same way everywhere:</para>
///
/// <code>
/// dotnet tool run meshweaver-plugin-build -- module-fetch Store \
///     --registry https://memex.meshweaver.cloud --key "$MW_INSTANCE_KEY" --out ./.upstreams
/// </code>
///
/// <para><b>The key is an INSTANCE key, not a repo credential.</b> A CI process registers with the
/// registry exactly as an installation does and presents its <c>mwi_</c> key here; what it may read
/// stays governed by that instance's grant. There is deliberately no GitHub token anywhere in this
/// path — a consumer of a release needs no access to the producer's SOURCE, which is the entire
/// point of publishing.</para>
/// </summary>
public static class ModuleFetchCommand
{
    /// <summary>The CLI verb.</summary>
    public const string Verb = "module-fetch";

    /// <summary>The registry's bundle routes. Stated here as the client's half of the contract the
    /// portal's <c>PluginBundleEndpoints.RoutePrefix</c> serves.</summary>
    private const string RoutePrefix = "/api/plugins/bundles";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record BundleRef(string? Plugin, string? Version, string? Url);

    private sealed record BundleIndex(string? FrameworkMvid, IReadOnlyList<BundleRef>? Bundles);

    /// <summary>Runs the command; returns the process exit code.</summary>
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("""
                usage: meshweaver-plugin-build module-fetch <package> [options]

                  <package>                   the package id to fetch (e.g. Store, Edu)
                  --registry <url>            registry base URL (default: https://memex.meshweaver.cloud)
                  --key <mwi_…>               this build's INSTANCE key. Required — the bundle routes
                                              authenticate the caller as a registered instance, and
                                              what it may read is that instance's grant.
                  --out <dir>                 where to materialise the package tree
                                              (default: ./.upstreams). The package lands in
                                              <out>/<package>/.
                  --version <v>               the released version to fetch (default: whatever the
                                              registry's index currently serves for this package)
                  --key-env <NAME>            read the key from an environment variable instead, so
                                              it never appears in a process listing or a CI log

                Exit codes: 0 fetched · 2 bad usage · 3 the registry refused or served nothing ·
                            4 the bundle carried no node definitions
                """);
            return 0;
        }

        var package = args[0];
        var registry = "https://memex.meshweaver.cloud";
        var outputDirectory = Path.Combine(Environment.CurrentDirectory, ".upstreams");
        string? key = null;
        string? version = null;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--registry" when i + 1 < args.Length:
                    registry = args[++i].TrimEnd('/');
                    break;
                case "--key" when i + 1 < args.Length:
                    key = args[++i];
                    break;
                // 🚨 The preferred form in CI. A key passed as an argument is visible to every
                // other process on the agent (`ps`) and is echoed by any step that logs its own
                // command line; an env var is neither.
                case "--key-env" when i + 1 < args.Length:
                    key = Environment.GetEnvironmentVariable(args[++i]);
                    break;
                case "--out" when i + 1 < args.Length:
                    outputDirectory = Path.GetFullPath(args[++i]);
                    break;
                case "--version" when i + 1 < args.Length:
                    version = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"error: unrecognised argument '{args[i]}'");
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(package) || package.Contains('/') || package.Contains('\\'))
        {
            Console.Error.WriteLine($"error: package id '{package}' is invalid — it composes a URL");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            // No anonymous fallback, deliberately: these are compiled assemblies for packages that
            // may be paid, and "open when unconfigured" is how the registry once served private
            // sources to anyone who knew the URL.
            Console.Error.WriteLine(
                "error: no instance key — pass --key or --key-env. The registry authenticates the "
                + "caller as a REGISTERED INSTANCE; there is no anonymous bundle read.");
            return 2;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");
        http.DefaultRequestHeaders.Add("Accept", "application/octet-stream");

        if (version is null)
        {
            version = ResolveVersion(http, registry, package);
            if (version is null)
            {
                Console.Error.WriteLine(
                    $"error: the registry serves no bundle for '{package}' — it has not published "
                    + "for this framework, or this instance's grant does not cover it (the two are "
                    + "deliberately indistinguishable from outside).");
                return 3;
            }
        }

        var url = $"{registry}{RoutePrefix}/{Uri.EscapeDataString(package)}/{Uri.EscapeDataString(version)}";
        using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, url));
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"error: registry answered {(int)response.StatusCode} for {url}");
            return 3;
        }

        using var buffer = new MemoryStream();
        response.Content.ReadAsStream().CopyTo(buffer);
        var bytes = buffer.ToArray();

        // BundleReader validates every declared path before returning it — a rooted or
        // parent-traversing entry throws rather than being written next to this tool's output.
        var (manifest, files) = BundleReader.ReadContent(bytes);
        if (files.Count == 0)
        {
            Console.Error.WriteLine(
                $"error: bundle {package}@{version} carries no node definitions — it is an "
                + "assemblies-only bundle, which can stamp existing nodes but cannot stand in for "
                + "the package. Its producer must pack with --content.");
            return 4;
        }

        var target = Path.Combine(outputDirectory, package);
        Materialise(files, target);

        Console.WriteLine(
            $"fetched {package}@{manifest?.Version ?? version} → {target} "
            + $"({files.Count} file(s), built against MVID {manifest?.FrameworkMvid ?? "(unrecorded)"})");
        return 0;
    }

    /// <summary>
    /// Writes the fetched tree to <paramref name="target"/>, REPLACING whatever was there.
    ///
    /// <para>🚨 Replace, never merge — and this is what makes a package's
    /// <c>content.includeSource</c> flip actually take effect. Merging would leave every file the
    /// new release no longer ships still sitting on disk: flip source OFF and yesterday's
    /// <c>Source/*.cs</c> would remain, so the consumer keeps compiling against code the producer
    /// deliberately withheld and nothing anywhere reports it. The same applies to any file a
    /// release drops — a merged tree is neither the old release nor the new one, and a build
    /// against that mix is reproducible from no commit at all.</para>
    ///
    /// <para>Public rather than internal-with-a-friendship: it is a real operation of this tool, and
    /// a seam a test can reach without the tool having to trust a test assembly by name.</para>
    /// </summary>
    /// <param name="files">The declared tree, already validated by <see cref="BundleReader"/>.</param>
    /// <param name="target">Directory the package is materialised into.</param>
    public static void Materialise(IReadOnlyList<BundleReader.ContentFile> files, string target)
    {
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);

        foreach (var file in files)
        {
            var destination = Path.Combine(
                target, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, file.Bytes);
        }
    }

    /// <summary>
    /// Asks the registry which version it currently serves for this package.
    ///
    /// <para>🚨 The index is the ONLY resolution source. Guessing a version from a local
    /// manifest.lock would defeat the point: the whole question is what the registry HAS, and a
    /// local file describes what this checkout thinks — which is exactly the divergence a fetch is
    /// supposed to remove.</para>
    /// </summary>
    private static string? ResolveVersion(HttpClient http, string registry, string package)
    {
        using var response = http.Send(
            new HttpRequestMessage(HttpMethod.Get, $"{registry}{RoutePrefix}/index.json"));
        if (!response.IsSuccessStatusCode)
            return null;

        var index = JsonSerializer.Deserialize<BundleIndex>(response.Content.ReadAsStream(), Json);
        return index?.Bundles?
            .FirstOrDefault(b => string.Equals(b.Plugin, package, StringComparison.OrdinalIgnoreCase))
            ?.Version;
    }
}
