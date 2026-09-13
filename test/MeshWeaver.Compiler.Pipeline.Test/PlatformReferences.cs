using System.Collections.Immutable;
using MeshWeaver.Compiler;
using Microsoft.CodeAnalysis;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The reference set a test's Roslyn compile is built against — the process-wide
/// <see cref="CompileReferences.Default"/>, filtered, never rebuilt.
///
/// <para>🚨 Five test classes used to build this list themselves — per <c>Emit</c> call, or as an
/// instance field xUnit re-creates for every test: <c>MetadataReference.CreateFromFile</c> over
/// every entry of <c>TRUSTED_PLATFORM_ASSEMBLIES</c>, about 520 assemblies and ~180 MB of PE
/// images each time. Roslyn reads every reference's metadata when it binds the compilation, so
/// each compile re-read the whole platform into memory only a finalizer releases. On CI (#4127)
/// this host was <c>FailFast</c>ed by the memory watchdog at 6.3 GB RSS with 41 MB of managed
/// heap, blamed on a cumulative Autofac leak — the sibling occurrence's trace
/// (<c>Memex.Portal.Shared.Test</c>, same run) shows the growth as two steps that coincide
/// exactly with its Roslyn-emitting classes and nothing else. <see cref="CompileReferences.Default"/>
/// is what a NodeType compile uses in production: built once, and Roslyn caches each reference's
/// metadata and symbol table against the instance, so the platform is read once per process.</para>
/// </summary>
internal static class PlatformReferences
{
    /// <summary>
    /// The platform's references, optionally without the assembly named <paramref name="excludeReference"/>
    /// (file name without extension) — the way a test stands in a different build of one contract.
    /// </summary>
    /// <param name="excludeReference">An assembly to leave out, or <c>null</c> for the whole set.</param>
    /// <returns>The references, in the order the platform lists them — immutable; a caller that
    /// needs one more reassigns <c>platform = platform.Add(extra)</c>.</returns>
    public static ImmutableList<MetadataReference> Platform(string? excludeReference = null) =>
        CompileReferences.Default
            .Where(r => r.Display is { } path
                        && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && (excludeReference is null
                            || !string.Equals(Path.GetFileNameWithoutExtension(path), excludeReference,
                                StringComparison.OrdinalIgnoreCase)))
            .ToImmutableList();
}
