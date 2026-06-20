using System.Reactive.Linq;
using System.Security.Cryptography;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

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

    public FileSystemAssemblyStore(string rootDirectory, ILogger<FileSystemAssemblyStore> logger)
    {
        this.rootDirectory = rootDirectory;
        this.logger = logger;
        Directory.CreateDirectory(rootDirectory);
    }

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
        var candidate = new DirectoryInfo(dir)
            .EnumerateFiles($"v{version}-{FrameworkTag}-*.dll")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (candidate is null)
        {
            logger.LogDebug("Assembly cache miss for {NodeTypePath}@v{Version}", nodeTypePath, version);
            return Observable.Return<string?>(null);
        }
        logger.LogDebug("Assembly cache hit at {DllPath}", candidate.FullName);
        return Observable.Return<string?>(candidate.FullName);
    }

    public IObservable<string> Put(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes)
        => PutWithLocation(nodeTypePath, version, assemblyBytes, pdbBytes)
            .Select(loc => loc.LocalPath);

    /// <summary>
    /// Sentinel collection name returned by <see cref="PutWithLocation"/> on this store —
    /// "local" denotes "the bytes live in the local filesystem cache only; cross-silo
    /// readers must recompile rather than rely on this reference."
    /// </summary>
    public const string FileSystemCollectionName = "local";

    public IObservable<AssemblyStoreLocation> PutWithLocation(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes)
    {
        var dir = Path.Combine(rootDirectory, Sanitize(nodeTypePath));
        Directory.CreateDirectory(dir);

        // First-write-wins for (nodeTypePath, version): if any v{version}-*.dll
        // already exists in the directory, return its path WITHOUT writing the
        // new bytes. The content-hash suffix is a tie-breaker for distinct
        // historical compiles, NOT a way to fork a single (path, version) into
        // multiple concurrent files. Two compiles for the same (path, version)
        // happen for two reasons, both of which must resolve to the existing
        // file:
        //   1. Identical bytes — the hashed name collides, File.Exists short-
        //      circuits, no IO. Optimal.
        //   2. Different bytes — happens when a recompile lands on the same
        //      hub-version key but the source-tree state shifted (test re-run
        //      with an in-memory edit, framework patch version drift). The
        //      first DLL is already ALC-loaded; overwriting it throws
        //      IOException → CompilationStatus.Error → the NodeType is poisoned
        //      until process restart. Skip the write and return the existing
        //      path so the loaded ALC keeps serving consistent bytes.
        //
        // Lookup mirrors TryGetAssemblyPath above (newest v{version}-*.dll).
        var existing = new DirectoryInfo(dir)
            .EnumerateFiles($"v{version}-{FrameworkTag}-*.dll")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (existing is not null)
        {
            var existingRel = Path.GetRelativePath(rootDirectory, existing.FullName).Replace('\\', '/');
            logger.LogDebug(
                "Assembly already at {DllPath} — skipping write (idempotent put, first-write-wins for ALC safety)",
                existing.FullName);
            return Observable.Return(new AssemblyStoreLocation(existing.FullName, FileSystemCollectionName, existingRel));
        }

        var dllPath = GetDllPath(nodeTypePath, version, assemblyBytes);
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        var relativeContentPath = Path.GetRelativePath(rootDirectory, dllPath).Replace('\\', '/');

        File.WriteAllBytes(dllPath, assemblyBytes);
        if (pdbBytes is { Length: > 0 })
            File.WriteAllBytes(pdbPath, pdbBytes);
        logger.LogInformation(
            "Cached assembly at {DllPath} ({Bytes} bytes)", dllPath, assemblyBytes.Length);
        return Observable.Return(new AssemblyStoreLocation(dllPath, FileSystemCollectionName, relativeContentPath));
    }

    // 🚨 Per-image framework identity baked into every assembly-cache filename + lookup glob. The store
    // is keyed by (nodeTypePath, MeshNode version), but the COMPILED bytes are bound to the framework's
    // reference assemblies — two DIFFERENT images compiling the SAME (path, version) produce
    // INCOMPATIBLE DLLs. Without this tag a freshly-deployed image's lookup matched (and first-write-
    // wins RETURNED) the PREVIOUS image's DLL → System.BadImageFormatException on ALC load, which
    // cascaded into failed grain activations and a portal-wide wedge on deploy (atioz 2026-06-20). The
    // MVID (Graph module content hash) changes only when the framework bytes change, so a new image
    // misses the old DLLs (clean recompile) while an unchanged framework still hits the cache.
    private static readonly string FrameworkTag = NodeTypeCompilationHelpers.FrameworkVersion[..8];

    private string GetDllPath(string nodeTypePath, long version, byte[] bytes)
    {
        var hash = ContentHash(bytes);
        return Path.Combine(rootDirectory, Sanitize(nodeTypePath), $"v{version}-{FrameworkTag}-{hash}.dll");
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
