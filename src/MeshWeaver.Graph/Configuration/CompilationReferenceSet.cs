using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The default Roslyn <see cref="MetadataReference"/> set every dynamic-NodeType compilation
/// binds against — the trusted-platform-assembly list plus a few well-known additions — owned by
/// the <b>mesh</b>, not by the process.
///
/// <para>🚨 Why this is a mesh-scoped instance and not a <c>static readonly</c> list. It used to
/// be <c>MeshNodeCompilationService._references</c>, a <c>private static readonly
/// IReadOnlyList&lt;MetadataReference&gt;</c>, and was twice written off in review as "a
/// write-once constant lookup" — the sanctioned exemption in
/// <see href="Doc/Architecture/NoStaticState.md">NoStaticState.md</see>. That reading is wrong,
/// and the exemption does not apply: the <i>list</i> is write-once, but its <i>elements</i> are
/// not constants. A <see cref="PortableExecutableReference"/> owns lazily materialized,
/// <see cref="IDisposable"/> <c>Metadata</c> (a memory-mapped PE image) and Roslyn hangs its
/// derived assembly/symbol tables — <c>AssemblyMetadata.CachedSymbols</c> — off that same
/// instance. So the field was a mutable, process-wide cache wearing <c>static readonly</c>
/// clothing: the exact shape the no-static-state rule exists to forbid, and the only state that
/// every compilation in a test host demonstrably shared.</para>
///
/// <para>Registered as a singleton next to <c>MeshNodeCompilationService</c>, so its lifetime IS
/// the mesh's: it becomes unreachable when the mesh's container does, and needs no
/// <c>Clear()</c> for test isolation. Nothing else in the process holds these instances, so
/// nothing outlives the mesh that built them.</para>
///
/// <para>🚨 It deliberately does NOT dispose the metadata at mesh teardown, and must not start.
/// <c>Metadata</c> is <see cref="IDisposable"/>, but a compile can still be in flight (or a
/// symbol graph still live) when a mesh is torn down — disposing underneath one is a
/// use-after-free on the very object graph whose corruption this class exists to rule out.
/// Dropping the reference and letting the mapping be released with the object is the only safe
/// release, and it is enough for the isolation property.</para>
///
/// <para>💰 Cost, measured rather than assumed (macOS, .NET 10, Roslyn 5.6, the 470-reference set
/// a <c>MeshWeaver.Hosting.Monolith.Test</c> process actually has). One set costs <b>~15 MiB of
/// resident metadata</b> and ~73 ms to build; a process that builds one per mesh instead of one
/// per process pays that per mesh that compiles. It is a HIGH-WATER cost, not a leak — the
/// native metadata is reused once the sets are collected (five rounds of 50 sets plateau at
/// ~1.1 GiB rather than growing) — but it is only reclaimed on finalization, which the GC has no
/// pressure signal for. That is why construction is <b>lazy</b>: a mesh that never compiles a
/// NodeType never materializes a reference set, which is most meshes in a test host.</para>
/// </summary>
public sealed class CompilationReferenceSet
{
    // Instance, not static. Lazy so that only a mesh that actually compiles pays the ~15 MiB /
    // ~73 ms; ExecutionAndPublication so concurrent first compiles materialize exactly one set.
    private readonly Lazy<ImmutableArray<MetadataReference>> references =
        new(BuildDefaultReferences, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// This mesh's reference set. Materialized on first access and then a plain field read.
    /// </summary>
    public ImmutableArray<MetadataReference> References => references.Value;

    /// <summary>
    /// Builds one reference set — TPA assemblies plus a few well-known additions. Uses
    /// <see cref="MetadataReference.CreateFromFile(string, MetadataReferenceProperties, DocumentationProvider)"/>
    /// (mmap, lazy read) — Roslyn typically reads only a small fraction of each assembly's
    /// metadata. An earlier attempt at
    /// <see cref="MetadataReference.CreateFromStream(Stream, MetadataReferenceProperties, DocumentationProvider, string?)"/>
    /// to avoid finalizer pressure ended up reading the whole DLL into managed memory eagerly —
    /// net 10%+ slower in the autocomplete-test CPU profile, since most of those bytes were never
    /// touched.
    /// </summary>
    internal static ImmutableArray<MetadataReference> BuildDefaultReferences()
    {
        var references = ImmutableArray.CreateBuilder<MetadataReference>();

        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (trustedAssemblies != null)
        {
            foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    continue;
                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
                catch
                {
                    // Skip assemblies that can't be loaded
                }
            }
        }

        // Three well-known additions in case TPA didn't include them. Dedup
        // against TPA-derived set by Display path so we don't double-load.
        var seen = new HashSet<string>(
            references.Select(r => r.Display ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in new[]
        {
            typeof(object).Assembly,                                           // System.Runtime
            typeof(System.ComponentModel.DataAnnotations.KeyAttribute).Assembly, // DataAnnotations
            typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute).Assembly, // System.Text.Json
        })
        {
            if (!string.IsNullOrEmpty(assembly.Location)
                && File.Exists(assembly.Location)
                && seen.Add(assembly.Location))
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(assembly.Location));
                }
                catch
                {
                    // Skip if it can't be loaded
                }
            }
        }

        return references.ToImmutable();
    }
}
