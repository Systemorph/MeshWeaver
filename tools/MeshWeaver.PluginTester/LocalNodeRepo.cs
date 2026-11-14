using System.Collections.Immutable;
using System.Text;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.PluginCatalog;

namespace MeshWeaver.PluginTester;

/// <summary>
/// Reads a node-repo checkout (the <c>MeshWeaver.Plugins</c> working tree) from the local
/// filesystem into GitSync's <see cref="RepoSnapshot"/> shape, discovers the installable
/// packages (top-level folders whose <c>index.json</c> declares a root node), and orders them
/// by their cross-package source dependencies (a package whose NodeTypes pull shared sources
/// from a sibling package — e.g. Edu sharing Store sources — installs AFTER that sibling, so
/// every referenced Code node has landed before the dependent type compiles).
/// </summary>
public static class LocalNodeRepo
{
    /// <summary>
    /// Directories that never carry mesh nodes (VCS/CI/build internals), plus <c>.worktrees</c> —
    /// the conventional home of a checkout's linked git worktrees, i.e. OTHER branches' trees.
    /// </summary>
    private static readonly ImmutableHashSet<string> ExcludedDirectories =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            ".git", ".github", ".claude", ".worktrees", "node_modules", "bin", "obj");

    /// <summary>
    /// True when <paramref name="directory"/> — a directory BELOW the sweep root — is the root of
    /// another git working tree: it holds a <c>.git</c> entry, a FILE for a linked worktree or a
    /// submodule, a DIRECTORY for a nested repository. Such a tree is a different checkout (another
    /// branch, another repository), never content of the one being swept.
    ///
    /// <para>🚨 A local checkout that keeps its worktrees inside itself made the sweep read every
    /// one of them before package discovery — minutes of silence, and a snapshot carrying other
    /// branches' files. A clean CI checkout has none, and GitSync's GitHub fetch never sees a
    /// nested tree's files either (a submodule is a gitlink, not content), so skipping them keeps
    /// the local sweep equal to what CI and the mesh read. The sweep ROOT itself is never tested:
    /// it may well be a linked worktree (a <c>.git</c> file) — that is the tree being swept.</para>
    /// </summary>
    /// <param name="directory">A subdirectory of the sweep root.</param>
    internal static bool IsNestedGitTree(string directory)
    {
        var marker = Path.Combine(directory, ".git");
        return File.Exists(marker) || Directory.Exists(marker);
    }

    /// <summary>
    /// Sweeps <paramref name="repoRoot"/> into a <see cref="RepoSnapshot"/> on the file I/O
    /// pool (the filesystem walk is a sync-blocking leaf — never run it on a hub scheduler).
    /// Valid-UTF-8 files ride as text, everything else as binary bytes — the same
    /// classification GitSync's GitHub fetch applies.
    /// </summary>
    public static IObservable<RepoSnapshot> Load(string repoRoot, IIoPool pool) =>
        pool.InvokeBlocking(_ => LoadSync(repoRoot));

    /// <summary>
    /// The sweep itself. Public and synchronous so the compiler-driven bake (#1763), which runs in
    /// a build process with no mesh and therefore no <see cref="IIoPool"/>, reads the SAME file set
    /// with the SAME exclusions and the SAME UTF-8 classification the mesh-driven gate reads —
    /// "which files are content" must not fork between the two bake paths.
    /// </summary>
    /// <param name="repoRoot">The checkout root to sweep.</param>
    public static RepoSnapshot LoadSync(string repoRoot)
    {
        var root = Path.GetFullPath(repoRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repo root '{root}' does not exist.");

        var files = new List<RepoFile>();
        Sweep(root, root, files);
        return new RepoSnapshot("local", files
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToImmutableList());
    }

    private static void Sweep(string root, string directory, List<RepoFile> files)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            // A linked worktree's root carries `.git` as a FILE (a `gitdir:` pointer). It is VCS
            // plumbing exactly like the `.git` DIRECTORY a CI checkout has, so it is no content.
            if (string.Equals(Path.GetFileName(file), ".git", StringComparison.OrdinalIgnoreCase))
                continue;
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that CANNOT be read is skipped loudly, never a fatal for the whole
                // sweep. A local checkout legitimately holds debris no clean CI checkout has —
                // the first real `build` run on a dev machine died, with nothing printed, on
                // e2e/.auth/profile/RunningChromeVersion, a dead symlink left by a Playwright
                // profile (gitignored, so invisible to every CI run). Such a file would have
                // crashed BOTH bake paths before, so skipping it cannot fork the swept content
                // set between them ("which files are content" stays shared). Console.Error, not
                // a callback: both callers are CLI entry points, and a silent skip would be the
                // same defect this tool just taught us about.
                Console.Error.WriteLine($"sweep: skipped unreadable '{relative}': {ex.Message}");
                continue;
            }
            files.Add(TryDecodeUtf8(bytes, out var text)
                ? new RepoFile(relative, text)
                : new RepoFile(relative, string.Empty, bytes));
        }
        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(sub);
            if (ExcludedDirectories.Contains(name) || IsNestedGitTree(sub))
                continue;
            Sweep(root, sub, files);
        }
    }

    // Strict UTF-8 probe (throw-on-invalid decoder) so an arbitrary byte stream — a video, a
    // font — is classified binary rather than lossily decoded. Mirrors OctokitGitHubRepoClient.
    internal static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Discovers every installable package in the snapshot — <see cref="NodeRepoPackageGraph.DiscoverPackages"/>,
    /// which is the one implementation (moved there so the combo gate reads the same packages,
    /// MeshWeaver#4413).
    /// </summary>
    /// <param name="snapshot">The swept tree.</param>
    public static IObservable<IReadOnlyList<PackageManifest>> DiscoverPackages(RepoSnapshot snapshot) =>
        NodeRepoPackageGraph.DiscoverPackages(snapshot);

    /// <summary>
    /// Orders the packages so every package installs AFTER the packages it references —
    /// <see cref="NodeRepoPackageGraph.OrderByDependencies"/>.
    /// </summary>
    /// <param name="packages">The discovered packages.</param>
    /// <param name="snapshot">The tree they were discovered in.</param>
    public static IReadOnlyList<PackageManifest> OrderByDependencies(
        IReadOnlyList<PackageManifest> packages, RepoSnapshot snapshot) =>
        NodeRepoPackageGraph.OrderByDependencies(packages, snapshot);

    /// <summary>
    /// The package-id → direct-dependency-ids map — <see cref="NodeRepoPackageGraph.DependencyMap"/>,
    /// the ONE reader of the three edge kinds.
    /// </summary>
    /// <param name="packages">The discovered packages.</param>
    /// <param name="snapshot">The tree they were discovered in.</param>
    public static ImmutableDictionary<string, ImmutableHashSet<string>> DependencyMap(
        IReadOnlyList<PackageManifest> packages, RepoSnapshot snapshot) =>
        NodeRepoPackageGraph.DependencyMap(packages, snapshot);
}
