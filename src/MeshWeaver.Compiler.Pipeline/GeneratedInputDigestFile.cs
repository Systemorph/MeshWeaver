using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 THE PRODUCER-DETERMINISM HALF of the per-type CONTENT KEY (#3892): the stage-1
/// generated-input digest, written BESIDE the bytes it describes so a disk-cache HIT can stamp the
/// same dependency record a fresh compile stamps.
///
/// <para><b>The defect this closes.</b> <c>CompiledDependencies.Compute</c> writes the reserved
/// <c>!input</c> entry only when the caller hands it a generated-input digest, and the digest was
/// available at exactly one place — inside <c>CompileAsyncCore</c>, three statements before Roslyn.
/// A disk-cache hit never enters that method, so it reached the stamp with <c>null</c> and produced
/// a record WITHOUT the content key. Nothing failed: <c>FindMismatch</c> simply had nothing to
/// compare and the toolchain entry still governed. But the stamp is a PRODUCER act — the record
/// travels into <c>NodeTypeDefinition.CompiledDependencies</c> and from there into every published
/// bundle — so whether a shipped artifact carried the ContentKey guard AT ALL depended on whether
/// the baking machine happened to have a warm cache. Two publishes of identical content shipped
/// records of different guard strength and no consumer could tell which it got; the weaker one
/// silently never fires the check. That is the shape of #3768 / #3732, and it is what made
/// <c>BakeEquivalenceTest</c> red only in a loaded CI shard.</para>
///
/// <para><b>Why the digest is PERSISTED and not RECOMPUTED.</b> Recomputing it at the hit site is
/// affordable (the values are all available — <c>PrepareGeneratedSource</c> runs before any compile
/// decision) but it answers a DIFFERENT question. The digest folds
/// <c>EmitPipeline.OptionsFingerprint</c>, <c>GeneratedInputIdentity.CompilerIdentity</c> and the
/// identities of the generator assemblies on disk — properties of the process doing the computing,
/// not of the bytes. A recomputed digest therefore describes "what a compile RIGHT HERE would be
/// fed", and stamping it onto bytes some other process emitted claims a content-key equality that
/// was never established. Since <c>FindMismatchAfterReevaluation</c> DEMOTES the toolchain entry
/// when the content key matches, that mis-stamp would let a build be adopted across the exact
/// toolchain move the toolchain entry exists to catch. Reading the producing compile's own digest
/// off the disk cannot say anything the producing compile did not.</para>
///
/// <para><b>Published atomically.</b> The file is written into <c>EmitToDiskWithRetry</c>'s STAGING
/// directory, before the directory rename that publishes the artifact under the discovery glob — so
/// a published cache entry carries the digest or does not exist, and a concurrent reader can never
/// observe bytes whose digest has not landed yet. A write fault here fails the compile exactly as a
/// lost DLL write does: an artifact whose provenance cannot be recorded is not published.</para>
///
/// <para>🚨 <b>Deliberately NOT in <c>MeshWeaver.Compiler</c>.</b> That assembly is a full-MVID
/// toolchain ROOT (<c>FrameworkBuildIdentity.ToolchainRoots</c>): under deterministic builds ANY
/// edit to it — a comment included — moves the framework identity, which invalidates every stamped
/// dependency record and every published bundle's adoptability on every mesh, and recompiles every
/// NodeType. The cache-directory layout is already split (the emit writes it, this pipeline reads
/// it), so the sidecar lives on the pipeline side where the same fix costs nothing.</para>
/// </summary>
internal static class GeneratedInputDigestFile
{
    /// <summary>
    /// The sidecar's extension. It sits beside <c>{nodeName}.dll</c> / <c>{nodeName}.pdb</c> in the
    /// published <c>{cacheDir}/{nodeName}_{ticks}_{guid}/</c> directory.
    /// </summary>
    internal const string Extension = ".inputdigest";

    /// <summary>The sidecar path for a cached assembly.</summary>
    internal static string PathFor(string dllPath) => Path.ChangeExtension(dllPath, Extension);

    /// <summary>Whether the artifact set at <paramref name="dllPath"/> carries its digest.</summary>
    internal static bool Exists(string dllPath) => File.Exists(PathFor(dllPath));

    /// <summary>
    /// Writes the digest into a STAGING directory, beside the assembly the caller just emitted.
    /// Throws on an IO fault — the caller (<c>EmitToDiskWithRetry</c>'s emit callback) discards the
    /// staging directory and the compile fails, which is the same verdict a lost DLL write gets:
    /// the artifact never enters the discovery namespace half-described.
    /// </summary>
    internal static void Write(string stagingDirectory, string nodeName, string digest)
        => File.WriteAllText(Path.Combine(stagingDirectory, nodeName + Extension), digest);

    /// <summary>
    /// The digest recorded beside <paramref name="dllPath"/>, or null when there is none (an
    /// artifact published by a build that predates the sidecar) or it cannot be read.
    ///
    /// <para>A read fault is LOGGED and answered as absent, never swallowed: the caller's response
    /// is to recompile, which is correct for an artifact this process cannot describe — and it can
    /// never be mistaken for a digest, because an absent digest is what the cache-validity check
    /// already refuses.</para>
    /// </summary>
    internal static string? TryRead(string dllPath, ILogger logger)
    {
        var path = PathFor(dllPath);
        try
        {
            if (!File.Exists(path))
                return null;
            var digest = File.ReadAllText(path).Trim();
            return digest.Length == 0 ? null : digest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex,
                "The generated-input digest at {DigestPath} could not be read — the cached assembly "
                + "beside it cannot be stamped with a content key, so it is recompiled instead",
                path);
            return null;
        }
    }
}
