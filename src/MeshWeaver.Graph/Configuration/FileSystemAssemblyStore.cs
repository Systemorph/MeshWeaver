using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Filesystem-backed <see cref="IAssemblyStore"/>. Used by the monolith portal and
/// tests where there is no shared blob storage — the cache lives on local disk,
/// survives process restarts, and is safe to share across multiple in-process hubs.
/// Layout: <c>{RootDirectory}/{sanitized-nodeTypePath}/v{version}.dll</c> (+ <c>.pdb</c>).
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
        var dllPath = GetDllPath(nodeTypePath, version);
        if (File.Exists(dllPath))
        {
            logger.LogDebug("Assembly cache hit at {DllPath}", dllPath);
            return Observable.Return<string?>(dllPath);
        }
        logger.LogDebug("Assembly cache miss for {NodeTypePath}@v{Version}", nodeTypePath, version);
        return Observable.Return<string?>(null);
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
        var dllPath = GetDllPath(nodeTypePath, version);
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);
        var relativeContentPath = Path.GetRelativePath(rootDirectory, dllPath).Replace('\\', '/');

        // Per (nodeTypePath, version), the bytes are deterministic — same source
        // compiled against the same framework produces an equivalent assembly. If
        // the destination DLL already exists, skip the write: it's either the
        // same compile finishing twice (two replicas raced), or a prior process
        // produced this version and an ALC in the current process has the file
        // locked. Overwriting a locked file throws IOException ("being used by
        // another process") and bubbles up as outcome.Error → CompilationStatus.Error
        // gets persisted to the NodeType JSON, where every subsequent activation
        // re-reads it and fails forever. Re-use is the self-healing path:
        // FrameworkVersion has rolled (Graph.dll rebuild → new version key →
        // new dllPath) iff a recompile is genuinely needed.
        if (File.Exists(dllPath))
        {
            logger.LogDebug(
                "Assembly already at {DllPath} — skipping write (idempotent put, file may be ALC-locked)",
                dllPath);
            return Observable.Return(new AssemblyStoreLocation(dllPath, FileSystemCollectionName, relativeContentPath));
        }

        File.WriteAllBytes(dllPath, assemblyBytes);
        if (pdbBytes is { Length: > 0 })
            File.WriteAllBytes(pdbPath, pdbBytes);
        logger.LogInformation(
            "Cached assembly at {DllPath} ({Bytes} bytes)", dllPath, assemblyBytes.Length);
        return Observable.Return(new AssemblyStoreLocation(dllPath, FileSystemCollectionName, relativeContentPath));
    }

    private string GetDllPath(string nodeTypePath, long version) =>
        Path.Combine(rootDirectory, Sanitize(nodeTypePath), $"v{version}.dll");

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
