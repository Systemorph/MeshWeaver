using System.IO.Compression;
using System.Text.Json;

namespace MeshWeaver.Plugin.Packaging;

/// <summary>
/// Reads what <see cref="NuGetPackageWriter"/> wrote. Lives beside the writer so the two cannot
/// drift: the folder names, the manifest entry and the node-path mapping are stated once.
/// </summary>
public static class BundleReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The bundle manifest: which node each assembly implements, and what framework they
    /// were compiled against.</summary>
    /// <param name="Plugin">Plugin id the bundle is for.</param>
    /// <param name="Version">The plugin's released SemVer.</param>
    /// <param name="FrameworkMvid">MVID of the MeshWeaver.Graph assembly that compiled these bytes.
    /// Null when the producer recorded none, which a consumer must treat as "cannot adopt".</param>
    /// <param name="Assemblies">One entry per compiled NodeType.</param>
    /// <param name="Module">The compiled MODULE this bundle carries (#1664), or null for a
    /// NodeType-only bundle. A MIXED package (content + NodeTypes + a module) carries both
    /// <paramref name="Assemblies"/> and this in ONE bundle — one reader, one transport.</param>
    /// <param name="Architecture">The portable RID the <paramref name="FrameworkMvid"/> lane belongs
    /// to (#1751), or null from a producer that predates the link. DIAGNOSTIC, never a second gate:
    /// the identity is the proof and already folds the architecture in. It is here so a decline can
    /// say "no arm64 lane" instead of only printing two hashes.</param>
    /// <param name="Misses">NodeTypes the producer could NOT resolve an assembly for in the
    /// requested lane, each with its reason (#1751). 🚨 Carried so the miss is COUNTABLE on the
    /// consumer too — a bundle that quietly arrives with fewer assemblies than the package has types
    /// is indistinguishable from a complete one, and "adopted N" is the only evidence the whole
    /// distribution lane works.</param>
    /// <param name="SourceIncluded">Whether <paramref name="Content"/> includes the package's C#
    /// SOURCE. Null on a legacy bundle (unknown). 🚨 This is a DECLARATION, not an inference from
    /// the file list, because the two answer different questions: a package may legitimately
    /// contain no C# at all, and a consumer that cannot resolve a <c>shared=@…</c> include needs to
    /// know whether the source was WITHHELD (its build cannot succeed and should say so) or simply
    /// never existed (nothing is wrong). Inferring "no .cs files ⇒ source withheld" would report
    /// the first for the second.</param>
    /// <param name="Content">The package's own NODE DEFINITION files, relative to
    /// <see cref="NuGetPackageWriter.ContentFolder"/> — present when the producer shipped the
    /// package tree alongside its bytes, null for an assemblies-only bundle. A consumer that means
    /// to USE this package as an upstream needs these: the bytes only stamp nodes that already
    /// exist, so without the definitions there is nothing to stamp.</param>
    public sealed record Manifest(
        string? Plugin,
        string? Version,
        string? FrameworkMvid,
        IReadOnlyList<AssemblyRef>? Assemblies,
        ModuleRef? Module = null,
        string? Architecture = null,
        IReadOnlyList<string>? Misses = null,
        IReadOnlyList<string>? Content = null,
        bool? SourceIncluded = null)
    {
        /// <summary>
        /// 🚨 <b>The PRODUCING REPOSITORY'S COMMIT these bytes were built from</b> (#4158) — the
        /// producer's own statement, carried to the consumer so a landed module can say what it was
        /// built from rather than only what its file looks like.
        ///
        /// <para>Everything else the consumer has is a property of the FILE: the MVID it reads out
        /// of the PE and the last-write time of the copy on its volume. Both read "newest" for a
        /// bundle that is the newest on the volume and carries types from last week — measured on
        /// memex.meshweaver.cloud 2026-09-10 (MeshWeaver.Plugins#1585), where that pairing cost a
        /// night of diagnosis and three RefreshModules before <c>/health</c> showed the bundle had
        /// never been adopted at all.</para>
        ///
        /// <para><c>null</c> when the producer recorded none — which every consumer must print and
        /// store as an explicit "unrecorded", never fill in from the version, the architecture or
        /// the file. DIAGNOSTIC: nothing gates on it, and nothing may start to, or a bundle from an
        /// older packer would stop landing over a field that is allowed to be absent.</para>
        ///
        /// <para>An INIT property, not a tenth primary-constructor parameter — see
        /// <see cref="AssemblyRef.SourceFingerprint"/> for why.</para>
        /// </summary>
        public string? SourceCommit { get; init; }
    }

    /// <summary>One assembly and the NodeType it implements.</summary>
    /// <param name="NodePath">Mesh path of the NodeType — the key a consumer re-seeds under.</param>
    /// <param name="Assembly">File name inside <see cref="NuGetPackageWriter.AssemblyFolder"/>.</param>
    /// <param name="Dependencies">The producer's per-type dependency record (#1707 slice 2), or
    /// null for a legacy bundle.</param>
    public sealed record AssemblyRef(
        string NodePath, string Assembly, IReadOnlyDictionary<string, string>? Dependencies = null)
    {
        /// <summary>
        /// The producer's CONTENT fingerprint of the sources these bytes were compiled from
        /// (#2813) — see <see cref="Payload.SourceFingerprint"/>. Null for a legacy bundle.
        ///
        /// <para>An INIT property, not a fourth primary-constructor parameter: adding one replaces
        /// a public record's constructor signature and is a binary break across the fleet.</para>
        /// </summary>
        public string? SourceFingerprint { get; init; }

        /// <summary>
        /// The producer's source snapshot for these bytes — <c>BundleWriter.AssemblyEntry.SourceVersions</c>,
        /// Code-node path → version ticks. The VALUES are meaningless to a consumer (the tree bake
        /// writes zeros; a mesh producer's ticks are its own clock) and were skipped by every
        /// reader until MeshWeaver#4280; the KEY SET is the provenance — which source nodes the
        /// compile consumed — and is what <see cref="Payload.SourcePaths"/> carries to the seeder.
        /// Null for a producer that recorded none. An INIT property for the same binary-break
        /// reason as <see cref="SourceFingerprint"/>.
        /// </summary>
        public IReadOnlyDictionary<string, long>? SourceVersions { get; init; }

        /// <summary>The <c>@@</c>-include closure's resolved paths, sorted ordinal —
        /// <c>BundleWriter.AssemblyEntry.SourceIncludes</c> (MeshWeaver#4280). Null for a producer
        /// that recorded none.</summary>
        public IReadOnlyList<string>? SourceIncludes { get; init; }
    }

    /// <summary>The bundle's compiled-module declaration.</summary>
    /// <param name="AssemblyName">The module's entry-assembly name WITHOUT extension
    /// (e.g. <c>MeshWeaver.Social</c>) — the same identity <c>Modules:Assemblies</c> entries and
    /// <c>modules/&lt;name&gt;/</c> folders use.</param>
    /// <param name="Assemblies">File names inside <see cref="NuGetPackageWriter.ModuleFolder"/> —
    /// the module's private closure (for most modules just <c>&lt;AssemblyName&gt;.dll</c>).</param>
    /// <param name="MinMeshVersion">The module's declared platform FLOOR — the consumer's landing
    /// gate (a plain .NET assembly binding by simple name is compatible by API, expressed as a
    /// semver floor). Null = no constraint. The manifest-level <see cref="Manifest.FrameworkMvid"/>
    /// stays the NodeType lane's strict gate and, for the module, DIAGNOSTIC metadata only.</param>
    /// <param name="StaticAssets">Module-relative paths inside
    /// <see cref="NuGetPackageWriter.ModuleAssetFolder"/> — a view pack's <c>wwwroot</c> tree.</param>
    public sealed record ModuleRef(
        string? AssemblyName, IReadOnlyList<string>? Assemblies, string? MinMeshVersion = null,
        IReadOnlyList<string>? StaticAssets = null)
    {
        /// <summary>
        /// 🚨 Module-relative paths inside <see cref="NuGetPackageWriter.ModuleNativeFolder"/>, each
        /// of the shape <c>runtimes/&lt;rid&gt;/native/&lt;file&gt;</c> (#4126) — the RID-specific
        /// loadable payloads a module's own dependency graph declares. DECLARED, never enumerated:
        /// every other section of this bundle is manifest-driven for the same reason, and a
        /// folder-enumerating reader would adopt any stray entry a future writer places there.
        ///
        /// <para>An INIT property, not a fifth primary-constructor parameter. A parameter — even
        /// with a default — REPLACES the record's constructor signature, so every module already
        /// compiled against the 4-parameter one calls a constructor this build no longer has; the
        /// repo's <c>check-record-signatures.py</c> gate refuses it, and it caught exactly this
        /// change. <see cref="Manifest.SourceCommit"/> and
        /// <see cref="AssemblyRef.SourceFingerprint"/> are here for the same reason.</para>
        /// </summary>
        public IReadOnlyList<string>? NativeAssets { get; init; }
    }

    /// <summary>One landed-to-be module file: its name and bytes.</summary>
    /// <param name="FileName">File name as the manifest declared it.</param>
    /// <param name="Bytes">The file's bytes.</param>
    public sealed record ModuleFile(string FileName, byte[] Bytes);

    /// <summary>
    /// One static web asset travelling with a compiled module — a view pack's <c>wwwroot</c>.
    /// Kept apart from <see cref="ModuleFile"/> because the two are placed differently: an
    /// assembly is a FLAT file beside the entry DLL, while an asset keeps its relative path so
    /// <c>modules/&lt;name&gt;/wwwroot/leaflet/leaflet.js</c> survives the trip.
    /// </summary>
    /// <param name="RelativePath">Path under the module folder, forward slashes, e.g.
    /// <c>wwwroot/leaflet/leaflet.css</c>.</param>
    /// <param name="Bytes">The file's bytes, verbatim.</param>
    public sealed record ModuleAsset(string RelativePath, byte[] Bytes);

    /// <summary>One node-definition file recovered from the bundle.</summary>
    /// <param name="RelativePath">Path within the package tree, exactly as the manifest declared
    /// it — the layout a consumer recreates on disk.</param>
    /// <param name="Bytes">The file's bytes, verbatim.</param>
    public sealed record ContentFile(string RelativePath, byte[] Bytes);

    /// <summary>An assembly's bytes, ready to seed.</summary>
    /// <param name="NodePath">Mesh path of the NodeType these bytes implement.</param>
    /// <param name="Assembly">The compiled assembly.</param>
    /// <param name="Pdb">Symbols, when the bundle carried them.</param>
    /// <param name="Dependencies">The producer's per-type dependency record for these bytes
    /// (#1707 slice 2), joined from the manifest, or null for a legacy bundle.</param>
    public sealed record Payload(
        string NodePath, byte[] Assembly, byte[]? Pdb,
        IReadOnlyDictionary<string, string>? Dependencies = null)
    {
        /// <summary>
        /// 🚨 <b>The producer's CONTENT fingerprint of the sources these bytes were compiled
        /// from</b> (#2813), joined from the manifest. A consumer MUST pass it to
        /// <c>PrebuiltAssemblySeeder.Seed</c>: it is what lets the owning hub compare the bundle's
        /// source against the source THIS mesh holds and refuse an adoption that would run last
        /// week's code over today's data.
        ///
        /// <para>Null for a legacy bundle whose producer recorded none — that adoption still
        /// lands, as <c>BuildProvenance.AdoptedUnverified</c>.</para>
        ///
        /// <para>An INIT property, not a fifth primary-constructor parameter — see
        /// <see cref="AssemblyRef.SourceFingerprint"/>.</para>
        /// </summary>
        public string? SourceFingerprint { get; init; }

        /// <summary>
        /// 🚨 The source-node PATHS these bytes were compiled from — the key set of the manifest's
        /// <c>sourceVersions</c>, sorted ordinal (MeshWeaver#4280). Carried to
        /// <c>PrebuiltAssemblySeeder.Seed</c> and stamped as
        /// <c>NodeTypeDefinition.AdoptedSourcePaths</c> so the owner can tell a live source set
        /// that has MOVED past the bundle from one that has not finished ARRIVING: while a path the
        /// bundle was built from is not on the mesh yet, a differing live fingerprint is not a
        /// measurement of staleness, it is a snapshot of an install in flight. Null for a legacy
        /// bundle whose producer recorded no snapshot — the owner then judges on the fingerprint
        /// alone, exactly as before.
        /// </summary>
        public IReadOnlyList<string>? SourcePaths { get; init; }

        /// <summary>
        /// The <c>@@</c>-include paths the bytes were compiled with, sorted ordinal (MeshWeaver#4280)
        /// — the other half of the completeness witness. <see cref="SourcePaths"/> covers what the
        /// type's source QUERIES resolve; an include is matched by no query, lands as its own node,
        /// and is an ABSENT include (an answer) until it does — so the owner defers on this list
        /// exactly as on the paths. Null for a legacy bundle: the owner then judges without it.
        /// </summary>
        public IReadOnlyList<string>? SourceIncludes { get; init; }
    }

    /// <summary>
    /// Extracts the manifest and every assembly it names.
    ///
    /// <para>🚨 Driven by the MANIFEST, never by enumerating
    /// <see cref="NuGetPackageWriter.AssemblyFolder"/>. Today
    /// <see cref="NuGetPackageWriter.EntryPathFor"/> keeps the node path verbatim, so the two happen
    /// to agree — but that is the WRITER's guarantee to make and change, not something a reader may
    /// assume. Reading the mapping the producer wrote is the only way to know which NodeType an
    /// assembly belongs to; recovering it from a file name would seed correct bytes against the
    /// wrong node, which fails at activation with a TypeLoadException rather than anywhere near
    /// here.</para>
    ///
    /// <para>An assembly the manifest names but the archive lacks is SKIPPED, not fatal: the rest of
    /// the bundle stays usable and that one NodeType compiles as it would have anyway.</para>
    /// </summary>
    /// <param name="bundle">The archive bytes.</param>
    /// <returns>The manifest (null when the archive carries none) and the payloads found.</returns>
    public static (Manifest? Manifest, IReadOnlyList<Payload> Assemblies) Read(byte[] bundle)
    {
        using var buffer = new MemoryStream(bundle, writable: false);
        return Read(buffer);
    }

    /// <summary>
    /// <see cref="Read(byte[])"/> over a STREAM, optionally extracting only the node paths
    /// <paramref name="nodePaths"/> names.
    ///
    /// <para>🚨 Both parameters exist to keep bytes OUT of memory that nobody is going to use. A
    /// bundle's assemblies are the whole of its weight, and a consumer that has already adopted
    /// them needs none of it: reading a file as a stream avoids holding the compressed archive,
    /// and the filter avoids materialising decompressed assemblies the caller will discard. The
    /// boot-time seeder (<c>ShippedPrebuiltBundles</c>) asks
    /// <see cref="ReadManifest(string)"/> first and comes back here for the DEVIATING subset only
    /// — on a steady-state boot that subset is empty and this is never called at all.</para>
    ///
    /// <para><paramref name="nodePaths"/> <c>null</c> extracts everything, which is what a
    /// consumer holding a freshly-downloaded bundle wants.</para>
    /// </summary>
    /// <param name="bundle">The archive, positioned at its start.</param>
    /// <param name="nodePaths">Node paths to extract, or null for all of them.</param>
    /// <returns>The manifest (null when the archive carries none) and the payloads found.</returns>
    public static (Manifest? Manifest, IReadOnlyList<Payload> Assemblies) Read(
        Stream bundle, IReadOnlySet<string>? nodePaths = null)
    {
        using var archive = new ZipArchive(bundle, ZipArchiveMode.Read, leaveOpen: true);

        var manifest = ManifestOf(archive);
        if (manifest?.Assemblies is null)
            return (manifest, []);

        var payloads = new List<Payload>();
        foreach (var reference in manifest.Assemblies)
        {
            if (nodePaths is not null && !nodePaths.Contains(reference.NodePath))
                continue;

            var dll = archive.GetEntry($"{NuGetPackageWriter.AssemblyFolder}/{reference.Assembly}");

            if (dll is null)
                continue;

            var pdb = archive.GetEntry(
                $"{NuGetPackageWriter.AssemblyFolder}/"
                + Path.ChangeExtension(reference.Assembly, ".pdb"));

            payloads.Add(new Payload(
                reference.NodePath, ReadAll(dll), pdb is null ? null : ReadAll(pdb),
                reference.Dependencies)
            {
                // #2813 — carried, not derived. The manifest is the producer's statement about
                // which sources these bytes came from; a reader that dropped it would leave every
                // consumer unable to tell a current adoption from a stale one, which is exactly
                // the state this mechanism sat in while it looked implemented.
                SourceFingerprint = reference.SourceFingerprint,
                // #4280 — the key set of the producer's snapshot, so the owner can defer its
                // judgement while any of these has not landed on the mesh yet.
                SourcePaths = reference.SourceVersions is { Count: > 0 } versions
                    ? versions.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()
                    : null,
                SourceIncludes = reference.SourceIncludes is { Count: > 0 } includes
                    ? includes.OrderBy(k => k, StringComparer.Ordinal).ToList()
                    : null,
            });
        }

        return (manifest, payloads);
    }

    /// <summary>
    /// The bundle's MANIFEST alone — every assembly's node path, dependency record and the
    /// framework identity, WITHOUT decompressing a single assembly.
    ///
    /// <para>This is what makes "load the manifest, then touch only what deviates" possible. The
    /// manifest is a few KB of JSON at a known entry; the assemblies it describes are megabytes.
    /// A consumer can therefore decide the whole adoption question — is this bundle for our
    /// framework, which of its types does this mesh hold, which of those are already on the
    /// store — before paying for any of the payload.</para>
    /// </summary>
    /// <param name="bundle">The archive, positioned at its start.</param>
    /// <returns>The manifest, or null when the archive carries none.</returns>
    public static Manifest? ReadManifest(Stream bundle)
    {
        using var archive = new ZipArchive(bundle, ZipArchiveMode.Read, leaveOpen: true);
        return ManifestOf(archive);
    }

    /// <summary>
    /// <see cref="ReadManifest(Stream)"/> for a bundle on disk — opened for sequential read and
    /// closed again, so the file's bytes never sit in the managed heap.
    /// </summary>
    /// <param name="bundlePath">Path to the bundle file.</param>
    /// <returns>The manifest, or null when the archive carries none.</returns>
    public static Manifest? ReadManifest(string bundlePath)
    {
        using var file = File.OpenRead(bundlePath);
        return ReadManifest(file);
    }

    /// <summary>
    /// <see cref="Read(Stream, IReadOnlySet{string})"/> for a bundle on disk.
    /// </summary>
    /// <param name="bundlePath">Path to the bundle file.</param>
    /// <param name="nodePaths">Node paths to extract, or null for all of them.</param>
    /// <returns>The manifest (null when the archive carries none) and the payloads found.</returns>
    public static (Manifest? Manifest, IReadOnlyList<Payload> Assemblies) ReadFile(
        string bundlePath, IReadOnlySet<string>? nodePaths = null)
    {
        using var file = File.OpenRead(bundlePath);
        return Read(file, nodePaths);
    }

    private static Manifest? ManifestOf(ZipArchive archive)
    {
        var manifestEntry = archive.GetEntry(NuGetPackageWriter.ManifestEntry);
        if (manifestEntry is null)
            return null;

        using var stream = manifestEntry.Open();
        return JsonSerializer.Deserialize<Manifest>(stream, Json);
    }

    /// <summary>
    /// Rejects a declared content path that could escape the directory a consumer extracts into.
    ///
    /// <para>🚨 This is the one place in the bundle format where PRODUCER-CONTROLLED STRINGS BECOME
    /// FILE PATHS on a consumer's machine. Everything else a bundle carries is either bytes keyed by
    /// a node path (used as a dictionary key, never as a path) or a module file name the module
    /// lane validates. A relative path is joined to an output directory and written, so
    /// <c>../../…</c>, a rooted path, or a drive-qualified one would place attacker-chosen bytes
    /// outside it — on a BUILD AGENT, which then compiles what it finds.</para>
    ///
    /// <para>Enforced on READ rather than only at the extraction site, because the alternative is
    /// every consumer re-deriving the same rule and one of them getting it wrong. The writer
    /// refuses to declare such a path too; this is the half that also holds for a bundle this
    /// process did not produce.</para>
    /// </summary>
    private static bool IsUnsafeContentPath(string relativePath) =>
        string.IsNullOrWhiteSpace(relativePath)
        || Path.IsPathRooted(relativePath)
        || relativePath.Contains('\\')                       // a Windows separator is not our shape
        || relativePath.Contains(':')                          // drive- or scheme-qualified
        || relativePath.Split('/').Any(segment => segment is ".." or ".");

    /// <summary>
    /// Extracts the package's NODE DEFINITIONS — the tree a consumer recreates to use this package
    /// as an upstream WITHOUT cloning and recompiling it.
    ///
    /// <para>🚨 Manifest-driven, like <see cref="Read(byte[])"/>, and for a sharper reason here:
    /// these files are written to a consumer's working tree. Globbing
    /// <see cref="NuGetPackageWriter.ContentFolder"/> would recreate whatever a future producer
    /// happens to put there, so only DECLARED paths are extracted.</para>
    ///
    /// <para>🚨 A declared file the archive lacks is FATAL to the whole read — this returns empty
    /// rather than a partial tree. Unlike a missing assembly (skipped: that one NodeType merely
    /// compiles as it would have anyway), a half-materialised package is worse than none: its roots
    /// reference nodes that are not there, so the consumer fails at bind time with a missing-node
    /// error that names the wrong thing. All or nothing, the same rule
    /// <see cref="ReadModule(byte[])"/> applies to a module's closure.</para>
    /// </summary>
    /// <param name="bundle">The archive bytes.</param>
    /// <returns>The manifest (null when the archive carries none) and the declared files.</returns>
    public static (Manifest? Manifest, IReadOnlyList<ContentFile> Files) ReadContent(byte[] bundle)
    {
        using var buffer = new MemoryStream(bundle, writable: false);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

        var manifestEntry = archive.GetEntry(NuGetPackageWriter.ManifestEntry);
        if (manifestEntry is null)
            return (null, []);

        Manifest? manifest;
        using (var stream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(stream, Json);

        if (manifest?.Content is not { Count: > 0 } declared)
            return (manifest, []);

        var files = new List<ContentFile>();
        foreach (var relativePath in declared)
        {
            // 🚨 THROWS rather than returning empty. An incomplete archive is a benign producer
            // bug and degrades to "no content"; a path that escapes the extraction directory is
            // hostile or corrupt, and a caller about to write files must not be able to mistake it
            // for "this bundle carries none".
            if (IsUnsafeContentPath(relativePath))
                throw new InvalidOperationException(
                    $"bundle declares an unsafe content path '{relativePath}' — a package tree is "
                    + "extracted relative to an output directory, so a rooted or parent-traversing "
                    + "path would write outside it");

            var entry = archive.GetEntry($"{NuGetPackageWriter.ContentFolder}/{relativePath}");
            if (entry is null)
                // Incomplete tree — all or nothing (see remarks).
                return (manifest, []);
            files.Add(new ContentFile(relativePath, ReadAll(entry)));
        }

        return (manifest, files);
    }

    /// <summary>
    /// Extracts the manifest and the MODULE closure files it names (#1664) — the module half of the
    /// same bundle <see cref="Read(byte[])"/> serves NodeType assemblies from.
    ///
    /// <para>Manifest-driven like <see cref="Read(byte[])"/>, for the same reason: the manifest is the
    /// producer's statement of which files belong to the module, and enumerating
    /// <see cref="NuGetPackageWriter.ModuleFolder"/> instead would adopt any stray entry a future
    /// writer happens to place there. A file the manifest names but the archive lacks is FATAL here,
    /// unlike the NodeType path: a NodeType with missing bytes simply compiles, but a module folder
    /// missing part of its closure loads and then faults at first use — so an incomplete module
    /// yields NO files rather than a subset that would land.</para>
    /// </summary>
    /// <param name="bundle">The archive bytes.</param>
    /// <returns>The manifest (null when the archive carries none) and the module's files — empty
    /// when the bundle declares no module or the declared closure is incomplete.</returns>
    /// <summary>
    /// The module's STATIC WEB ASSETS, read by the same all-or-nothing rule as its assemblies: a
    /// declared asset the archive does not carry means an incomplete bundle, and half a view pack
    /// renders unstyled rather than failing.
    /// </summary>
    public static IReadOnlyList<ModuleAsset> ReadModuleAssets(byte[] bundle)
    {
        using var buffer = new MemoryStream(bundle, writable: false);
        return ReadModuleAssetsFrom(buffer);
    }

    /// <summary>
    /// <see cref="ReadModuleAssets(byte[])"/> over a SEEKABLE stream — a spooled upload on disk (#5501), so
    /// the archive's compressed bytes never have to sit in the managed heap to be read.
    /// </summary>
    /// <param name="bundle">The archive, seekable and positioned at its start. Left open.</param>
    public static IReadOnlyList<ModuleAsset> ReadModuleAssetsFrom(Stream bundle)
    {
        using var archive = new ZipArchive(bundle, ZipArchiveMode.Read, leaveOpen: true);

        var manifestEntry = archive.GetEntry(NuGetPackageWriter.ManifestEntry);
        if (manifestEntry is null)
            return [];
        Manifest? manifest;
        using (var stream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(stream, Json);
        if (manifest?.Module?.StaticAssets is not { Count: > 0 } declared)
            return [];

        var assets = new List<ModuleAsset>();
        foreach (var relative in declared)
        {
            var entry = archive.GetEntry(NuGetPackageWriter.ModuleAssetEntryPathFor(relative));
            if (entry is null)
                return [];
            assets.Add(new ModuleAsset(relative, ReadAll(entry)));
        }
        return assets;
    }

    /// <summary>
    /// The module's RID-specific NATIVE payloads (#4126), read by the same all-or-nothing rule as
    /// its assemblies and for a stronger reason: a module that loads without the engine it declared
    /// does not render wrong, it throws <c>DllNotFoundException</c> at first use — so half a native
    /// set is worse than none, and none is what a consumer of a legacy bundle already handles.
    ///
    /// <para>The relative path is preserved exactly as declared, because it IS the layout the
    /// runtime resolver probes (<c>ModuleNativeAssets</c> →
    /// <c>&lt;moduleDir&gt;/runtimes/&lt;rid&gt;/native/&lt;lib&gt;</c>).</para>
    /// </summary>
    /// <param name="bundle">The archive bytes.</param>
    /// <returns>The declared native payloads, or empty when the bundle declares none.</returns>
    public static IReadOnlyList<ModuleAsset> ReadModuleNativeAssets(byte[] bundle)
    {
        using var buffer = new MemoryStream(bundle, writable: false);
        return ReadModuleNativeAssetsFrom(buffer);
    }

    /// <summary>
    /// <see cref="ReadModuleNativeAssets(byte[])"/> over a SEEKABLE stream — a spooled upload on disk (#5501), so
    /// the archive's compressed bytes never have to sit in the managed heap to be read.
    /// </summary>
    /// <param name="bundle">The archive, seekable and positioned at its start. Left open.</param>
    public static IReadOnlyList<ModuleAsset> ReadModuleNativeAssetsFrom(Stream bundle)
    {
        using var archive = new ZipArchive(bundle, ZipArchiveMode.Read, leaveOpen: true);

        var manifestEntry = archive.GetEntry(NuGetPackageWriter.ManifestEntry);
        if (manifestEntry is null)
            return [];
        Manifest? manifest;
        using (var stream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(stream, Json);
        if (manifest?.Module?.NativeAssets is not { Count: > 0 } declared)
            return [];

        var natives = new List<ModuleAsset>();
        foreach (var relative in declared)
        {
            // 🚨 The EXACT layout, validated HERE and not only in the packer. This is the
            // boundary for a PRODUCER-controlled bundle, and the landing stage writes these paths
            // to disk for the process to load — so a path that is merely "not traversing" is not
            // enough. Two refusals, because they are two different statements: one is a safety
            // violation, the other is a payload nothing would ever probe.
            if (IsUnsafeContentPath(relative))
                throw new InvalidOperationException(
                    $"bundle declares an unsafe native path '{relative}' — native payloads are "
                    + "written relative to the module folder and then loaded, so a rooted or "
                    + "parent-traversing path would place executable code outside it");
            if (!NuGetPackageWriter.IsModuleNativeLayout(relative))
                throw new InvalidOperationException(
                    $"bundle declares a native payload at '{relative}', which is not the layout "
                    + "the module loader probes (exactly runtimes/<rid>/native/<file>). Landing it "
                    + "would write bytes to a path nothing ever looks at — shipped in appearance, "
                    + "absent in behaviour — so the bundle is refused instead");
            var entry = archive.GetEntry(NuGetPackageWriter.ModuleNativeEntryPathFor(relative));
            if (entry is null)
                return [];
            natives.Add(new ModuleAsset(relative, ReadAll(entry)));
        }
        return natives;
    }

    public static (Manifest? Manifest, IReadOnlyList<ModuleFile> Files) ReadModule(byte[] bundle)
    {
        using var buffer = new MemoryStream(bundle, writable: false);
        return ReadModuleFrom(buffer);
    }

    /// <summary>
    /// <see cref="ReadModule(byte[])"/> over a SEEKABLE stream — a spooled upload on disk (#5501), so
    /// the archive's compressed bytes never have to sit in the managed heap to be read.
    /// </summary>
    /// <param name="bundle">The archive, seekable and positioned at its start. Left open.</param>
    public static (Manifest? Manifest, IReadOnlyList<ModuleFile> Files) ReadModuleFrom(Stream bundle)
    {
        using var archive = new ZipArchive(bundle, ZipArchiveMode.Read, leaveOpen: true);

        var manifestEntry = archive.GetEntry(NuGetPackageWriter.ManifestEntry);
        if (manifestEntry is null)
            return (null, []);

        Manifest? manifest;
        using (var stream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(stream, Json);

        if (manifest?.Module?.Assemblies is not { Count: > 0 } declared)
            return (manifest, []);

        var files = new List<ModuleFile>();
        foreach (var fileName in declared)
        {
            var entry = archive.GetEntry(NuGetPackageWriter.ModuleEntryPathFor(fileName));
            if (entry is null)
                // Incomplete closure — all or nothing (see remarks).
                return (manifest, []);
            files.Add(new ModuleFile(fileName, ReadAll(entry)));
        }

        return (manifest, files);
    }

    /// <summary>
    /// One entry's bytes, allocated ONCE at the size the archive declares (#5501).
    ///
    /// <para>🚨 The previous form copied into a growing <see cref="MemoryStream"/> and then called
    /// <see cref="MemoryStream.ToArray"/>: every doubling left its predecessor for the GC, and the
    /// final copy held the whole entry a second time — roughly THREE times an entry's size live at
    /// the peak, on the large-object heap for any real assembly. The publish endpoint's OOM
    /// (<c>MemoryStream.ToArray</c> under <c>MapPublish</c>) is that dance on the body; this is the
    /// same dance per entry. The declared length is authoritative for a Deflate or Stored entry, and
    /// a stream that ends short of it is a corrupt archive, which <see cref="Stream.ReadExactly(Span{byte})"/>
    /// reports instead of returning a silently truncated assembly.</para>
    /// </summary>
    /// <summary>Deflate's maximum expansion ratio (a 258-byte match per ~2 bits, ≈1032:1).</summary>
    private const long MaxDeflateExpansion = 1032;

    /// <summary>Headroom for block headers on tiny entries, where the ratio bound alone is too tight.</summary>
    private const long DeflateSlackBytes = 1024;

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        // 🚨 The declared length comes from the PRODUCER's central directory, so it is checked
        // before anything is allocated from it. Deflate cannot expand beyond ~1032:1, and a Stored
        // entry cannot expand at all, so a length past that bound is a lie. Allocating it would
        // be the OOM this method exists to prevent, driven by one malformed entry.
        if (entry.Length > entry.CompressedLength * MaxDeflateExpansion + DeflateSlackBytes
            || entry.Length > Array.MaxLength)
            throw new InvalidDataException(
                $"bundle entry '{entry.FullName}' declares {entry.Length:N0} bytes from "
                + $"{entry.CompressedLength:N0} compressed, which no Deflate stream can expand to");
        using var source = entry.Open();
        var bytes = GC.AllocateUninitializedArray<byte>((int)entry.Length);
        source.ReadExactly(bytes);
        if (source.ReadByte() != -1)
            throw new InvalidDataException(
                $"bundle entry '{entry.FullName}' holds more than the {entry.Length:N0} bytes it declares");
        return bytes;
    }
}
