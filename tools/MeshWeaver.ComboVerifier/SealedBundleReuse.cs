using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.GitSync;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;

namespace MeshWeaver.ComboVerifier;

/// <summary>
/// Where the combo's sealed NodeType bundles come from: one directory per producing source, each
/// holding the <c>*.zip</c> bundles that source's publication SEALED for the candidate's framework
/// identity (<c>compose-sealed-modules.sh --bundles-out</c>, MeshWeaver#4413).
/// </summary>
/// <param name="Directories">One directory per source, in the order given.</param>
/// <param name="FrameworkIdentity">The candidate's framework identity — the address every reused
/// bundle must carry, and the identity the gate's seed is keyed to.</param>
public sealed record SealedBundleSource(ImmutableArray<string> Directories, string FrameworkIdentity);

/// <summary>
/// What the combo bake rebuilds and what it takes, unchanged, from the producing repositories' seals.
/// </summary>
/// <param name="FrameworkIdentity">The identity every reused bundle carries.</param>
/// <param name="Reused">Package id → the sealed bundle the gate consumes for it.</param>
/// <param name="Rebuilt">Package id → why its sealed bundle cannot stand (no seal, different content,
/// or a package it reads sources from is rebuilt).</param>
/// <param name="Staged">What the bake mounts: every rebuilt package plus everything it needs
/// installed (the merged dependency closure), dependencies first in no particular order — the same
/// "the mount is the filter" rule a narrowed publish-bake uses.</param>
/// <param name="SealProblems">Every fetched seal file that could not be read as a bundle, and every
/// seal directory that was not there — each one reuses nothing, and is named rather than skipped.</param>
/// <param name="NoNodeTypes">Packages that declare no NodeType of their own: nothing to compile, so
/// no bake writes a bundle for them and none is needed. They are neither reused nor rebuilt, and
/// are staged only when a rebuilt package needs them installed.</param>
public sealed record SealedBundlePlan(
    string FrameworkIdentity,
    ImmutableSortedDictionary<string, string> Reused,
    ImmutableSortedDictionary<string, string> Rebuilt,
    ImmutableSortedSet<string> Staged,
    ImmutableArray<string> SealProblems,
    ImmutableSortedSet<string> NoNodeTypes)
{
    /// <summary>True when every package's sealed bytes stand, so there is nothing to compile.</summary>
    public bool BakesNothing => Staged.IsEmpty;

    /// <summary>The plan as the lines the run log carries — counts first, then every rebuilt
    /// package with its reason, so the first real run measures what reuse bought.</summary>
    public IEnumerable<string> Describe()
    {
        yield return $"sealed bundles: {Reused.Count} package(s) reuse the producing repositories' sealed "
                     + $"bytes for identity {FrameworkIdentity}; {Rebuilt.Count} rebuilt; the bake mounts "
                     + $"{Staged.Count} package(s); {NoNodeTypes.Count} declare no NodeType (nothing to bake)"
                     + (BakesNothing ? " — nothing to compile." : ".");
        foreach (var problem in SealProblems)
            yield return $"  ⚠ {problem}";
        foreach (var (package, reason) in Rebuilt)
            yield return $"  rebuilt {package}: {reason}";
    }
}

/// <summary>
/// 🚨 <b>The combo consumes the producing repositories' SEALED bundles instead of re-baking them
/// (MeshWeaver#4413, owner decision option C, first half).</b> Every slot of every combo run used to
/// compile the instance's whole tree from source — 218 NodeTypes, 9.0 of a 25-minute budget on memex
/// (run 43319782317) — although each of those types is already baked and sealed, for this very
/// framework identity, by its producing repository's own publish lane. The build architecture's rule
/// is that outputs are content-addressed and an unchanged input is never recompiled
/// (<c>Doc/Architecture/ModuleBuildArchitecture</c>); the combo was the one bake that ignored it.
///
/// <para><b>When a sealed bundle stands for a package.</b> All four, or the package is rebuilt and
/// the reason is printed:</para>
/// <list type="number">
///   <item>(a package that declares NO NodeType of its own is outside this list: it has nothing to
///     compile, so no bake writes it a bundle and none is needed — see
///     <see cref="SealedBundlePlan.NoNodeTypes"/>. The first run after the reuse landed, 43586622051,
///     counted such packages as "no fetched seal carries a bundle for it" and staged each one WITH its
///     install closure, so packages whose seals stood were compiled again);</item>
///   <item>exactly one fetched seal carries a bundle for the package, keyed to the candidate's
///     framework identity, with assemblies and no producer misses;</item>
///   <item>the bundle's node-definition content (the package tree the producer baked, which every
///     bake writes into its bundle) is BYTE-FOR-BYTE the package as the combo materialised it — the
///     same file set, the same bytes. That is the content address: the instance may run an older
///     commit than the one sealed, and then the seal is not its build;</item>
///   <item>no package it reads SOURCES from (<see cref="NodeRepoPackageGraph.SourceDependencyMap"/>
///     — a <c>shared=@Other/…</c> include puts another package's bytes into this one's assembly) is
///     rebuilt, transitively. A dependency that carries no bundle at all cannot be shown equal and
///     counts as rebuilt — conservative, never a guess.</item>
/// </list>
///
/// <para><b>What the bake then compiles.</b> Only the rebuilt packages and what they need installed
/// (<see cref="NodeRepoPackageGraph.DependencyMap"/>, every edge kind), copied into a staging tree
/// beside the work root and mounted in its place — exactly how a narrowed publish-bake narrows (its
/// mount is the filter). The seed the gate consumes is that bake plus every reused bundle the bake
/// did not itself produce.</para>
///
/// <para><b>Safety net, not the decision.</b> The gate's seeder still validates every reused bundle
/// per NodeType — framework identity, source fingerprint, dependency record — exactly as a portal
/// does, and a decline makes the gate's bake-consumption postcondition RED by name. So a reuse this
/// plan got wrong is loud, never a green over bytes nobody judged.</para>
/// </summary>
public static class SealedBundleReuse
{
    /// <summary>Directories no package tree's content includes — the tester's sweep skips the same
    /// names (<c>LocalNodeRepo</c>), so a producer's bundle never carries them.</summary>
    private static readonly ImmutableHashSet<string> ExcludedDirectories =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            ".git", ".github", ".claude", ".worktrees", "node_modules", "bin", "obj");

    /// <summary>Where the staging tree of the packages the bake compiles is written: beside the work
    /// root, never inside it (the tester discovers packages under the root).</summary>
    /// <param name="workRoot">The materialised combo.</param>
    public static string StageRootFor(string workRoot) => Path.GetFullPath(workRoot) + ".stage";

    /// <summary>
    /// Decides, per package of the materialised combo, whether a sealed bundle stands for it.
    /// Blocking file I/O — call it from the process/file pool.
    /// </summary>
    /// <param name="workRoot">The materialised combo.</param>
    /// <param name="source">The fetched seals.</param>
    public static SealedBundlePlan Plan(string workRoot, SealedBundleSource source)
    {
        var root = Path.GetFullPath(workRoot);
        var tree = ReadTree(root);
        var packages = Discover(tree.Snapshot, root);
        var (sealedBundles, problems) = ReadSeals(source);

        var rebuilt = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var candidates = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var typeless = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            // Nothing to compile, so nothing a seal could carry: "no bundle" is this package's normal
            // state, never evidence that it changed. Counting it as rebuilt staged it as a ROOT of the
            // bake together with its whole install closure, recompiling packages whose seals stood.
            if (!NodeRepoPackageGraph.DefinesNodeTypes(package, tree.Snapshot))
            {
                typeless.Add(package.Id);
                continue;
            }
            var (bundle, reason) = Decide(package, tree, sealedBundles, source.FrameworkIdentity);
            if (bundle is not null)
                candidates[package.Id] = bundle;
            else
                rebuilt[package.Id] = reason;
        }

        // A package whose included sources come from a rebuilt package is rebuilt too — to a fixed
        // point, because an include of an include changes the bytes just the same.
        var sources = NodeRepoPackageGraph.SourceDependencyMap(packages, tree.Snapshot);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var id in candidates.Keys.ToList())
            {
                var stale = sources.GetValueOrDefault(id, []).Where(rebuilt.ContainsKey)
                    .OrderBy(d => d, StringComparer.Ordinal).FirstOrDefault();
                // 🚨 A package with no NodeType can still hold sources another package includes. No
                // seal carries ITS bytes, so nothing shows the instance's copy is the one the
                // includer's seal was compiled from: conservative, exactly as for a rebuilt one.
                var unproven = stale is null
                    ? sources.GetValueOrDefault(id, []).Where(typeless.Contains)
                        .OrderBy(d => d, StringComparer.Ordinal).FirstOrDefault()
                    : null;
                if (stale is null && unproven is null)
                    continue;
                candidates.Remove(id);
                rebuilt[id] = stale is not null
                    ? $"its sealed bundle includes sources of '{stale}', which is rebuilt"
                    : $"its sealed bundle includes sources of '{unproven}', which declares no NodeType, "
                      + "so no seal shows the instance's copy is the one it was compiled from";
                changed = true;
            }
        }

        // The mount: everything rebuilt, plus whatever any of it needs installed, transitively.
        var all = NodeRepoPackageGraph.DependencyMap(packages, tree.Snapshot);
        var staged = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(rebuilt.Keys);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!staged.Add(id))
                continue;
            foreach (var dependency in all.GetValueOrDefault(id, []))
                pending.Push(dependency);
        }

        return new SealedBundlePlan(
            source.FrameworkIdentity, candidates.ToImmutable(), rebuilt.ToImmutable(), staged.ToImmutable(),
            problems, typeless.ToImmutable());
    }

    /// <summary>
    /// Writes the staging tree the bake mounts — every <see cref="SealedBundlePlan.Staged"/> package,
    /// copied from the work root — and returns its path. A stale staging tree is removed first, so a
    /// package an earlier run staged can never be compiled into this run's seed.
    /// </summary>
    /// <param name="workRoot">The materialised combo.</param>
    /// <param name="plan">The plan to stage.</param>
    public static string Stage(string workRoot, SealedBundlePlan plan)
    {
        var root = Path.GetFullPath(workRoot);
        var stage = StageRootFor(root);
        if (Directory.Exists(stage))
            Directory.Delete(stage, recursive: true);
        Directory.CreateDirectory(stage);
        foreach (var package in plan.Staged)
            CopyTree(Path.Combine(root, package), Path.Combine(stage, package));
        return stage;
    }

    /// <summary>
    /// Completes the seed: copies every reused bundle the bake did not itself produce into
    /// <paramref name="bakeRoot"/>, and keys the directory to the plan's identity when the bake
    /// compiled nothing (it then wrote no identity file). Returns null on success, else why the
    /// directory cannot be one seed.
    /// </summary>
    /// <param name="bakeRoot">The run's bake directory.</param>
    /// <param name="plan">The plan whose reused bundles complete it.</param>
    public static string? CompleteSeed(string bakeRoot, SealedBundlePlan plan)
    {
        var identityFile = Path.Combine(bakeRoot, DockerImageGate.FrameworkMvidFile);
        if (File.Exists(identityFile))
        {
            var baked = File.ReadAllText(identityFile).Trim();
            if (baked.Length > 0 && !string.Equals(baked, plan.FrameworkIdentity, StringComparison.Ordinal))
                return $"the bake sealed framework identity '{baked}' but the reused bundles are keyed "
                       + $"to '{plan.FrameworkIdentity}' — one seed cannot hold both, and the gate would "
                       + "decline one half of it.";
        }
        else if (plan.BakesNothing)
            File.WriteAllText(identityFile, plan.FrameworkIdentity);

        var bakedPackages = Directory.EnumerateFiles(bakeRoot, "*.zip", SearchOption.TopDirectoryOnly)
            .Select(f => BundleReader.ReadManifest(f)?.Plugin)
            .OfType<string>()
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (package, bundle) in plan.Reused)
        {
            if (bakedPackages.Contains(package))
                continue;
            var target = Path.Combine(bakeRoot, $"sealed-{package}.zip");
            File.Copy(bundle, target, overwrite: false);
        }
        return null;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The tree, the seals, the decision
    // ══════════════════════════════════════════════════════════════════════════

    private sealed record Tree(RepoSnapshot Snapshot, ImmutableDictionary<string, byte[]> Bytes);

    private sealed record SealedBundle(string File, BundleReader.Manifest Manifest,
        IReadOnlyList<BundleReader.ContentFile> Content);

    /// <summary>The sealed bundle that stands for <paramref name="package"/> and an empty reason, or
    /// no bundle and the reason it is rebuilt — one value, so "reused" and "why not" cannot disagree.</summary>
    private static (string? Bundle, string Reason) Decide(
        PackageManifest package, Tree tree,
        ImmutableDictionary<string, ImmutableList<SealedBundle>> seals, string identity)
    {
        if (!seals.TryGetValue(package.Id, out var offered) || offered.IsEmpty)
            return Rebuild("no fetched seal carries a bundle for it");
        if (offered.Count > 1)
            return Rebuild($"{offered.Count} seals carry a bundle for it ({string.Join(", ", offered.Select(b => b.File))}) "
                   + "— which one is this instance's build is not decidable here");
        var sealedBundle = offered[0];
        var manifest = sealedBundle.Manifest;
        if (!string.Equals(manifest.FrameworkMvid, identity, StringComparison.Ordinal))
            return Rebuild($"its sealed bundle is keyed to '{manifest.FrameworkMvid}', not the candidate's '{identity}'");
        if (manifest.Assemblies is not { Count: > 0 })
            return Rebuild("its sealed bundle carries no assembly");
        if (manifest.Misses is { Count: > 0 } misses)
            return Rebuild($"its sealed bundle records {misses.Count} producer miss(es)");
        if (sealedBundle.Content.Count == 0)
            return Rebuild("its sealed bundle carries no node-definition content to compare the tree against");
        if (ContentDifference(package, tree, sealedBundle.Content) is { } difference)
            return Rebuild($"the sealed build is not this tree's ({difference})");
        return (sealedBundle.File, "");

        static (string? Bundle, string Reason) Rebuild(string reason) => (null, reason);
    }

    /// <summary>Null when the bundle's content is byte-for-byte the package as materialised; else the
    /// first difference, named.</summary>
    private static string? ContentDifference(
        PackageManifest package, Tree tree, IReadOnlyList<BundleReader.ContentFile> content)
    {
        var prefix = (package.SourceFolder ?? package.Id) + "/";
        var local = tree.Bytes
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToImmutableSortedDictionary(kv => kv.Key[prefix.Length..], kv => kv.Value, StringComparer.Ordinal);
        var sealedFiles = content.ToImmutableSortedDictionary(
            f => f.RelativePath, f => f.Bytes, StringComparer.Ordinal);
        if (local.Keys.Except(sealedFiles.Keys).FirstOrDefault() is { } onlyHere)
            return $"'{onlyHere}' is in the tree and not in the seal";
        if (sealedFiles.Keys.Except(local.Keys).FirstOrDefault() is { } onlySealed)
            return $"'{onlySealed}' is in the seal and not in the tree";
        foreach (var (path, bytes) in local)
            if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(SHA256.HashData(sealedFiles[path])))
                return $"'{path}' differs";
        return null;
    }

    private static (ImmutableDictionary<string, ImmutableList<SealedBundle>> Bundles, ImmutableArray<string> Problems)
        ReadSeals(SealedBundleSource source)
    {
        var byPackage = ImmutableDictionary.CreateBuilder<string, ImmutableList<SealedBundle>>(
            StringComparer.OrdinalIgnoreCase);
        var problems = ImmutableArray.CreateBuilder<string>();
        foreach (var directory in source.Directories)
        {
            if (!Directory.Exists(directory))
            {
                problems.Add($"seal directory '{directory}' does not exist — it reuses nothing");
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                BundleReader.Manifest? manifest;
                IReadOnlyList<BundleReader.ContentFile> content;
                try
                {
                    (manifest, content) = BundleReader.ReadContent(File.ReadAllBytes(file));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                               or System.Text.Json.JsonException)
                {
                    // An unreadable seal reuses nothing: the package it would have covered is
                    // rebuilt, and the file is NAMED here so the rebuild is attributable.
                    problems.Add($"'{file}' is not a readable bundle ({ex.GetType().Name}: {ex.Message}) — it reuses nothing");
                    continue;
                }
                if (manifest?.Plugin is not { Length: > 0 } plugin)
                {
                    problems.Add($"'{file}' names no package in its manifest — it reuses nothing");
                    continue;
                }
                byPackage[plugin] = byPackage.GetValueOrDefault(plugin, [])
                    .Add(new SealedBundle(file, manifest, content));
            }
        }
        return (byPackage.ToImmutable(), problems.ToImmutable());
    }

    private static IReadOnlyList<PackageManifest> Discover(RepoSnapshot snapshot, string root)
    {
        // NodeRepoPackageGraph composes over an in-memory snapshot and emits synchronously on
        // Subscribe; this reads it without a blocking bridge and refuses if that stops being true.
        IReadOnlyList<PackageManifest>? packages = null;
        Exception? failure = null;
        using (NodeRepoPackageGraph.DiscoverPackages(snapshot).Subscribe(p => packages = p, ex => failure = ex))
        {
        }
        if (failure is not null)
            throw new InvalidOperationException(
                $"package discovery under '{root}' failed: {failure.Message}", failure);
        return packages ?? throw new InvalidOperationException(
            $"package discovery under '{root}' did not complete synchronously over an in-memory snapshot.");
    }

    private static Tree ReadTree(string root)
    {
        var files = ImmutableList.CreateBuilder<RepoFile>();
        var bytes = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.Ordinal);
        Sweep(root, root, files, bytes);
        return new Tree(
            new RepoSnapshot("combo", files.ToImmutable().Sort((a, b) => string.CompareOrdinal(a.Path, b.Path))),
            bytes.ToImmutable());
    }

    private static void Sweep(string root, string directory,
        ImmutableList<RepoFile>.Builder files, ImmutableDictionary<string, byte[]>.Builder bytes)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            // Root-level files (the assembler's manifest) belong to no package.
            if (!relative.Contains('/', StringComparison.Ordinal))
                continue;
            var content = File.ReadAllBytes(file);
            bytes[relative] = content;
            files.Add(TryDecodeUtf8(content, out var text)
                ? new RepoFile(relative, text)
                : new RepoFile(relative, string.Empty, content));
        }
        foreach (var sub in Directory.EnumerateDirectories(directory))
            if (!ExcludedDirectories.Contains(Path.GetFileName(sub)))
                Sweep(root, sub, files, bytes);
    }

    private static bool TryDecodeUtf8(byte[] bytes, out string text)
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

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var sub in Directory.EnumerateDirectories(from))
            CopyTree(sub, Path.Combine(to, Path.GetFileName(sub)));
    }
}
