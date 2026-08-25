using System.Collections.Immutable;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Every asset reference inside the JavaScript we ship must resolve to an asset that is actually
/// packaged — in the RIGHT content package.
///
/// <para>Razor-class-library JS serves under <c>_content/{Package}/…</c>, so a relative ES-module
/// import resolves against the package the FILE lives in, not the package the asset lives in. When
/// a component (with its collocated <c>.razor.js</c>) moves to another project, a same-package
/// relative import like <c>'../highlightUtils.js'</c> silently starts pointing into the new
/// package, where the asset does not exist. Nothing catches this at build time: the C# compiles,
/// CI is green, and the 404 first appears in the browser — and because a failed STATIC import
/// takes the whole module down, the component's ErrorBoundary fires on every page that mounts it
/// ("Importing a module script failed"). That is exactly how the view-pack extraction broke every
/// markdown page on the deployed portals.</para>
///
/// <para>This test rebuilds the served-URL space from the source tree — each project's
/// <c>wwwroot/**</c> plus its collocated <c>*.razor.js</c>/<c>*.razor.css</c> — and resolves every
/// relative ES-module import and every literal <c>_content/{Package}/{path}</c> reference found in
/// first-party JS. Vendored third-party bundles (<c>lib/</c> folders) are part of the served set
/// but are not scanned: their internal imports are the vendor's contract, not ours. References to
/// packages outside this repository (e.g. FluentUI) cannot be validated from source and are
/// skipped.</para>
/// </summary>
public class StaticAssetReferenceIntegrityTest
{
    // import x from '…'; import { a, b } from "…"; import '…'
    private static readonly Regex StaticImportRegex = new(
        @"(?m)^\s*import\s+(?:[^'""]*?from\s+)?['""]([^'""]+)['""]",
        RegexOptions.Compiled);

    // await import('…')
    private static readonly Regex DynamicImportRegex = new(
        @"import\(\s*['""]([^'""]+)['""]\s*\)",
        RegexOptions.Compiled);

    // '…_content/{Package}/{path.ext}…' inside any string/comment — the extension requirement
    // keeps prose mentions of _content out of scope.
    private static readonly Regex ContentPathRegex = new(
        @"_content/([A-Za-z0-9_.]+)/([A-Za-z0-9_\-./]+\.(?:m?js|css|json|map|woff2?|png|svg|gif|ico))",
        RegexOptions.Compiled);

    [Fact]
    public void EveryFirstPartyJsAssetReference_ResolvesToAPackagedAsset()
    {
        var root = FindRepositoryRoot();
        Assert.SkipWhen(root is null,
            "repository tree not reachable from the test bin — this convention check runs in-repo only");

        var packages = DiscoverPackages(Path.Combine(root!, "src"));

        // A discovery that found nothing has verified nothing; it must not read as a pass.
        Assert.True(packages.Count > 3,
            "the repository ships several RCLs with static assets — a tiny discovery means the walk broke");
        Assert.True(packages.Values.Sum(p => p.ScannedFiles.Count) > 10,
            "there are many first-party JS modules — an empty scan means the walk broke, not that references hold");

        var failures = new List<string>();

        foreach (var package in packages.Values)
        foreach (var (fileRepoPath, servedPath) in package.ScannedFiles)
        {
            var text = File.ReadAllText(Path.Combine(root!, fileRepoPath));

            foreach (Match match in StaticImportRegex.Matches(text).Concat(DynamicImportRegex.Matches(text)))
                CheckRelativeImport(fileRepoPath, servedPath, package.Name, match.Groups[1].Value, packages, failures);

            foreach (Match match in ContentPathRegex.Matches(text))
                CheckContentReference(fileRepoPath, match.Groups[1].Value, match.Groups[2].Value, packages, failures);
        }

        Assert.True(failures.Count == 0,
            "every relative import / _content reference in shipped JS must point at an asset that "
            + "exists in the named package. A same-package relative path breaks when the file moves "
            + "to another project — reference cross-package assets as '../../{OtherPackage}/…' "
            + "(module imports) or '_content/{OtherPackage}/…' (script.src). Failures:\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// Resolves a relative ES-module specifier against the importing file's served URL and asserts
    /// the target exists in whichever package the resolution lands in.
    /// </summary>
    private static void CheckRelativeImport(
        string fileRepoPath,
        string servedPath,
        string packageName,
        string specifier,
        IReadOnlyDictionary<string, PackageAssets> packages,
        List<string> failures)
    {
        // Bare specifiers, absolute URLs and app-absolute paths are not package-relative — out of scope.
        if (!specifier.StartsWith("./", StringComparison.Ordinal)
            && !specifier.StartsWith("../", StringComparison.Ordinal))
            return;

        // The importing module's URL is _content/{package}/{servedPath}; resolve the specifier
        // against its directory the way the browser does.
        var baseSegments = $"_content/{packageName}/{servedPath}".Split('/')[..^1].ToList();
        foreach (var segment in specifier.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (baseSegments.Count == 0)
                    {
                        failures.Add($"{fileRepoPath}: import '{specifier}' escapes the _content root");
                        return;
                    }
                    baseSegments.RemoveAt(baseSegments.Count - 1);
                    continue;
                default:
                    baseSegments.Add(segment);
                    continue;
            }
        }

        if (baseSegments.Count < 3 || baseSegments[0] != "_content")
        {
            failures.Add($"{fileRepoPath}: import '{specifier}' resolves outside _content/ ({string.Join('/', baseSegments)})");
            return;
        }

        CheckContentReference(fileRepoPath, baseSegments[1], string.Join('/', baseSegments.Skip(2)), packages, failures,
            origin: $"import '{specifier}'");
    }

    private static void CheckContentReference(
        string fileRepoPath,
        string packageName,
        string assetPath,
        IReadOnlyDictionary<string, PackageAssets> packages,
        List<string> failures,
        string? origin = null)
    {
        // Packages outside this repository (FluentUI, …) cannot be validated from source.
        if (!packages.TryGetValue(packageName, out var package))
            return;

        if (!package.ServedAssets.Contains(assetPath))
            failures.Add(
                $"{fileRepoPath}: {origin ?? $"reference '_content/{packageName}/{assetPath}'"} "
                + $"-> _content/{packageName}/{assetPath} is not a packaged asset of {packageName}");
    }

    /// <summary>
    /// One entry per <c>src/</c> project that contributes static web assets: the set of paths it
    /// serves under <c>_content/{Name}/</c> (wwwroot files plus collocated <c>.razor.js/.css</c>),
    /// and the first-party JS files to scan (everything served except vendored <c>lib/</c> bundles).
    /// </summary>
    private sealed record PackageAssets(
        string Name,
        ImmutableHashSet<string> ServedAssets,
        IReadOnlyList<(string FileRepoPath, string ServedPath)> ScannedFiles);

    private static IReadOnlyDictionary<string, PackageAssets> DiscoverPackages(string src)
    {
        var packages = new Dictionary<string, PackageAssets>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(src))
            return packages;

        var root = Path.GetDirectoryName(src)!;
        foreach (var csproj in Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories))
        {
            var projectDir = Path.GetDirectoryName(csproj)!;
            var relative = Path.GetRelativePath(root, csproj).Split(Path.DirectorySeparatorChar);
            if (relative.Any(part => part is "bin" or "obj" or ".worktrees"))
                continue;

            var name = Path.GetFileNameWithoutExtension(csproj);
            var served = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
            var scanned = new List<(string, string)>();

            var wwwroot = Path.Combine(projectDir, "wwwroot");
            if (Directory.Exists(wwwroot))
                foreach (var file in Directory.EnumerateFiles(wwwroot, "*", SearchOption.AllDirectories))
                    AddAsset(file, Path.GetRelativePath(wwwroot, file));

            foreach (var pattern in new[] { "*.razor.js", "*.razor.css" })
            foreach (var file in Directory.EnumerateFiles(projectDir, pattern, SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(projectDir, file);
                if (rel.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "wwwroot"))
                    continue;
                AddAsset(file, rel);
            }

            if (served.Count > 0)
                packages[name] = new PackageAssets(name, served.ToImmutable(), scanned);
            continue;

            void AddAsset(string file, string relPath)
            {
                var servedPath = relPath.Replace(Path.DirectorySeparatorChar, '/');
                served.Add(servedPath);
                var isVendored = servedPath.Split('/').Contains("lib");
                if (!isVendored && servedPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                    scanned.Add((Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'), servedPath));
            }
        }

        return packages;
    }

    private static string? FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
