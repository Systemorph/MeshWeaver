using System.Runtime.CompilerServices;
using FluentAssertions;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pre-warms FluentAssertions' test-framework detection BEFORE any dynamic
/// ALC pollutes the assembly list.
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
/// on Linux CI when it runs after compile-heavy test classes have
/// accumulated zombie ALCs in the test-host process.</para>
///
/// <para>Fix: trigger the detection at module load — only the test-host's
/// own startup assemblies are loaded then, so the scan completes cleanly
/// and the result is cached. Subsequent assertions reuse the cache.</para>
///
/// <para>FluentAssertions' first call also writes its commercial-license
/// notice to <see cref="System.System.Console.Out"/>. xUnit v3's test runner reads
/// the test-host's stdout as JSON for discovery; the license preamble
/// breaks JSON parsing and causes "catastrophic failure: Test process did
/// not return valid JSON". We redirect stdout into a discarded
/// <see cref="System.IO.TextWriter"/> for the duration of the warmup so
/// the notice doesn't reach the parent process.</para>
/// </summary>
internal static class TestModuleInit
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var originalOut = System.Console.Out;
        try
        {
            System.Console.SetOut(System.IO.TextWriter.Null);
            try { 1.Should().Be(1); }
            catch { /* defensive — we only care about the side effect (caching) */ }
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }
    }
}
