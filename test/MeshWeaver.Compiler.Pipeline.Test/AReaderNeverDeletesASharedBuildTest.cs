using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🔴 <b>memex.systemorph.com, 2026-10-08 — every roll deleted the shared builds the running
/// replicas were serving</b> (#6161, #6052).
///
/// <para><c>NodeAssemblyLoadContext.LoadNodeAssembly</c> compared the file's write time with the
/// framework DLL's and DELETED the file "for regeneration" when it was older. A new image's
/// framework DLL is newer than every build compiled before the image existed, so on the ReadWriteMany
/// <c>/data/assembly-cache</c> the first replica of each roll deleted the current, compatible,
/// same-framework-tag builds (~180 at the ci.10245 boot; <c>Store_Plugin/v19020-c003e001-….dll</c>
/// at the ci.10256 boot). Each was a store miss, a recompile on the owner and a new record version;
/// for <c>Store/Plugin</c> every installed package's root overlaid while it ran and logged "stuck
/// AGAIN"; and the record then named a LOCAL build no shipped bundle carries, so the registry
/// refetch (#6262) could never land one.</para>
///
/// <para><b>The contract.</b> A build the store serves is loaded whatever its write time, and a
/// READER never deletes it. Generation is decided by identity before the path reaches the loader —
/// the store's framework tag, the record's <c>CompiledFrameworkVersion</c>, the disk cache's own
/// framework-time check — never by a timestamp inside it.</para>
///
/// <para><b>Negative control</b> (run by hand against the pre-fix loader): both write-time arms
/// answer <c>null</c> and the file is gone; the fresh-write control loads either way, so the bytes
/// are loadable and only the write time decides.</para>
/// </summary>
public sealed class AReaderNeverDeletesASharedBuildTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MeshWeaverReaderNeverDeletes", $"t_{Guid.NewGuid():N}");

    public AReaderNeverDeletesASharedBuildTest() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>The instant the running framework was "built" — what the pre-fix check compared to.</summary>
    private static DateTime FrameworkWriteTime()
        => File.GetLastWriteTimeUtc(typeof(CompilationCacheService).Assembly.Location);

    private static byte[] LoadableAssembly(string name)
    {
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText("namespace Probe { public sealed class Marker { } }")],
            PlatformReferences.Platform(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }

    /// <summary>
    /// The production path: bytes PUT into the shared store under this framework's tag, then the
    /// replica's clock moves on — a new image boots with a newer framework DLL — and the record's
    /// path is resolved and loaded through the compilation cache, as activation does.
    /// </summary>
    [Fact]
    public async Task AStoreBuildOlderThanTheRunningImage_IsLoaded_AndStaysOnTheShare()
    {
        var store = new FileSystemAssemblyStore(Path.Combine(_root, "assembly-cache"),
            NullLogger<FileSystemAssemblyStore>.Instance);
        var location = await store.PutWithLocation("Store/Plugin", 19020, LoadableAssembly("DynamicNode_Store_Plugin"), null)
            .Timeout(TestTimeouts.Convergence).Await();
        Path.GetFileName(location.LocalPath).Should().Contain($"-{FileSystemAssemblyStore.FrameworkTag}-",
            "the store keys the file by the generation it serves — that, not a write time, is its generation");
        // The ci.10249 compile at 15:00 under the ci.10256 framework built at 15:54.
        File.SetLastWriteTimeUtc(location.LocalPath, FrameworkWriteTime().AddMinutes(-54));

        var resolved = await store.TryGetBuildPath("Store/Plugin", 19020, null, null)
            .Timeout(TestTimeouts.Convergence).Await();
        resolved.Should().Be(location.LocalPath, "the store serves its own generation's file");

        var cache = new CompilationCacheService(
            Options.Create(new CompilationCacheOptions { CacheDirectory = Path.Combine(_root, "mesh-cache") }),
            NullLogger<CompilationCacheService>.Instance);
        try
        {
            using var pinned = cache.PinForScan("Store_Plugin", resolved);
            pinned.Context.LoadNodeAssembly().Should().NotBeNull(
                "a compatible build the store serves must bind — a recompile here is the per-roll churn of #6161; "
                + $"the loader said: {pinned.Context.LastLoadFailure}");
        }
        finally
        {
            cache.Dispose();
        }
        File.Exists(location.LocalPath).Should().BeTrue(
            "a READER must never delete a build every other replica on the share is serving");
    }

    /// <summary>The loader alone, with the control that makes the arm mean something.</summary>
    [Fact]
    public void TheLoader_DoesNotJudgeOrDeleteByWriteTime()
    {
        var bytes = LoadableAssembly("DynamicNode_Probe");
        var stale = Path.Combine(_root, $"v1-{FileSystemAssemblyStore.FrameworkTag}-aaaaaaaaaaaa.dll");
        var fresh = Path.Combine(_root, $"v2-{FileSystemAssemblyStore.FrameworkTag}-bbbbbbbbbbbb.dll");
        File.WriteAllBytes(stale, bytes);
        File.WriteAllBytes(fresh, bytes);
        File.SetLastWriteTimeUtc(stale, FrameworkWriteTime().AddDays(-1));
        File.SetLastWriteTimeUtc(fresh, FrameworkWriteTime().AddMinutes(1));

        using (var control = new NodeAssemblyLoadContext("Probe_Fresh", fresh))
            control.LoadNodeAssembly().Should().NotBeNull("CONTROL: the bytes are loadable");

        using (var context = new NodeAssemblyLoadContext("Probe_Stale", stale))
        {
            context.LoadNodeAssembly().Should().NotBeNull(
                $"the same bytes with an older write time are the same build; the loader said: {context.LastLoadFailure}");
            context.LastLoadFailure.Should().BeNull();
        }
        File.Exists(stale).Should().BeTrue("the loader must not delete a loadable file");
    }
}
