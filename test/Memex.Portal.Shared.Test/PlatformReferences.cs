using System.Collections.Immutable;
using MeshWeaver.Compiler;
using Microsoft.CodeAnalysis;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The reference set a test's Roslyn <c>Emit</c> compiles against — the process-wide
/// <see cref="CompileReferences.Default"/>, filtered, never rebuilt.
///
/// <para>🚨 Three test classes used to build this list themselves, per call:
/// <c>MetadataReference.CreateFromFile</c> over every entry of <c>TRUSTED_PLATFORM_ASSEMBLIES</c>
/// — about 520 assemblies, ~180 MB of PE images — for EACH of their 34 compilations. Roslyn reads
/// every reference's metadata when it binds the compilation, so each <c>Emit</c> re-read the whole
/// platform into memory that only a finalizer releases. On CI (#4127) that was the shape of the
/// two RSS steps the memory watchdog's trace shows — <c>+1.8 GB</c> in the eleven seconds
/// <c>ModuleLinkVersionTest</c> ran (11 emits) and <c>+3.9 GB</c> in the four seconds
/// <c>ModulePlatformLinkTest</c> ran (19 emits), with no <c>MonolithMeshTestBase</c> class active
/// and every mesh class before and after them flat — and the watchdog then <c>FailFast</c>ed the
/// host blaming a cumulative Autofac leak its own trace refutes. <see cref="CompileReferences.Default"/>
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
