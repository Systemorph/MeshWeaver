using System.Runtime.CompilerServices;
using FluentAssertions;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pre-warms FluentAssertions' test-framework detection BEFORE any test runs.
///
/// <para>FluentAssertions 8.9 lazily detects the test framework on the first
/// assertion by scanning <see cref="System.AppDomain.CurrentDomain"/> via
/// <c>TestFrameworkFactory.AttemptToDetectUsingDynamicScanning</c> — it calls
/// <c>Assembly.GetName()</c> on every loaded assembly. Once detected, the
/// result is cached; subsequent assertions skip the scan.</para>
///
/// <para>The hazard: dynamic NodeType assemblies loaded into collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>s by earlier tests
/// can be in a half-unloaded state when the scan happens — their backing DLL
/// has been deleted (test-cache cleanup) but the assembly is still listed in
/// <c>AppDomain.GetAssemblies()</c>. Calling <c>GetName()</c> on such an
/// assembly throws <c>BadImageFormatException: "Index not found."</c>, the
/// scan aborts, and the test that triggered it dies before its body runs.
/// Repro: <c>CreatableTypesFileSystemTest.FileSystem_VerifyDataStructure</c>
/// on CI (Linux) when it runs after compile-heavy test classes have
/// accumulated zombie ALCs in the test-host process.</para>
///
/// <para>Fix: trigger the detection NOW, at module load — only the test-host's
/// own startup assemblies are loaded at this point, so the scan completes
/// cleanly and the result is cached. Subsequent assertions reuse the cache
/// and never re-scan, no matter how many dynamic ALCs accumulate.</para>
/// </summary>
internal static class TestModuleInit
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // Cheapest possible FluentAssertions call — forces framework detection.
        try { 1.Should().Be(1); }
        catch { /* defensive — we only care about the side effect (caching) */ }
    }
}
