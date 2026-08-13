using System.Security.Cryptography;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// What an emit produced, and the fingerprint that lets the publisher prove the file on disk still
/// IS that. Returned by <c>MeshNodeCompilationService.EmitCompilationToDirectory</c> and checked by
/// <c>MeshNodeCompilationService.EmitToDiskWithRetry</c> before the staged directory is renamed into
/// the discovery namespace.
///
/// <para>🚨 Why a fingerprint and not a size. The acceptance test used to be
/// <c>File.Exists(dll) &amp;&amp; new FileInfo(dll).Length &gt; 0</c> — "non-empty", not "the bytes we
/// emitted". A compiled NodeType assembly is published under a name readers discover by, and the
/// discovered path goes straight to <c>AssemblyLoadContext.LoadFromAssemblyPath</c>, so anything
/// that gate lets through is executed as an assembly. Two shapes get through it, and both are
/// TERMINAL rather than transient:</para>
/// <list type="bullet">
///   <item><description>A <b>short</b> image throws <see cref="BadImageFormatException"/> at LOAD,
///   so <c>LoadNodeAssembly</c> hands back null and the compile records
///   <c>CompilationStatus.Error</c>.</description></item>
///   <item><description>A <b>full-length</b> image with an unwritten region inside it LOADS — header
///   and assembly identity are intact — and throws <c>ReflectionTypeLoadException</c> on the first
///   <c>Assembly.GetTypes()</c>: <i>"Could not load type 'X' from assembly 'DynamicNode_…' because
///   the format is invalid"</i>. That is MeshWeaver#1412's signature verbatim, and a length check
///   cannot see it.</description></item>
/// </list>
///
/// <para>Either way <c>CompileResultFromAssembly</c> writes <c>CompilationStatus.Error</c> and the
/// first-build kickoff is gated on <c>Status == null</c>, so it never recompiles: <b>the bytes may
/// heal, the verdict does not</b>, and the NodeType is parked until someone deletes the artifact by
/// hand. A parked NodeType refuses portal readiness.</para>
///
/// <para><b>Why exact-match instead of "does it parse".</b> Validating the staged file as an
/// assembly (PE header + metadata tables) was tried and measured: over a census of damaged images it
/// still admitted <b>19</b> that the real loader refuses — corrupt signature blobs, corrupt type
/// references, names that read as strings but are rejected as identities. Metadata validation is a
/// leaky approximation of "usable". Comparing against the digest of what the emit actually produced
/// is not an approximation: it admits the emitted image and nothing else.</para>
///
/// <para>This is a publication CONTRACT, not a retry around a load. Nothing re-reads a bad artifact
/// hoping for a better answer; a bad artifact simply never becomes discoverable, and the compile
/// re-runs the emit it already re-ran for a lost write — bounded, stateless, and never applied to a
/// genuine compile error.</para>
/// </summary>
/// <param name="DllPath">Path the emit wrote the assembly to (inside the staging directory).</param>
/// <param name="Sha256">SHA-256 of the emitted image, taken from the bytes in memory.</param>
/// <param name="Length">Length in bytes of the emitted image.</param>
internal readonly record struct EmittedArtifact(string DllPath, byte[] Sha256, long Length)
{
    /// <summary>Fingerprints an in-memory image.</summary>
    /// <param name="dllPath">Path the image was written to.</param>
    /// <param name="image">The emitted bytes.</param>
    /// <returns>The artifact descriptor to hand to the publisher.</returns>
    public static EmittedArtifact For(string dllPath, ReadOnlySpan<byte> image)
    {
        var hash = new byte[SHA256.HashSizeInBytes];
        SHA256.HashData(image, hash);
        return new EmittedArtifact(dllPath, hash, image.Length);
    }

    /// <summary>
    /// True when the file at <see cref="DllPath"/> holds exactly the emitted image.
    /// </summary>
    /// <param name="reason">On false, what was wrong — carried into the log and, after the last
    /// attempt, into the terminal <c>CompilationException</c> so the operator is not left with a
    /// generic "could not be persisted" pointing at a read-only cache directory.</param>
    /// <returns>Whether the staged file may be published.</returns>
    public bool MatchesFileOnDisk(out string reason)
    {
        try
        {
            var info = new FileInfo(DllPath);
            if (!info.Exists)
            {
                reason = "the artifact is not on disk";
                return false;
            }
            if (info.Length != Length)
            {
                reason = $"the artifact is {info.Length} bytes on disk but {Length} were emitted";
                return false;
            }

            // Read rather than memory-map: a mapped file that a concurrent writer truncates faults
            // as SIGBUS — a process kill, not an exception — and a verification step must never be
            // able to do that.
            using var stream = new FileStream(
                DllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 64 * 1024);
            var onDisk = SHA256.HashData(stream);
            if (!onDisk.AsSpan().SequenceEqual(Sha256))
            {
                reason = $"the artifact on disk differs from the emitted image ({Length} bytes, "
                         + $"digest {Convert.ToHexString(onDisk.AsSpan(0, 6))} != "
                         + $"{Convert.ToHexString(Sha256.AsSpan(0, 6))})";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = $"the artifact could not be read back — {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
