using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// Process-shared, materialized-once Roslyn metadata references for kernel script
/// sessions.
///
/// <para><b>Why this exists (the ~200 MiB-per-session native leak):</b> every kernel
/// session compiles its submissions against ~350 assemblies. Left to itself, Roslyn
/// materializes a fresh <c>AssemblyMetadata</c> → <c>MetadataReader</c> → native
/// metadata block for EVERY one of them PER SESSION — via two paths: (1) raw
/// <see cref="Assembly"/> objects passed to <c>ScriptOptions.WithReferences</c>, and
/// (2) <c>RuntimeMetadataReferenceResolver.ResolveMissingAssembly</c>, which the
/// script compilation calls for the whole transitive closure of the globals type
/// (<c>MeshScriptGlobals</c> pulls in the full MeshWeaver graph). The metadata
/// section of a managed PE is copied into a NATIVE heap block — Microsoft.Graph.dll
/// alone is a 41 MiB block — and Roslyn's script <c>LoadContext</c> is
/// non-collectible, so nothing is ever reclaimed. A full-dump analysis of one
/// kernel-test class run showed 2,073+ live <c>MetadataReader</c>s (≈ sessions ×
/// refs), five identical 41 MiB Microsoft.Graph metadata blocks (one per session),
/// and ~1.1 GiB of committed private native memory — the direct cause of the CI
/// memory-pressure flakes (shifting observable-timeout failures late in every
/// shard).</para>
///
/// <para><b>The fix:</b> one <see cref="PortableExecutableReference"/> per assembly
/// file, once per process, shared by every session — both for the declared
/// reference set (<see cref="GetReferences"/>) and for resolver-driven resolution
/// (<see cref="SharedScriptMetadataResolver"/> wraps
/// <see cref="ScriptMetadataResolver.Default"/> and memoizes per path). Roslyn
/// shares the underlying <c>AssemblyMetadata</c> across compilations that use the
/// same reference instance, so the per-session metadata cost drops to ~zero.</para>
///
/// <para><b>NoStaticState.md compliance:</b> <see cref="Materialized"/> is a
/// process-global MEMO — pure-by-key (absolute file path → immutable reference over
/// immutable on-disk bytes), bounded by the set of assemblies on disk, and holding
/// NO <see cref="Type"/>s and NO AssemblyLoadContexts — it can pin neither meshes
/// nor collectible NodeType contexts. Allowlisted in <c>NoStaticCollectionsTest</c>
/// next to the other MEMO entries.</para>
/// </summary>
internal static class KernelScriptReferences
{
    /// <summary>
    /// MEMO: absolute assembly file path → the ONE shared
    /// <see cref="PortableExecutableReference"/> (and thus the one native metadata
    /// materialization) for that file in this process.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PortableExecutableReference> Materialized =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<ImmutableArray<PortableExecutableReference>> SharedSnapshot =
        new(CreateSharedSnapshot, LazyThreadSafetyMode.ExecutionAndPublication);

    private static ImmutableArray<PortableExecutableReference> CreateSharedSnapshot()
        => AppDomain.CurrentDomain.GetAssemblies()
            .Select(TryGetOrCreate)
            .Where(r => r is not null)
            .Select(r => r!)
            .Distinct()
            .ToImmutableArray();

    /// <summary>
    /// The shared reference for <paramref name="asm"/>, or null when the assembly
    /// cannot be referenced (dynamic, byte-loaded, or its file was deleted —
    /// collectible NodeType ALCs leave Assembly objects in AppDomain after a test
    /// removes their cache directory; CreateFromFile on a missing path would throw
    /// and must not poison the whole set).
    /// </summary>
    private static PortableExecutableReference? TryGetOrCreate(Assembly asm)
    {
        if (asm.IsDynamic) return null;
        var location = asm.Location;
        if (string.IsNullOrEmpty(location)) return null;
        return TryGetOrCreate(location);
    }

    private static PortableExecutableReference? TryGetOrCreate(string location)
    {
        if (Materialized.TryGetValue(location, out var existing))
            return existing;
        try
        {
            if (!File.Exists(location)) return null;
            // GetOrAdd with a freshly created value: a concurrent racer's loser copy
            // is dropped and collected — at most a transient double-materialization,
            // never a leak.
            return Materialized.GetOrAdd(location, MetadataReference.CreateFromFile(location));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Re-keys a resolver-produced reference onto the shared materialization for
    /// its file path. References without a path (in-memory) or with non-default
    /// properties (aliases, embed-interop) pass through untouched — sharing an
    /// instance across different properties would change compilation semantics.
    /// </summary>
    public static PortableExecutableReference? Share(PortableExecutableReference? reference)
    {
        if (reference?.FilePath is not { Length: > 0 } path)
            return reference;
        if (reference.Properties != MetadataReferenceProperties.Assembly)
            return reference;
        return TryGetOrCreate(path) ?? reference;
    }

    /// <summary>
    /// References for one kernel session: the process-shared snapshot plus shared
    /// references for any assembly in <paramref name="sessionAssemblies"/> (curated
    /// core set + DI-contributed module assemblies) that was loaded after the
    /// snapshot was taken. The per-session cost is ~zero — everything resolves to
    /// the same process-wide instances.
    /// </summary>
    public static ImmutableArray<MetadataReference> GetReferences(IEnumerable<Assembly> sessionAssemblies)
    {
        var snapshot = SharedSnapshot.Value;
        var result = ImmutableArray.CreateBuilder<MetadataReference>(snapshot.Length + 4);
        var seen = new HashSet<PortableExecutableReference>();
        foreach (var reference in snapshot)
        {
            if (seen.Add(reference))
                result.Add(reference);
        }
        foreach (var asm in sessionAssemblies)
        {
            var reference = TryGetOrCreate(asm);
            if (reference is not null && seen.Add(reference))
                result.Add(reference);
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// Shared reference for an explicit file path (the <c>#r "nuget: …"</c> restore
    /// path) — same memo, so repeated cells / sessions don't re-materialize package
    /// metadata either.
    /// </summary>
    public static PortableExecutableReference? GetOrCreateFromFile(string path)
        => TryGetOrCreate(path);

    /// <summary>
    /// Identity → shared reference WITHOUT materializing anything new: match the
    /// requested simple name against the live AppDomain (the missing-assembly
    /// closure of <c>MeshScriptGlobals</c> is by definition loaded — it's running
    /// this code), fall back to a file next to the referencing assembly. Returns
    /// null when neither matches — only then may the caller consult Roslyn's own
    /// (eagerly materializing) resolver.
    /// </summary>
    public static PortableExecutableReference? TryResolveByIdentity(
        AssemblyIdentity identity,
        string? referencingAssemblyPath)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            var name = asm.GetName();
            if (!string.Equals(name.Name, identity.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            var reference = TryGetOrCreate(asm);
            if (reference is not null)
                return reference;
        }

        if (!string.IsNullOrEmpty(referencingAssemblyPath))
        {
            var dir = Path.GetDirectoryName(referencingAssemblyPath);
            if (!string.IsNullOrEmpty(dir))
            {
                var sibling = Path.Combine(dir, identity.Name + ".dll");
                var reference = TryGetOrCreate(sibling);
                if (reference is not null)
                    return reference;
            }
        }

        return null;
    }
}

/// <summary>
/// Drop-in replacement for <see cref="ScriptMetadataResolver.Default"/> that funnels
/// every resolution through the process-shared materializations in
/// <see cref="KernelScriptReferences"/>. Without this, the script compilation's
/// missing-assembly resolution (the transitive closure of the globals type — the
/// whole MeshWeaver graph) re-materializes ~350 native metadata blocks per kernel
/// session; see <see cref="KernelScriptReferences"/> for the full leak analysis.
/// </summary>
internal sealed class SharedScriptMetadataResolver : MetadataReferenceResolver
{
    public static readonly SharedScriptMetadataResolver Instance = new();

    private readonly ScriptMetadataResolver inner = ScriptMetadataResolver.Default;

    private SharedScriptMetadataResolver() { }

    public override bool ResolveMissingAssemblies => true;

    public override PortableExecutableReference? ResolveMissingAssembly(
        MetadataReference definition,
        AssemblyIdentity referenceIdentity)
    {
        // 🚨 Resolve identity → SHARED reference ourselves first. Calling the inner
        // resolver here is itself the leak: RuntimeMetadataReferenceResolver's file
        // provider EAGERLY materializes the full native metadata block before we
        // could dedupe — the per-session blocks of every discarded duplicate are
        // never reclaimed. Only fall through to the inner resolver (then re-key the
        // result) for identities that genuinely aren't loaded anywhere.
        var shared = KernelScriptReferences.TryResolveByIdentity(
            referenceIdentity,
            (definition as PortableExecutableReference)?.FilePath);
        if (shared is not null)
            return shared;
        return KernelScriptReferences.Share(inner.ResolveMissingAssembly(definition, referenceIdentity));
    }

    public override ImmutableArray<PortableExecutableReference> ResolveReference(
        string reference,
        string? baseFilePath,
        MetadataReferenceProperties properties)
    {
        // Direct file path (#r "C:\…\Foo.dll") → shared materialization; everything
        // else (search-path / name-based #r) goes through the inner resolver and is
        // re-keyed onto the shared instance per path.
        if (properties == MetadataReferenceProperties.Assembly
            && (reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || reference.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            && Path.IsPathRooted(reference))
        {
            var direct = KernelScriptReferences.GetOrCreateFromFile(reference);
            if (direct is not null)
                return [direct];
        }
        var resolved = inner.ResolveReference(reference, baseFilePath, properties);
        if (resolved.IsDefaultOrEmpty) return resolved;
        return resolved.Select(r => KernelScriptReferences.Share(r)!).ToImmutableArray();
    }

    public override bool Equals(object? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => typeof(SharedScriptMetadataResolver).GetHashCode();
}
