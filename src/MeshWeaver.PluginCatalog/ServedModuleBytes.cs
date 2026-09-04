using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MeshWeaver.Compiler;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// WHICH BUILD of a package's compiled module the registry hands a consumer — the "one producer in
/// TIME" decision (#3244).
///
/// <para>🚨 <b>The asymmetry this closes.</b> A bundle download resolves its NodeType assemblies
/// through each type's <c>Release</c> node for exactly the CALLER's framework identity (#1751), and
/// resolved the module section from the registry's own <c>modules/</c> shelf for nobody's — the
/// shelf holds whatever the module's OWN lane published last, under a content version that does not
/// move when a rebuild changes the bytes (MeshWeaver.Plugins#931). So one archive could carry
/// assemblies sealed against <c>mvid:A</c> beside a module whose bytes are <c>mvid:B</c>, and the
/// consumer's boot seeder rightly declined every one of them — <i>"dependency record mismatch —
/// built against mvid:…, live is mvid:…"</i> — while every set-level check passed, because
/// <see cref="ReleaseAvailability"/> compares the SEALED module set and the instance runs the
/// SHELF's. Two internally-consistent producers, disagreeing across time.</para>
///
/// <para><b>The rule is evidence, not a preference order.</b> The assemblies being served carry, per
/// NodeType, the id they were compiled against (<see cref="CompiledDependencies"/>); for an
/// installed module that id is <c>mvid:</c> + the module's raw MVID. So the archive states which
/// build it needs, and this picks the bytes that ARE that build: the shelf when the shelf is it
/// (the healthy case, and no extra read beyond one PE header), otherwise the module bundle the
/// SEALED publication for the caller's identity composed — the exact bytes those assemblies were
/// built against (MeshWeaver#2698/#2707), which the registry already mounts and already serves at
/// <c>…/prebuilt/{identity}/{source}/modules/{bundle}</c>.</para>
///
/// <para>🚨 <b>Why the decision lives on the SERVER.</b> The alternative — the instance fetching its
/// module bytes from the prebuilt route directly — needs a whole-source grant
/// (<c>&lt;source&gt;/*</c>) on every installation, and a whole-source grant deliberately bypasses
/// plan tiering: <i>"a sealed publication carries every plan's bundles, so a plan-scoped
/// <c>&lt;source&gt;/*@pro</c> … must never fetch the publication whole"</i>. Deciding here needs no
/// new grant, no new credential and no client change, so the whole fleet converges the moment the
/// registry rolls — and it is right in the case a preference order gets wrong, a registry that
/// COMPILED its own NodeTypes (its records then name its own live module, and the shelf is the
/// correct answer).</para>
///
/// <para><b>What is deliberately unchanged.</b> When nothing being served records an id for the
/// module, when the shelf already IS the recorded build, or when no sealed publication for the
/// caller's identity carries a matching build, the shelf's bytes are served exactly as before.
/// The last of those three is not a silent fallback: it carries a
/// <see cref="ServedModule.Divergence"/> naming both builds, which the caller reports as a MISS on
/// the wire and in the log, so "the registry cannot satisfy this lane" stops being a fact only the
/// consumer's decline could reveal.</para>
/// </summary>
public static class ServedModuleBytes
{
    /// <summary>The seal's own naming for a package's module bundle, mirrored from
    /// <c>.github/scripts/compose-sealed-modules.sh</c> (<c>&lt;lower(package)&gt;.module.nupkg</c>)
    /// — the first candidate tried, before the identity's module index is scanned.</summary>
    public static string BundleNameFor(string packageId) =>
        packageId.ToLowerInvariant() + ".module.nupkg";

    /// <summary>
    /// The build the assemblies in this bundle say they need for one module: the single
    /// <c>mvid:</c> id their dependency records agree on, or a named
    /// <see cref="RecordedModuleId.Disagreement"/> when they do not.
    /// </summary>
    /// <param name="dependencyRecords">Each served assembly's <c>CompiledDependencies</c> record
    /// (null entries — an assembly recorded before records existed — simply contribute nothing).</param>
    /// <param name="moduleName">The module's entry-assembly simple name.</param>
    public static RecordedModuleId RecordedFor(
        IEnumerable<IReadOnlyDictionary<string, string>?> dependencyRecords, string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
            return new RecordedModuleId(null, null);

        var found = ImmutableHashSet<string>.Empty;
        foreach (var record in dependencyRecords)
        {
            if (record is null
                || !record.TryGetValue(moduleName, out var id)
                || !id.StartsWith(CompiledDependencies.MvidScheme, StringComparison.Ordinal))
                continue;
            found = found.Add(id);
        }

        return found.Count switch
        {
            0 => new RecordedModuleId(null, null),
            1 => new RecordedModuleId(found.Single(), null),
            // Two builds recorded INSIDE one bundle: no module choice can satisfy both, so this is
            // named rather than resolved — picking either would decline the other's NodeTypes.
            _ => new RecordedModuleId(
                null,
                $"the assemblies in this bundle were built against {found.Count} different builds of "
                + $"'{moduleName}' ({string.Join(", ", found.OrderBy(x => x, StringComparer.Ordinal))}) "
                + "— no single module can satisfy them"),
        };
    }

    /// <summary>
    /// The module bytes to serve, and where they came from.
    /// </summary>
    /// <param name="moduleName">The module's entry-assembly simple name.</param>
    /// <param name="packageId">The package whose bundle is being assembled — names the seal's
    /// module bundle (<see cref="BundleNameFor"/>).</param>
    /// <param name="shelfFiles">The registry's own closure paths for the module, entry DLL first
    /// (<see cref="ModuleBundleSource.Collect"/>). Empty means there is nothing to serve.</param>
    /// <param name="shelfAssets">The shelf's static web assets, module-relative path and full path.</param>
    /// <param name="recorded">What the served assemblies record for this module
    /// (<see cref="RecordedFor"/>).</param>
    /// <param name="publishedRoot">The published bundle root
    /// (<see cref="PublishedBundleCatalogue.PublishedRootConfigKey"/>), or null when this deployment
    /// consumes no CI bakes — then only the shelf exists.</param>
    /// <param name="identity">The CALLER's framework build identity.</param>
    /// <param name="logger">Diagnostics.</param>
    public static ServedModule Resolve(
        string? moduleName,
        string packageId,
        IReadOnlyList<string> shelfFiles,
        IReadOnlyList<(string RelativePath, string FullPath)> shelfAssets,
        RecordedModuleId recorded,
        string? publishedRoot,
        string? identity,
        ILogger? logger = null)
    {
        var shelf = FromShelf(shelfFiles, shelfAssets);

        // Nothing to serve, or nothing that binds it: byte-for-byte the pre-#3244 answer.
        if (string.IsNullOrWhiteSpace(moduleName) || shelf.Files.Count == 0)
            return shelf;
        if (recorded.Disagreement is not null)
            return shelf with { Divergence = recorded.Disagreement };
        if (recorded.Mvid is null)
            return shelf;

        var shelfMvid = ShelfMvid(shelfFiles, moduleName!, logger);
        if (string.Equals(shelfMvid, recorded.Mvid, StringComparison.Ordinal))
            return shelf;

        // The shelf is NOT the build these assemblies were compiled against. The sealed publication
        // for this identity is, by construction — it is what the bake composed with `--module`.
        foreach (var candidate in SealedCandidates(publishedRoot, identity, packageId, logger))
        {
            if (!string.Equals(candidate.Mvid, recorded.Mvid, StringComparison.Ordinal))
                continue;
            if (FromSealed(candidate, moduleName!, logger) is { } served)
                return served;
        }

        return shelf with
        {
            Divergence =
                $"the assemblies served here were built against module '{moduleName}' {recorded.Mvid}, "
                + $"but this registry's shelf holds {shelfMvid ?? "a build whose MVID could not be read"} "
                + $"and no publication sealed for framework identity '{identity}' carries a matching "
                + "build — the consumer will decline every NodeType binding it (dependency record "
                + "mismatch). Republish the package's module bundle for this identity, or rebake it "
                + "against the module the registry serves",
        };
    }

    private static ServedModule FromShelf(
        IReadOnlyList<string> files,
        IReadOnlyList<(string RelativePath, string FullPath)> assets) =>
        new(
            files.Select(path =>
                    new ServedModuleFile(Path.GetFileName(path), () => File.OpenRead(path)))
                .ToArray(),
            assets.Select(asset =>
                    new ServedModuleAsset(asset.RelativePath, () => File.OpenRead(asset.FullPath)))
                .ToArray(),
            "this registry's own modules/ shelf",
            null);

    /// <summary>
    /// One sealed module bundle inflated into a servable module: its <c>meshweaver/modules/</c>
    /// entries and its <c>meshweaver/moduleassets/</c> tree, read from the ARCHIVE rather than from
    /// the manifest's claim — what lands is what the bundle contains (the #3221 rule). Null when the
    /// archive turns out not to carry the entry after all, which is a reason to keep looking rather
    /// than to fail the download.
    /// </summary>
    private static ServedModule? FromSealed(SealedModuleCandidate candidate, string moduleName, ILogger? logger)
    {
        try
        {
            var bytes = File.ReadAllBytes(candidate.Path);
            using var buffer = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

            var files = new List<ServedModuleFile>();
            var entryName = moduleName + ".dll";
            foreach (var entry in archive.Entries
                         .Where(e => e.FullName.StartsWith(
                             NuGetPackageWriter.ModuleFolder + "/", StringComparison.Ordinal))
                         .Where(e => !e.FullName[(NuGetPackageWriter.ModuleFolder.Length + 1)..].Contains('/'))
                         .OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                var name = entry.FullName[(NuGetPackageWriter.ModuleFolder.Length + 1)..];
                if (name.Length == 0)
                    continue;
                var content = Inflate(entry);
                // Entry DLL FIRST, the order ModuleBundleSource.Collect guarantees and the consumer's
                // landing relies on.
                if (string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase))
                    files.Insert(0, new ServedModuleFile(name, () => new MemoryStream(content, writable: false)));
                else
                    files.Add(new ServedModuleFile(name, () => new MemoryStream(content, writable: false)));
            }

            if (files.Count == 0
                || !string.Equals(files[0].FileName, entryName, StringComparison.OrdinalIgnoreCase))
                return null;

            var assetPrefix = NuGetPackageWriter.ModuleAssetFolder + "/";
            var assets = archive.Entries
                .Where(e => e.FullName.StartsWith(assetPrefix, StringComparison.Ordinal))
                .Where(e => e.FullName.Length > assetPrefix.Length)
                .OrderBy(e => e.FullName, StringComparer.Ordinal)
                .Select(e =>
                {
                    var content = Inflate(e);
                    return new ServedModuleAsset(
                        e.FullName[assetPrefix.Length..],
                        () => new MemoryStream(content, writable: false));
                })
                .ToArray();

            return new ServedModule(
                files,
                assets,
                $"the publication sealed for framework identity '{candidate.Identity}' by source "
                + $"'{candidate.Source}' ({candidate.BundleName})",
                null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or BadImageFormatException)
        {
            logger?.LogWarning(ex,
                "Served module bytes: sealed module bundle {Path} is unreadable — it cannot supply "
                + "'{Module}'", candidate.Path, moduleName);
            return null;
        }
    }

    private static byte[] Inflate(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var target = new MemoryStream();
        source.CopyTo(target);
        return target.ToArray();
    }

    /// <summary>
    /// The sealed module bundles under one identity that could carry <paramref name="packageId"/>'s
    /// module, cheapest first: the lane's own name (<see cref="BundleNameFor"/>) in each complete
    /// source, then every other bundle those sources sealed.
    ///
    /// <para>🚨 The broad scan runs ONLY when the shelf already disagreed — the healthy download
    /// never reaches here, so the O(sealed bundles) PE reads are the cost of a divergence, not of a
    /// serve. It exists because the naming convention is a contract of the pack lane, and a control
    /// that can only see the conventionally-named bundle would report "no matching build" for a
    /// package whose module the seal carries under another name.</para>
    /// </summary>
    private static IEnumerable<SealedModuleCandidate> SealedCandidates(
        string? publishedRoot, string? identity, string packageId, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(publishedRoot) || string.IsNullOrWhiteSpace(identity))
            yield break;
        var identityDirectory = Path.Combine(publishedRoot, identity);
        if (!Directory.Exists(identityDirectory))
            yield break;

        var preferred = BundleNameFor(packageId);
        var sources = new List<(string Directory, string Source, IReadOnlyList<string> Modules)>();
        foreach (var sourceDirectory in Directory.EnumerateDirectories(identityDirectory)
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
            var reading = PublishedBundleCatalogue.SealedModulesOf(sourceDirectory, logger);
            if (reading.Modules is { Count: > 0 })
                sources.Add((sourceDirectory, Path.GetFileName(sourceDirectory)!, reading.Modules));
        }

        foreach (var pass in new[] { true, false })
            foreach (var (directory, source, modules) in sources)
                foreach (var bundle in modules)
                {
                    var isPreferred = string.Equals(bundle, preferred, StringComparison.OrdinalIgnoreCase);
                    if (isPreferred != pass)
                        continue;
                    var path = Path.Combine(
                        directory, PublishedBundleCatalogue.ModulesDirectoryName, bundle);
                    string? mvid;
                    try
                    {
                        mvid = PublishedBundleCatalogue.SealedModuleAssembliesOf(path)
                            .FirstOrDefault(a => a.IsEntry).Mvid;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException
                                                   or BadImageFormatException or InvalidOperationException)
                    {
                        logger?.LogWarning(ex,
                            "Served module bytes: sealed module bundle {Path} could not be read for its "
                            + "entry MVID — it cannot be offered as a match", path);
                        continue;
                    }
                    if (mvid is not null)
                        yield return new SealedModuleCandidate(identity!, source, bundle, path, mvid);
                }
    }

    /// <summary>The MVID of the shelf's entry assembly, in the dependency-record spelling, or null
    /// when it cannot be read (a truncated volume, bytes that are not a PE) — read as "unknown",
    /// never as "matches".</summary>
    private static string? ShelfMvid(IReadOnlyList<string> shelfFiles, string moduleName, ILogger? logger)
    {
        var entry = shelfFiles.FirstOrDefault(f =>
            string.Equals(Path.GetFileName(f), moduleName + ".dll", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return null;
        try
        {
            using var stream = File.OpenRead(entry);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            return CompiledDependencies.MvidScheme
                   + metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("N");
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException)
        {
            logger?.LogWarning(ex,
                "Served module bytes: the shelf's '{Module}' at {Path} could not be read for its MVID",
                moduleName, entry);
            return null;
        }
    }

    private readonly record struct SealedModuleCandidate(
        string Identity, string Source, string BundleName, string Path, string Mvid);
}

/// <summary>What the assemblies of one bundle record for a module: the single build they agree on,
/// or the reason they do not.</summary>
/// <param name="Mvid">The <c>mvid:</c> id every record naming the module agrees on, or null when
/// none names it (nothing binds it) or they disagree.</param>
/// <param name="Disagreement">Why no single build can satisfy them, or null.</param>
public readonly record struct RecordedModuleId(string? Mvid, string? Disagreement);

/// <summary>One file of a module bundle's closure, and how to open it — a path on the shelf, an
/// inflated archive entry from a sealed publication.</summary>
public sealed record ServedModuleFile(string FileName, Func<Stream> Open);

/// <summary>One static web asset of a module, keeping the module-relative path a component's
/// <c>_content/&lt;pack&gt;/…</c> URL asks for.</summary>
public sealed record ServedModuleAsset(string RelativePath, Func<Stream> Open);

/// <summary>
/// The module bytes a bundle download carries, where they came from, and — when the registry could
/// not supply the build the bundle's own assemblies record — what disagreed.
/// </summary>
/// <param name="Files">The closure, entry DLL first.</param>
/// <param name="Assets">The module's static web assets.</param>
/// <param name="Provenance">Human-readable origin, for the log line and the manifest.</param>
/// <param name="Divergence">Null when these bytes ARE the recorded build (or nothing recorded one);
/// otherwise the named reason the consumer will decline, reported as a miss rather than discovered
/// downstream.</param>
public sealed record ServedModule(
    IReadOnlyList<ServedModuleFile> Files,
    IReadOnlyList<ServedModuleAsset> Assets,
    string Provenance,
    string? Divergence);
