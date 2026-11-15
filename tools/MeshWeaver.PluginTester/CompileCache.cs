using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshWeaver.Compiler;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The bake's CROSS-RUN compile cache (<c>Doc/Architecture/NodeTypeCompileCache</c>): one entry per
/// NodeType compile, keyed on everything that decides the emitted bytes and on nothing that does
/// not, so a gate over unchanged content and unchanged reference SURFACES compiles nothing.
///
/// <para><b>The key.</b> <c>sha256</c> over (1) the compile's generated-input digest —
/// <c>NodeSetCompiler.GeneratedInputDigestOf</c>: the generated source after every <c>@@</c>
/// include, the emit options, the Roslyn build and the generators that run; (2) the paths of the
/// sources and includes it consumed (the dependency edges name them); (3) the names of the
/// composed MODULES (they classify the edges); and (4) the reference set as
/// <c>name=surface</c> pairs (<see cref="AssemblySurface"/>). A reference's build identity is NOT
/// in it: the four modules a Plugins gate composes are rebuilt by every run with a fresh MVID and
/// a commit stamp, and a key on that identity never hit (MeshWeaver CI profile 2026-11, R3). A
/// change to any reference's PUBLIC SURFACE does change it, so every NodeType compiled against
/// that set misses.</para>
///
/// <para><b>What a hit reuses and what it recomputes.</b> The bytes, the warnings and the
/// dependency edges are a function of the key, and are reused. The dependency RECORD is a
/// function of the bytes AND the current host, so it is recomputed against this run's resolver
/// (<c>NodeSetCompiler.FromEmitted</c>), and every edge's <c>TargetBuildMvid</c> is re-stamped with
/// this run's build of that reference — the values a fresh compile would have written.</para>
///
/// <para><b>Never a different verdict.</b> Only SUCCESSFUL compiles are stored. An unreadable,
/// unwritable, damaged or partly written entry is a miss (the type compiles), never a failure;
/// every entry carries the sha256 of its files and its full key, and is checked on read. A type
/// with <c>#r "nuget:…"</c> references is never cached: its resolution is network state.</para>
/// </summary>
internal sealed class CompileCache
{
    /// <summary>The layout and key version — bumped whenever either changes meaning.</summary>
    public const string Version = "nodetype-compile/v1";

    /// <summary>Entries unused for this long are removed by <see cref="Prune"/>.</summary>
    /// <remarks>Short on purpose: an entry is ~2 MB per NodeType uncompressed (about half that
    /// stored), and every change to a reference's public surface starts a new set of entries on a
    /// share that every runner uses.</remarks>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(3);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string root;
    private readonly string referenceKey;
    private readonly string moduleKey;
    private readonly ImmutableDictionary<string, string?> currentMvids;
    private int hits;
    private int misses;
    private int stored;
    private int uncacheable;
    private int damaged;

    private CompileCache(string root, string referenceKey, string moduleKey,
        ImmutableDictionary<string, string?> currentMvids)
    {
        this.root = root;
        this.referenceKey = referenceKey;
        this.moduleKey = moduleKey;
        this.currentMvids = currentMvids;
    }

    /// <summary>The reference-set half of every key (for the log).</summary>
    public string ReferenceKey => referenceKey;

    /// <summary>Entries reused.</summary>
    public int Hits => Volatile.Read(ref hits);

    /// <summary>Cacheable compiles that found no entry.</summary>
    public int Misses => Volatile.Read(ref misses);

    /// <summary>Entries written by this bake.</summary>
    public int Stored => Volatile.Read(ref stored);

    /// <summary>Compiles that cannot be cached (NuGet references).</summary>
    public int Uncacheable => Volatile.Read(ref uncacheable);

    /// <summary>Entries that existed and failed verification (counted inside <see cref="Misses"/>).</summary>
    public int Damaged => Volatile.Read(ref damaged);

    /// <summary>
    /// Opens the cache at <paramref name="directory"/> for a bake against
    /// <paramref name="references"/>, or returns null with the reason when it cannot be keyed —
    /// a reference with no file, or a directory that cannot be created. Null means "compile
    /// everything", exactly as without a cache.
    /// </summary>
    public static CompileCache? Open(
        string directory,
        IReadOnlyList<MetadataReference> references,
        IReadOnlyList<string> moduleAssemblyPaths,
        out string? reason)
    {
        reason = null;
        try
        {
            var root = Path.GetFullPath(directory);
            Directory.CreateDirectory(Path.Combine(root, "entries"));
            var surfaces = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var mvids = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var reference in references)
            {
                if (reference is not PortableExecutableReference { FilePath: { Length: > 0 } path })
                {
                    reason = $"reference '{reference.Display}' has no file, so the reference set cannot be keyed";
                    return null;
                }
                var name = Path.GetFileNameWithoutExtension(path);
                var (surface, mvid) = SurfaceOf(root, path);
                if (!surfaces.TryGetValue(name, out var set))
                    surfaces[name] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(surface);
                // Two files of one simple name: the edge re-stamp cannot know which one the
                // compile bound, so neither is re-stamped and such an edge forces a miss.
                mvids[name] = mvids.ContainsKey(name) && mvids[name] != mvid ? null : mvid;
            }
            var referenceDocument = new StringBuilder(Version).Append("\nreferences\n");
            foreach (var (name, set) in surfaces)
                referenceDocument.Append(name).Append('=').Append(string.Join("+", set)).Append('\n');
            var modules = moduleAssemblyPaths
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .Order(StringComparer.Ordinal);
            return new CompileCache(
                root,
                Sha256(referenceDocument.ToString()),
                string.Join(",", modules),
                mvids.ToImmutableDictionary(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>A reference's surface, memoised by its MVID (a content identity) under
    /// <c>surfaces/</c> — so the platform's hundreds of assemblies are read once per build of each.</summary>
    private static (string Surface, string? Mvid) SurfaceOf(string root, string path)
    {
        string mvid;
        using (var stream = File.OpenRead(path))
        using (var pe = new PEReader(stream))
        {
            if (!pe.HasMetadata)
                return ("file:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), null);
            var reader = pe.GetMetadataReader();
            mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("N");
        }
        var memo = Path.Combine(root, "surfaces", AssemblySurface.Scheme.TrimEnd(':'), mvid[..2], mvid);
        try
        {
            if (File.Exists(memo) && File.ReadAllText(memo).Trim() is { Length: > 0 } known
                && (known.StartsWith(AssemblySurface.Scheme, StringComparison.Ordinal)
                    || known.StartsWith(AssemblySurface.MvidScheme, StringComparison.Ordinal)))
                return (known, mvid);
        }
        catch (IOException)
        {
            // An unreadable memo only means reading the assembly again.
        }
        var surface = AssemblySurface.Of(path);
        TryWriteAtomically(memo, Encoding.UTF8.GetBytes(surface));
        return (surface, mvid);
    }

    /// <summary>The key of one compile — see the class summary for what it covers.</summary>
    public string KeyOf(NodeSetCompiler.CompileInputs inputs, string generatedInputDigest)
    {
        var document = new StringBuilder(Version).Append('\n')
            .Append("input=").Append(generatedInputDigest).Append('\n')
            .Append("references=").Append(referenceKey).Append('\n')
            .Append("modules=").Append(moduleKey).Append('\n');
        foreach (var path in inputs.MatchedSourcePaths)
            document.Append("source=").Append(path).Append('\n');
        foreach (var path in inputs.ResolvedIncludes.Keys)
            document.Append("include=").Append(path).Append('\n');
        return Sha256(document.ToString());
    }

    /// <summary>Counts a compile that cannot be cached.</summary>
    public void CountUncacheable() => Interlocked.Increment(ref uncacheable);

    /// <summary>
    /// The cached outcome of <paramref name="key"/>, materialised into
    /// <paramref name="outputDirectory"/>, or null (a miss — the caller compiles).
    /// </summary>
    public NodeSetCompiler.CompiledNode? TryRead(
        string key,
        NodeSetCompiler.CompileInputs inputs,
        string generatedInputDigest,
        Func<string, string?> dependencyIdOf,
        string toolchainId,
        string outputDirectory)
    {
        var entryDirectory = EntryDirectory(key);
        var manifestPath = Path.Combine(entryDirectory, "entry.json");
        try
        {
            if (!File.Exists(manifestPath))
            {
                Interlocked.Increment(ref misses);
                return null;
            }
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(manifestPath), Json);
            if (entry is null || entry.Key != key || entry.Version != Version)
                return Refuse(entryDirectory);
            Directory.CreateDirectory(outputDirectory);
            var dllPath = Path.Combine(outputDirectory, entry.DllName);
            var dll = ReadStored(Path.Combine(entryDirectory, entry.DllName));
            if (Convert.ToHexStringLower(SHA256.HashData(dll)) != entry.DllSha256)
                return Refuse(entryDirectory);
            string? pdbPath = null;
            if (entry.PdbName is { } pdbName)
            {
                var pdb = ReadStored(Path.Combine(entryDirectory, pdbName));
                if (Convert.ToHexStringLower(SHA256.HashData(pdb)) != entry.PdbSha256)
                    return Refuse(entryDirectory);
                pdbPath = Path.Combine(outputDirectory, pdbName);
                File.WriteAllBytes(pdbPath, pdb);
            }
            File.WriteAllBytes(dllPath, dll);

            DependencyEdgeSet? edges = null;
            if (entry.Edges is { } cachedEdges)
            {
                var restamped = ImmutableList.CreateBuilder<DependencyEdge>();
                foreach (var edge in cachedEdges.Edges)
                {
                    if (edge.TargetBuildMvid is null)
                    {
                        restamped.Add(edge);
                        continue;
                    }
                    // The edge names a reference by simple name; this run's build of it is the one
                    // a fresh compile would have stamped. A name this run does not hold exactly
                    // once cannot be re-stamped truthfully — compile instead.
                    if (!currentMvids.TryGetValue(edge.Target, out var mvid) || mvid is null)
                    {
                        Interlocked.Increment(ref misses);
                        return null;
                    }
                    restamped.Add(edge with { TargetBuildMvid = mvid });
                }
                edges = cachedEdges with
                {
                    Edges = restamped.ToImmutable(),
                    BuildMvid = NodeSetCompiler.EmittedMvidOf(dllPath),
                    InputDigest = generatedInputDigest,
                };
            }

            var compiled = NodeSetCompiler.FromEmitted(
                inputs, dllPath, pdbPath, dependencyIdOf, toolchainId, generatedInputDigest) with
            {
                Warnings = [.. entry.Warnings],
                DependencyEdges = edges,
            };
            Touch(manifestPath);
            Interlocked.Increment(ref hits);
            return compiled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or BadImageFormatException or NotSupportedException or InvalidDataException)
        {
            return Refuse(entryDirectory);
        }
    }

    /// <summary>A damaged entry is a miss — and is REMOVED, so the compile that follows can store a
    /// good one in its place instead of every later run missing on it until it is pruned.</summary>
    private NodeSetCompiler.CompiledNode? Refuse(string entryDirectory)
    {
        TryDelete(entryDirectory);
        Interlocked.Increment(ref damaged);
        Interlocked.Increment(ref misses);
        return null;
    }

    /// <summary>Stores a SUCCESSFUL compile under <paramref name="key"/>. A failure to write only
    /// costs the next run a compile.</summary>
    public void Store(string key, NodeSetCompiler.CompiledNode compiled)
    {
        var entryDirectory = EntryDirectory(key);
        if (Directory.Exists(entryDirectory))
            return;
        var staging = Path.Combine(root, "staging", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var dll = File.ReadAllBytes(compiled.DllPath);
            var dllName = Path.GetFileName(compiled.DllPath);
            WriteStored(Path.Combine(staging, dllName), dll);
            string? pdbName = null, pdbSha = null;
            if (compiled.PdbPath is { } pdbPath && File.Exists(pdbPath))
            {
                var pdb = File.ReadAllBytes(pdbPath);
                pdbName = Path.GetFileName(pdbPath);
                pdbSha = Convert.ToHexStringLower(SHA256.HashData(pdb));
                WriteStored(Path.Combine(staging, pdbName), pdb);
            }
            var entry = new Entry(
                Version, key, compiled.NodePath, dllName, Convert.ToHexStringLower(SHA256.HashData(dll)),
                pdbName, pdbSha, [.. compiled.Warnings], compiled.DependencyEdges);
            // entry.json LAST: a reader treats an entry without it as absent.
            File.WriteAllBytes(Path.Combine(staging, "entry.json"), JsonSerializer.SerializeToUtf8Bytes(entry, Json));
            Directory.CreateDirectory(ShardDirectory(key));
            Directory.Move(staging, entryDirectory);
            Interlocked.Increment(ref stored);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Another bake stored the same key first (the move found the directory), or the share
            // refused the write. Either way the next run compiles or reads the other bake's entry.
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// Removes entries unused for <see cref="Retention"/> and abandoned staging directories — at
    /// most once a day per cache, so a busy share is not walked on every bake. Returns how many
    /// entries were removed, or null when it did not run.
    /// </summary>
    public int? Prune(DateTimeOffset now)
    {
        var marker = Path.Combine(root, "last-prune");
        try
        {
            if (File.Exists(marker) && now - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromDays(1))
                return null;
            TryWriteAtomically(marker, Encoding.UTF8.GetBytes(now.ToString("O", CultureInfo.InvariantCulture)));
            var removed = 0;
            foreach (var shard in Directory.EnumerateDirectories(Path.Combine(root, "entries")))
                foreach (var entry in Directory.EnumerateDirectories(shard))
                {
                    var manifest = Path.Combine(entry, "entry.json");
                    var lastUsed = File.Exists(manifest)
                        ? File.GetLastWriteTimeUtc(manifest)
                        : Directory.GetLastWriteTimeUtc(entry);
                    if (now - lastUsed > Retention && TryDelete(entry))
                        removed++;
                }
            var staging = Path.Combine(root, "staging");
            if (Directory.Exists(staging))
                foreach (var abandoned in Directory.EnumerateDirectories(staging))
                    if (now - Directory.GetLastWriteTimeUtc(abandoned) > TimeSpan.FromDays(1))
                        TryDelete(abandoned);
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Stored files are gzip'd (about half the size); the recorded sha256 is over the
    /// UNCOMPRESSED bytes, so a damaged stream and a damaged payload are both refused.</summary>
    private static void WriteStored(string path, byte[] bytes)
    {
        using var file = File.Create(path + ".gz");
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        gzip.Write(bytes);
    }

    private static byte[] ReadStored(string path)
    {
        using var file = File.OpenRead(path + ".gz");
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        gzip.CopyTo(memory);
        return memory.ToArray();
    }

    private string ShardDirectory(string key) => Path.Combine(root, "entries", key[..2]);

    private string EntryDirectory(string key) => Path.Combine(ShardDirectory(key), key);

    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* LRU is advisory */ }
    }

    private static void TryWriteAtomically(string path, byte[] bytes)
    {
        try
        {
            // Every caller passes a file UNDER the cache root, so a parent always exists; a path
            // without one is a caller defect and is refused by name rather than assumed away.
            var parent = Path.GetDirectoryName(path)
                ?? throw new ArgumentException($"'{path}' has no parent directory", nameof(path));
            Directory.CreateDirectory(parent);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A memo or marker that could not be written is recomputed next time.
        }
    }

    private static bool TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>One stored compile.</summary>
    internal sealed record Entry(
        string Version,
        string Key,
        string NodePath,
        string DllName,
        string DllSha256,
        string? PdbName,
        string? PdbSha256,
        ImmutableArray<CompileWarning> Warnings,
        DependencyEdgeSet? Edges);
}
