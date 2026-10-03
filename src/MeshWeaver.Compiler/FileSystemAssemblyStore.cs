using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Security.Cryptography;
using MeshWeaver.Mesh.Persistence;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Compiler;

/// <summary>
/// Filesystem-backed <see cref="IAssemblyStore"/>. Used by the monolith portal and
/// tests where there is no shared blob storage — the cache lives on local disk,
/// survives process restarts, and is safe to share across multiple in-process hubs.
/// Layout: <c>{RootDirectory}/{sanitized-nodeTypePath}/v{version}-{contentHash}.dll</c>
/// (+ <c>.pdb</c>). The content-hash suffix is what makes each compile's path unique
/// — two compiles for the same (nodeTypePath, version) but different bytes (e.g. an
/// edit-then-recompile that happens to land on the same hub-version key, or two test
/// runs that reuse a stale on-disk dll from a previous session) get distinct files
/// instead of one overwriting / "winning" the other.
/// </summary>
public sealed class FileSystemAssemblyStore : IAssemblyStore
{
    private readonly string rootDirectory;
    private readonly ILogger<FileSystemAssemblyStore> logger;
    private readonly int keepVersionsPerType;

    /// <summary>
    /// The shipped per-type version budget: the live build plus two behind it. Three is what makes a
    /// mixed-build window survivable (an instance one or two publications behind still finds its
    /// bytes) without letting a directory grow with the recompile count.
    /// </summary>
    public const int DefaultKeepVersionsPerType = 3;

    /// <summary>
    /// Initializes a new instance of the filesystem-backed assembly store rooted at the
    /// given directory (created if it does not exist).
    /// </summary>
    /// <param name="rootDirectory">The root directory under which compiled assemblies are cached.</param>
    /// <param name="logger">The logger for cache hit/miss and write diagnostics.</param>
    /// <param name="keepVersionsPerType">
    /// How many of a type's most recent VERSIONS to keep, within the writing process's own framework
    /// generation — see <see cref="EvictSupersededVersions"/>. Values below 1 are clamped to 1: a
    /// budget of zero would evict the file just written.
    /// </param>
    public FileSystemAssemblyStore(
        string rootDirectory,
        ILogger<FileSystemAssemblyStore> logger,
        int keepVersionsPerType = DefaultKeepVersionsPerType)
        : this(rootDirectory, logger, keepVersionsPerType, AtomicFileWrite.OpenTempForWrite)
    {
    }

    /// <summary>
    /// <see cref="FileSystemAssemblyStore(string, ILogger{FileSystemAssemblyStore}, int)"/> with the
    /// staging-file writer injected — the seam a test uses to stand this store on a volume that
    /// accepts a write and then does not keep it (a full share). Production never passes anything
    /// but <see cref="AtomicFileWrite.OpenTempForWrite"/>.
    /// </summary>
    /// <param name="rootDirectory">The root directory under which compiled assemblies are cached.</param>
    /// <param name="logger">The logger for cache hit/miss and write diagnostics.</param>
    /// <param name="keepVersionsPerType">See the primary constructor.</param>
    /// <param name="openForWrite">Opens a staging path for writing; see
    /// <see cref="AtomicFileWrite.PublishBytesWith(string, byte[], Func{string, Stream})"/>.</param>
    public FileSystemAssemblyStore(
        string rootDirectory,
        ILogger<FileSystemAssemblyStore> logger,
        int keepVersionsPerType,
        Func<string, Stream> openForWrite)
    {
        ArgumentNullException.ThrowIfNull(openForWrite);
        this.rootDirectory = rootDirectory;
        this.logger = logger;
        this.keepVersionsPerType = Math.Max(1, keepVersionsPerType);
        this.openForWrite = openForWrite;
        Directory.CreateDirectory(rootDirectory);
    }

    private readonly Func<string, Stream> openForWrite;

    /// <summary>How many of a type's most recent versions this store keeps per framework generation.</summary>
    public int KeepVersionsPerType => keepVersionsPerType;

    /// <summary>
    /// Looks up the cached assembly for a (node-type path, version) pair, returning the
    /// path of the newest matching DLL or null if none is cached.
    /// </summary>
    /// <param name="nodeTypePath">The mesh path of the node type whose assembly is requested.</param>
    /// <param name="version">The MeshNode version the assembly was compiled for.</param>
    /// <returns>An observable emitting the local DLL path, or null on a cache miss.</returns>
    public IObservable<string?> TryGetAssemblyPath(string nodeTypePath, long version)
    {
        // Lookup by (nodeTypePath, version) alone — the caller doesn't know the
        // content hash. Returns the newest dll matching the v{version}- prefix,
        // which is the same file that the latest Put for this (nodeTypePath, version)
        // produced. A stale dll from a prior session with the same version key but
        // different content is sorted before the freshly-written one (LastWriteTimeUtc),
        // so newest-first ensures we never serve a stale-bytes hit.
        var dir = Path.Combine(rootDirectory, Sanitize(nodeTypePath));
        if (!Directory.Exists(dir))
        {
            logger.LogDebug("Assembly cache miss for {NodeTypePath}@v{Version} — no dir", nodeTypePath, version);
            return Observable.Return<string?>(null);
        }
        var candidate = NewestOpenable(dir, nodeTypePath, version);
        if (candidate is null)
        {
            logger.LogDebug("Assembly cache miss for {NodeTypePath}@v{Version}", nodeTypePath, version);
            return Observable.Return<string?>(null);
        }
        logger.LogDebug("Assembly cache hit at {DllPath}", candidate.FullName);
        return Observable.Return<string?>(candidate.FullName);
    }

    /// <summary>
    /// 🚨 <b>Resolves the build the record NAMES, not merely the newest file under its version.</b>
    /// In order: the exact <paramref name="contentPath"/> (the record's
    /// <c>LatestAssemblyPath</c>) when it is this generation's, openable and — when
    /// <paramref name="assemblyMvid"/> is known — carries that MVID; else the openable file of
    /// <paramref name="version"/> whose MVID is <paramref name="assemblyMvid"/>; else the
    /// version-only answer of <see cref="TryGetAssemblyPath(string, long)"/>, unchanged.
    ///
    /// <para><b>Why.</b> The version key alone is not an identity: several builds can share it (a
    /// bundle adopted at the node version a compile already used, a recompile that did not move the
    /// version, a rollback). Answering "newest by write time" re-bound whichever file happened to be
    /// newest on this volume, so a dispose could land on the OLD bytes while the record named the
    /// new ones. The record carries the content-hashed path and the MVID of what it published;
    /// resolving by them is what makes "the next activation binds the newest published build" a
    /// guarantee instead of a likelihood.</para>
    ///
    /// <para>The fall-through is deliberate: a record that names bytes this volume does not hold
    /// still gets the version's newest file, and the caller's bind-time identity check
    /// (<c>ServedBuildIdentity.Mismatch</c>) refuses it and rebuilds — a named, logged outcome,
    /// never a silent stale bind.</para>
    /// </summary>
    /// <param name="nodeTypePath">The mesh path of the node type whose assembly is requested.</param>
    /// <param name="version">The store version the record names (<c>LastCompiledVersion</c>).</param>
    /// <param name="contentPath">The record's content path, relative to this store's root; may be null.</param>
    /// <param name="assemblyMvid">The record's published MVID ("N" hex); may be null.</param>
    /// <returns>An observable emitting the local DLL path, or null on a miss.</returns>
    public IObservable<string?> TryGetBuildPath(
        string nodeTypePath, long version, string? contentPath, string? assemblyMvid)
        => Observable.Defer(() =>
        {
            var named = NamedBuild(contentPath, assemblyMvid);
            if (named is not null)
                return Observable.Return<string?>(named);
            if (!string.IsNullOrEmpty(assemblyMvid))
            {
                var dir = Path.Combine(rootDirectory, Sanitize(nodeTypePath));
                if (Directory.Exists(dir))
                {
                    var byIdentity = new DirectoryInfo(dir)
                        .EnumerateFiles($"v{version}-{FrameworkTag}-*.dll")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .FirstOrDefault(f => CanOpen(f.FullName)
                            && string.Equals(MvidOf(f.FullName), assemblyMvid, StringComparison.OrdinalIgnoreCase));
                    if (byIdentity is not null)
                        return Observable.Return<string?>(byIdentity.FullName);
                }
                logger.LogInformation(
                    "Assembly cache: no file of {NodeTypePath}@v{Version} carries the published MVID "
                    + "{Mvid} — answering the version's newest file; the bind-time identity check "
                    + "decides whether it may be bound", nodeTypePath, version, assemblyMvid);
            }
            return TryGetAssemblyPath(nodeTypePath, version);
        });

    /// <summary>The file <paramref name="contentPath"/> names under this root, when it is this
    /// generation's, openable, and (if <paramref name="assemblyMvid"/> is known) those bytes.</summary>
    private string? NamedBuild(string? contentPath, string? assemblyMvid)
    {
        if (string.IsNullOrWhiteSpace(contentPath))
            return null;
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(rootDirectory, contentPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        var rootFull = Path.GetFullPath(rootDirectory);
        if (!full.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            return null;
        // Another generation's bytes are not loadable here (BadImageFormat — prod 2026-06-20).
        if (!string.Equals(AssemblyCacheFileName.TagOf(Path.GetFileName(full)), FrameworkTag,
                StringComparison.OrdinalIgnoreCase))
            return null;
        if (!File.Exists(full) || !CanOpen(full))
            return null;
        if (!string.IsNullOrEmpty(assemblyMvid)
            && MvidOf(full) is { } mvid
            && !string.Equals(mvid, assemblyMvid, StringComparison.OrdinalIgnoreCase))
            return null;
        return full;
    }

    /// <summary>The MVID of the PE at <paramref name="path"/> ("N" hex), or null when unreadable.
    /// Metadata only — nothing is loaded (the same read as <c>ServedBuildIdentity.OfFile</c>).</summary>
    private static string? MvidOf(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            if (!pe.HasMetadata)
                return null;
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            return md.GetGuid(md.GetModuleDefinition().Mvid).ToString("N");
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryTouch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Assembly cache: could not refresh the write time of {Path}", path);
        }
    }

    /// <summary>
    /// Writes the compiled assembly (and optional PDB) into the cache for the given
    /// (node-type path, version) pair, returning the local DLL path. Content-addressed: the
    /// same bytes land on the same name (a no-op), different bytes on their own name — an
    /// existing build is never returned in place of the bytes handed in.
    /// </summary>
    /// <param name="nodeTypePath">The mesh path of the node type the assembly belongs to.</param>
    /// <param name="version">The MeshNode version the assembly was compiled for.</param>
    /// <param name="assemblyBytes">The compiled assembly (DLL) bytes.</param>
    /// <param name="pdbBytes">The optional debug symbol (PDB) bytes; null or empty to skip.</param>
    /// <returns>An observable emitting the local path of the cached DLL.</returns>
    public IObservable<string> Put(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes)
        => PutWithLocation(nodeTypePath, version, assemblyBytes, pdbBytes)
            .Select(loc => loc.LocalPath);

    /// <summary>
    /// Sentinel collection name returned by <see cref="PutWithLocation"/> on this store —
    /// "local" denotes "the bytes live in the local filesystem cache only; cross-silo
    /// readers must recompile rather than rely on this reference."
    /// </summary>
    public const string FileSystemCollectionName = "local";

    /// <summary>
    /// Writes the compiled assembly (and optional PDB) into the cache and returns its full
    /// store location (local path, collection name, and relative content path).
    /// Content-addressed: different bytes for the same (node-type path, version) land on their
    /// own name and are returned; nothing already published is overwritten.
    /// </summary>
    /// <param name="nodeTypePath">The mesh path of the node type the assembly belongs to.</param>
    /// <param name="version">The MeshNode version the assembly was compiled for.</param>
    /// <param name="assemblyBytes">The compiled assembly (DLL) bytes.</param>
    /// <param name="pdbBytes">The optional debug symbol (PDB) bytes; null or empty to skip.</param>
    /// <returns>An observable emitting the location of the cached assembly.</returns>
    public IObservable<AssemblyStoreLocation> PutWithLocation(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes)
        // 🚨 Deferred, so the write — and its refusal — happen at SUBSCRIBE time and travel the
        // subscriber's error channel. Eager evaluation would throw a ShortWriteException out of
        // the CALL, before the compile pipeline's Catch/terminal handler is even wired, and the
        // node would settle nothing.
        => Observable.Defer(() => Observable.Return(PutWithLocationCore(nodeTypePath, version, assemblyBytes, pdbBytes)));

    private AssemblyStoreLocation PutWithLocationCore(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes)
    {
        var dir = Path.Combine(rootDirectory, Sanitize(nodeTypePath));
        Directory.CreateDirectory(dir);

        // 🚨 CONTENT-ADDRESSED, NEVER FIRST-WRITE-WINS (maintainer, 2026-10-03: "after
        // disposerequest, new version must be loaded"). This used to return ANY existing
        // v{version}-*.dll without writing the new bytes. Identical bytes were never the problem —
        // they land on the identical content-hash name below and the publication is a no-op. The
        // problem was DIFFERENT bytes at the same (path, version): a prebuilt bundle N+1 adopted at
        // the node version a bundle N (or a compile) already used, or a recompile that did not move
        // the node version. The store kept N and handed N's path back, while the caller stamped
        // N+1's MVID from the bytes it had in hand. Every activation after that resolved N, the
        // bind-time identity check refused it and recompiled, the recompile's Put handed N back
        // AGAIN, and once the retry budget was spent the activation bound N for its lifetime — the
        // #2471 state: "a recycle re-binds the same local copy". A DisposeRequest could therefore
        // never load the new version, by construction.
        //
        // The old reason for first-write-wins — "the first DLL is already ALC-loaded; overwriting
        // it throws IOException and poisons the NodeType" — does not apply to a DIFFERENT name:
        // nothing is overwritten. The loaded file stays exactly where it is and keeps serving the
        // activation that holds it ("old version must continue working"); the new bytes get their
        // own name, so the path itself now changes whenever the bytes do, and the stale-build
        // watcher's path comparison sees a same-version byte change as the new build it is.
        var dllPath = GetDllPath(nodeTypePath, version, assemblyBytes);
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        var relativeContentPath = Path.GetRelativePath(rootDirectory, dllPath).Replace('\\', '/');

        // 🚨 ATOMIC PUBLICATION — never File.WriteAllBytes on dllPath (MeshWeaver#1387).
        // The DLL's NAME is its publication: TryGetAssemblyPath and TryGetBuildPath above
        // discover it by globbing
        // `v{version}-{tag}-*.dll`, and the winner's path goes straight to
        // AssemblyLoadContext.LoadFromAssemblyPath. FileMode.Create (what WriteAllBytes uses)
        // creates the target FIRST and streams the bytes afterwards, so a reader that globs
        // inside that window loads a TRUNCATED PE image. The header is intact, so the load
        // itself succeeds and the first Assembly.GetTypes() throws
        //   ReflectionTypeLoadException: Could not load type 'X' from assembly
        //   'DynamicNode_…' because the format is invalid
        // which CompileResultFromAssembly records as a compile failure — and that failure is
        // TERMINAL: it writes CompilationStatus.Error and the first-build kickoff (gated on
        // Status == null) never retries, so one transient torn read PARKS the NodeType until
        // someone deletes the file. A parked NodeType refuses portal readiness.
        //
        // The reader is not even necessarily in this process: on AKS this directory is
        // /data/assembly-cache, a ReadWriteMany Azure Files share, so every replica globs the
        // bytes another replica is mid-write on.
        //
        // Publish the PDB before the DLL: the DLL is the discovery key, so anything visible to
        // a reader is complete AND already has its symbols.
        //
        // 🚨 And a publication is DURABLE + VERIFIED, or it is refused (2026-09-08, memex, /data at
        // 3 MiB free): PublishBytes flushes to disk and compares the length the volume kept with
        // the bytes handed in, and throws ShortWriteException when they differ. The throw is the
        // contract — it propagates out of this observable into the compile pipeline, which treats
        // it as TERMINAL: no Release node, no version pointer, the previous build left in place.
        // Swallowing it here and returning the path would re-create the very incident: a name in
        // the discovery namespace whose bytes are not there.
        if (pdbBytes is { Length: > 0 })
            AtomicFileWrite.PublishBytesWith(pdbPath, pdbBytes, openForWrite);
        var published = AtomicFileWrite.PublishBytesWith(dllPath, assemblyBytes, openForWrite);
        if (published)
            logger.LogInformation(
                "Cached assembly at {DllPath} ({Bytes} bytes)", dllPath, assemblyBytes.Length);
        else
        {
            // Another writer (or another replica through the shared volume) published the same
            // content-hashed name first. The bytes are identical by construction — the hash IS
            // the name — so this is a no-op, not a conflict.
            logger.LogDebug(
                "Assembly already published at {DllPath} by a concurrent writer — kept theirs "
                + "(identical bytes: the content hash is the file name)", dllPath);
            // 🚨 …but it is now the version's LATEST publication, and the version-only lookup
            // (TryGetAssemblyPath without an identity) answers by write time. A rollback that
            // republishes bytes an older sibling of this version already holds must not leave the
            // newer sibling looking current. Touching the time changes no bytes, so an ALC mapping
            // of this file is unaffected; a failure is housekeeping and never fails the Put.
            TryTouch(dllPath);
        }

        // 🚨 EVICTION AT WRITE (#2086). The pass that just added a version is the only one that
        // knows, without a second directory walk from somewhere else, that this type's directory
        // grew — so it is the one that trims it.
        EvictSupersededVersions(dir, keep: dllPath);

        return new AssemblyStoreLocation(dllPath, FileSystemCollectionName, relativeContentPath);
    }

    /// <summary>
    /// 🚨 The newest <c>v{version}-{tag}-*.dll</c> this process can actually OPEN — never merely
    /// the newest one the directory LISTS (MeshWeaver#4528, #3876).
    ///
    /// <para><b>Listed is not openable on the share.</b> <c>/data/assembly-cache</c> is an Azure
    /// Files (SMB) volume shared by every replica. An evicted dll that another replica still holds
    /// open — an <c>AssemblyLoadContext</c> maps the file it loaded for as long as it lives — is
    /// only marked delete-pending: it stays in every directory listing, and every new open of it
    /// fails with ENOENT until the last handle closes, which can be hours. Before this, the
    /// lookup handed such a name out as a hit. The bundle route then failed at its open, every
    /// time, for the same file (memex-cloud 2026-09-26: <c>MyAi_Panel/v751-…</c> four requests in
    /// 30 s, <c>SocialMedia_Profile/v1413-…</c>, <c>LinkedIn_TileMigration/v643-…</c>) — a 500
    /// until #5769 and a 503 + <c>Retry-After</c> since, which every consumer retried to
    /// exhaustion on every pass (control instance, <c>Admin/_LogIncident/613ea204988e9e23</c>,
    /// 09-26 → 09-30). A condition that lasts as long as a handle on another pod is not a
    /// transient, and no retry budget can outlast it.</para>
    ///
    /// <para>So the lookup proves the candidate: a name that cannot be opened is skipped (and
    /// said so, once per lookup), an openable sibling of the same version is taken, and none is a
    /// MISS — which is recoverable by construction: the bundle route counts the type as a miss and
    /// the consumer compiles it; activation recompiles; <see cref="Put"/> writes fresh bytes.</para>
    ///
    /// <para>🚨 Only ABSENCE is classified. A file that is present and unreadable for any other
    /// reason (denied, locked, a share fault) is a real defect and keeps surfacing.</para>
    /// </summary>
    /// <param name="dir">The type's directory.</param>
    /// <param name="nodeTypePath">The type's mesh path, for the log line.</param>
    /// <param name="version">The MeshNode version sought.</param>
    /// <returns>The newest openable candidate, or null when there is none.</returns>
    private FileInfo? NewestOpenable(string dir, string nodeTypePath, long version)
    {
        var unopenable = ImmutableList<string>.Empty;
        FileInfo? found = null;
        foreach (var candidate in new DirectoryInfo(dir)
                     .EnumerateFiles($"v{version}-{FrameworkTag}-*.dll")
                     .OrderByDescending(f => f.LastWriteTimeUtc))
        {
            if (CanOpen(candidate.FullName))
            {
                found = candidate;
                break;
            }
            unopenable = unopenable.Add(candidate.Name);
        }

        if (!unopenable.IsEmpty)
            logger.LogWarning(
                "Assembly cache: {NodeTypePath}@v{Version} — {Count} listed file(s) cannot be opened "
                + "(evicted, still held open elsewhere on the share): {Names}. Not a hit; {Outcome}",
                nodeTypePath, version, unopenable.Count, string.Join(", ", unopenable),
                found is null ? "treated as a MISS" : $"serving {found.Name}");
        return found;
    }

    /// <summary>Whether <paramref name="path"/> opens for reading. Absence (ENOENT — a vanished
    /// file, a dangling link, a delete-pending name on SMB) answers false; every other fault
    /// propagates.</summary>
    private static bool CanOpen(string path)
    {
        try
        {
            using var handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 🚨 <b>Keep the newest <see cref="KeepVersionsPerType"/> VERSIONS of this type, in this
    /// process's own framework generation. Delete the rest. Nothing else.</b>
    ///
    /// <para><b>Why the version axis, and why it needed its own collector.</b> The cache accumulates
    /// on TWO independent axes and they need two different arguments. Across framework generations a
    /// whole new fleet of files appears per deploy, and only a live CLAIM can prove one is
    /// unreferenced — that is <c>AssemblyCacheGenerations</c>'s job, and it is the axis it can see.
    /// WITHIN one generation a type accrues one dll/pdb pair per recompile, forever: measured on
    /// memex-cloud 2026-08-22, <c>Store_Plugin</c> alone held 4,184 files spanning v100…v8800+ —
    /// inside a single generation, where generation retention has nothing to bucket. Three
    /// generations of that shape is still ~12.5k files, which is why keeping generations could never
    /// have been the answer to a 16 GiB volume filling up (and taking the DataProtection key ring
    /// beside it down with the compile cache).</para>
    ///
    /// <para><b>Why it is safe to delete a superseded version, when deleting a superseded GENERATION
    /// is not.</b> A generation belongs to an IMAGE — another pod may be running it, and loading the
    /// wrong generation's bytes is <c>BadImageFormatException</c> → failed grain activations →
    /// portal-wide wedge (prod 2026-06-20). That is why this never crosses the tag boundary: it only
    /// ever removes files carrying <see cref="FrameworkTag"/>, the generation THIS process runs and
    /// is authoritative about. Within it, the worst case of removing an older version is a cache
    /// MISS, and a miss is recoverable by construction: <c>TryGetAssemblyPath</c> returns null and
    /// activation's recompile-and-retry mints the bytes again. An assembly already loaded is
    /// unaffected — the ALC holds the mapping, and on a filesystem that refuses to unlink an open
    /// file the delete simply fails and the file stays.</para>
    ///
    /// <para><b>Everything here is a KEEP rule and failures never propagate.</b> Only names
    /// <see cref="AssemblyCacheFileName"/> decodes are candidates — the atomic-write
    /// <c>.tmp-*</c> leftovers, the bake leases, the generation claims and any legacy pre-tag DLL are
    /// unattributable and therefore untouchable. The file just written is excluded explicitly rather
    /// than by trusting that its version is the highest. A delete that throws is logged and skipped:
    /// the next write reconsiders it, and a locked file is exactly the file we most want to leave
    /// alone.</para>
    /// </summary>
    /// <param name="dir">The type's directory — already created, already the one just written to.</param>
    /// <param name="keep">The full path of the DLL this call published; never a deletion candidate.</param>
    private void EvictSupersededVersions(string dir, string keep)
    {
        try
        {
            var mine = new DirectoryInfo(dir)
                // The glob NARROWS, it never decides: a legacy directory can hold thousands of
                // files from generations this process must not touch, and listing all of them on an
                // Azure Files share once per compile is a real cost for a set that is empty by
                // construction on a freshly-rolled image. Attribution stays entirely with
                // AssemblyCacheFileName.Parse plus the tag equality below, which is what keeps this
                // and the generation sweep agreeing on which names this store wrote.
                .EnumerateFiles($"*-{FrameworkTag}-*", SearchOption.TopDirectoryOnly)
                .Select(f => (File: f, Identity: AssemblyCacheFileName.Parse(f.Name)))
                .Where(x => x.Identity is { } id
                            && string.Equals(id.Tag, FrameworkTag, StringComparison.OrdinalIgnoreCase))
                .Select(x => (x.File, Identity: x.Identity!.Value))
                .ToList();

            var keptVersions = mine
                .Select(x => x.Identity.Version)
                .Distinct()
                .OrderByDescending(v => v)
                .Take(keepVersionsPerType)
                .ToHashSet();

            var evicted = 0;
            var bytes = 0L;
            foreach (var (file, identity) in mine)
            {
                if (keptVersions.Contains(identity.Version))
                    continue;
                if (string.Equals(file.FullName, keep, StringComparison.Ordinal)
                    || string.Equals(file.FullName, Path.ChangeExtension(keep, ".pdb"), StringComparison.Ordinal))
                    continue;
                try
                {
                    var length = file.Length;
                    file.Delete();
                    evicted++;
                    bytes += length;
                }
                catch (Exception ex)
                {
                    // Surfaced, not swallowed — and NOT retried. A file we cannot unlink is a file
                    // something is holding, which is the one we least want to remove; the next write
                    // into this directory plans it again.
                    logger.LogDebug(ex,
                        "Assembly cache: could not evict {Path} — it stays, and the next write for "
                        + "this type considers it again", file.FullName);
                }
            }

            if (evicted > 0)
                logger.LogInformation(
                    "Assembly cache: evicted {Evicted} superseded file(s) ({Kb:N0} KB) from {Dir}, "
                    + "keeping the newest {Keep} version(s) of framework {Tag}",
                    evicted, bytes / 1024d, dir, keepVersionsPerType, FrameworkTag);
        }
        catch (Exception ex)
        {
            // Eviction is housekeeping behind a successful publication. The bytes are on disk and the
            // caller's path is valid whatever happens here, so an unreadable directory must never
            // turn a good compile into a failed one.
            logger.LogDebug(ex,
                "Assembly cache: could not evict superseded versions under {Dir} — the write itself "
                + "succeeded and the next one plans eviction again", dir);
        }
    }

    /// <summary>
    /// The root directory this cache lives in — the same directory
    /// <c>AssemblyCacheGenerations</c> (MeshWeaver.Graph) sweeps and claims against.
    /// </summary>
    public string RootDirectory => rootDirectory;

    /// <summary>
    /// 🚨 Per-image framework identity baked into every assembly-cache filename + lookup glob. The
    /// store is keyed by (nodeTypePath, MeshNode version), but the COMPILED bytes are bound to the
    /// framework's reference assemblies — two DIFFERENT images compiling the SAME (path, version)
    /// produce INCOMPATIBLE DLLs. Without this tag a freshly-deployed image's lookup matched (and
    /// first-write-wins RETURNED) the PREVIOUS image's DLL → System.BadImageFormatException on ALC
    /// load, which cascaded into failed grain activations and a portal-wide wedge on deploy (prod
    /// 2026-06-20). The identity (<see cref="FrameworkBuildIdentity.FrameworkVersion"/>) is the
    /// platform COMPATIBILITY KEY (<c>c003e001</c> — eight characters, so the whole key is the tag;
    /// policy <c>platform-backwards-compatibility</c>): it changes only on a declared epoch or major
    /// bump, so every platform build of one epoch HITS the previous build's bytes — the ladder's
    /// "platform rolls, keeps the old plugin bytes" — and a declared break misses them (clean
    /// recompile). Bytes a NEWER platform build produced are refused above the store, by the record's
    /// producer floor (<c>NodeTypeBuildIdentity</c>), never by the tag.
    ///
    /// <para>It is also the GENERATION key: a whole new set of files is written per image, and
    /// nothing in the store removes an old one — see <c>AssemblyCacheGenerations</c> (in
    /// MeshWeaver.Graph) for the retention sweep and for why the growth is by design while the
    /// unboundedness was not.</para>
    /// </summary>
    public static readonly string FrameworkTag = FrameworkBuildIdentity.FrameworkVersion[..8];

    /// <summary>
    /// The name a fresh publication of <paramref name="bytes"/> lands under: the content hash — or,
    /// when a dead name already holds that exact spelling, the first name of a deterministic
    /// sequence that does not (#4528).
    ///
    /// <para>🚨 A deterministic recompile of the same version produces the SAME bytes and therefore
    /// the same content-hash name. If that name is listed but unopenable (an evicted dll another
    /// replica still holds open on the share — see <see cref="NewestOpenable"/>), publishing onto it
    /// fails the way every open of it fails, and the miss → recompile → <see cref="Put"/> recovery
    /// would land exactly where it started. So a dead spelling is stepped past: the next candidate
    /// hashes the bytes with an ordinal appended, which keeps the name in the 12-hex shape
    /// <see cref="AssemblyCacheFileName.Parse"/> attributes (so eviction still collects it). The
    /// hash is a tie-breaker, never verified against content. The sequence ends at the first name
    /// that is absent or openable — there are only finitely many dead names in a directory.</para>
    /// </summary>
    private string GetDllPath(string nodeTypePath, long version, byte[] bytes)
    {
        var dir = Path.Combine(rootDirectory, Sanitize(nodeTypePath));
        for (var ordinal = 0; ; ordinal++)
        {
            var hash = ordinal == 0 ? ContentHash(bytes) : ContentHash(bytes, ordinal);
            var path = Path.Combine(dir, $"v{version}-{FrameworkTag}-{hash}.dll");
            if (!IsListed(path) || CanOpen(path))
                return path;
            logger.LogWarning(
                "Assembly cache: {DllPath} is listed but cannot be opened (evicted, still held open "
                + "elsewhere on the share) — publishing these bytes under another name",
                path);
        }
    }

    /// <summary>Whether the directory LISTS <paramref name="path"/> — a dangling or delete-pending
    /// name included. Asked of the listing, not of <see cref="File.Exists"/>: that follows the name
    /// to what it opens, and a name that opens nothing is exactly the one this must see.</summary>
    private static bool IsListed(string path) =>
        Path.GetDirectoryName(path) is { } dir
        && Directory.Exists(dir)
        && Directory.EnumerateFileSystemEntries(dir, Path.GetFileName(path)).Any();

    private static string ContentHash(byte[] bytes, int ordinal)
    {
        var salted = new byte[bytes.Length + sizeof(int)];
        bytes.CopyTo(salted, 0);
        BitConverter.GetBytes(ordinal).CopyTo(salted, bytes.Length);
        return ContentHash(salted);
    }

    private static string ContentHash(byte[] bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        // 12 hex chars from the SHA-256 — collision-resistant for the assembly-bytes
        // population we're keying on, short enough to keep paths readable in logs.
        return Convert.ToHexString(hash[..6]).ToLowerInvariant();
    }

    /// <summary>
    /// Turns a mesh path like <c>Systemorph/FutuRe/Pricing</c> into a filesystem-safe
    /// subdirectory name using a two-step escape: literal <c>_</c> becomes <c>__</c>
    /// first, then <c>/</c> becomes <c>_</c>. This is reversible and collision-free —
    /// a mesh path <c>A/B</c> and a mesh path <c>A_B</c> encode to different directories.
    /// </summary>
    private static string Sanitize(string nodeTypePath)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(nodeTypePath.Length);
        foreach (var c in nodeTypePath)
        {
            if (c == '_') sb.Append("__");
            else if (c == '/') sb.Append('_');
            else if (invalid.Contains(c)) sb.Append('-');
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
