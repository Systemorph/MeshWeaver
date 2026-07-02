using System;
using System.IO;
using MeshWeaver.Graph.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for the disk-emit self-heal
/// (<see cref="MeshNodeCompilationService.EmitToDiskWithRetry"/>).
///
/// Reproduces the atioz <c>AgenticPension/Datenpunkt</c> failure (2026-06-22): a Roslyn emit
/// reports success but the assembly is missing on disk afterward (the ephemeral container
/// <c>/tmp</c> cache evicted the just-written file). That used to poison the NodeType with a
/// permanent "Compilation succeeded but DLL not found" error that no recompile could clear.
/// The fix re-emits the lost artifact; an unrecoverable loss surfaces a clear, loud failure.
/// </summary>
public class EmitToDiskWithRetryTest : IDisposable
{
    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), $"mesh-emit-retry-{Guid.NewGuid():N}");

    public EmitToDiskWithRetryTest() => Directory.CreateDirectory(_cacheDir);

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>Writes a non-empty placeholder assembly into the release dir, like a real emit.</summary>
    private static string WriteAssembly(string releaseDir, string nodeName)
    {
        var dllPath = Path.Combine(releaseDir, $"{nodeName}.dll");
        File.WriteAllBytes(dllPath, new byte[] { 0x4D, 0x5A, 0x90, 0x00 }); // MZ header-ish, non-empty
        return dllPath;
    }

    [Fact]
    public void Happy_path_persists_on_first_attempt()
    {
        const string nodeName = "Demo_Happy";
        var attempts = 0;

        var result = MeshNodeCompilationService.EmitToDiskWithRetry(
            _cacheDir, nodeName, maxAttempts: 3, NullLogger.Instance,
            releaseDir => { attempts++; return WriteAssembly(releaseDir, nodeName); });

        attempts.Should().Be(1, "a persisted artifact needs no re-emit");
        File.Exists(result).Should().BeTrue();
        new FileInfo(result).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Re_emits_when_first_artifact_is_lost_then_succeeds()
    {
        // The exact broken case: emit "succeeds" but the file is gone afterward on attempt 1,
        // then a normal emit on attempt 2 persists. The NodeType must end up compiled, NOT poisoned.
        const string nodeName = "Demo_Datenpunkt";
        var attempts = 0;

        var result = MeshNodeCompilationService.EmitToDiskWithRetry(
            _cacheDir, nodeName, maxAttempts: 3, NullLogger.Instance,
            releaseDir =>
            {
                attempts++;
                var dllPath = WriteAssembly(releaseDir, nodeName);
                if (attempts == 1)
                    File.Delete(dllPath); // simulate the ephemeral-/tmp eviction
                return dllPath;
            });

        attempts.Should().Be(2, "the lost first artifact must trigger exactly one re-emit");
        File.Exists(result).Should().BeTrue("the second emit's assembly must persist");
        new FileInfo(result).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Treats_empty_artifact_as_lost_and_re_emits()
    {
        // A zero-byte DLL (truncated write) is as broken as a missing one — it must re-emit.
        const string nodeName = "Demo_Truncated";
        var attempts = 0;

        var result = MeshNodeCompilationService.EmitToDiskWithRetry(
            _cacheDir, nodeName, maxAttempts: 3, NullLogger.Instance,
            releaseDir =>
            {
                attempts++;
                var dllPath = Path.Combine(releaseDir, $"{nodeName}.dll");
                if (attempts == 1)
                    File.WriteAllBytes(dllPath, Array.Empty<byte>()); // empty == broken
                else
                    File.WriteAllBytes(dllPath, new byte[] { 1, 2, 3, 4 });
                return dllPath;
            });

        attempts.Should().Be(2);
        new FileInfo(result).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Throws_clear_error_when_artifact_never_persists()
    {
        const string nodeName = "Demo_NeverPersists";
        var attempts = 0;

        var ex = Assert.Throws<CompilationException>(() =>
        {
            MeshNodeCompilationService.EmitToDiskWithRetry(
                _cacheDir, nodeName, maxAttempts: 3, NullLogger.Instance,
                releaseDir =>
                {
                    attempts++;
                    var dllPath = WriteAssembly(releaseDir, nodeName);
                    File.Delete(dllPath); // every attempt loses the artifact
                    return dllPath;
                });
        });

        ex.Message.Should().Contain("could not be persisted");
        ex.Message.Should().Contain(nodeName);
        ex.NodePath.Should().Be(nodeName);
        attempts.Should().Be(3, "it must exhaust all attempts before failing loudly");
    }

    [Fact]
    public void Does_not_retry_a_genuine_compile_error()
    {
        // A real Roslyn error is deterministic — re-emitting would just burn attempts and
        // bury the diagnostics. It must propagate immediately, on the first attempt.
        const string nodeName = "Demo_CompileError";
        var attempts = 0;

        var ex = Assert.Throws<CompilationException>(() =>
        {
            MeshNodeCompilationService.EmitToDiskWithRetry(
                _cacheDir, nodeName, maxAttempts: 3, NullLogger.Instance,
                releaseDir =>
                {
                    attempts++;
                    throw new CompilationException(nodeName, "CS1002: ; expected");
                });
        });

        ex.Message.Should().Contain("; expected");
        attempts.Should().Be(1, "a deterministic compile error must NOT be retried");
    }

    [Fact]
    public void Successful_emit_publishes_to_a_discoverable_dir_and_leaves_no_staging()
    {
        // Atomic-publish contract: the artifact is emitted into a NON-discoverable staging dir and
        // renamed into the `{nodeName}_*` discovery namespace only once complete, so a concurrent
        // TryGetLatestCachedDllPath never sees a half-written DLL (loading a truncated image is a
        // native crash / BadImageFormat). After success exactly one discoverable dir holds the DLL
        // and no `.staging-*` dir is left behind.
        const string nodeName = "Demo_AtomicPublish";

        var result = MeshNodeCompilationService.EmitToDiskWithRetry(
            _cacheDir, nodeName, maxAttempts: 3, NullLogger.Instance,
            releaseDir => WriteAssembly(releaseDir, nodeName));

        File.Exists(result).Should().BeTrue();
        Path.GetDirectoryName(result)!.Should().NotContain(".staging-");
        Directory.GetDirectories(_cacheDir, $"{nodeName}_*").Should()
            .ContainSingle("the published artifact lives in exactly one discoverable dir");
        Directory.GetDirectories(_cacheDir, ".staging-*").Should()
            .BeEmpty("staging is renamed away on publish, never left behind");
    }

    [Fact]
    public void Compile_error_leaves_no_discoverable_partial_artifact()
    {
        // A failed emit must NOT pollute the discovery namespace with a partial DLL — the staging dir
        // (and any half-written bytes in it) is discarded, so TryGetLatestCachedDllPath can never pick
        // up the wreckage of a failed compile.
        const string nodeName = "Demo_ErrorNoPartial";

        Assert.Throws<CompilationException>(() =>
            MeshNodeCompilationService.EmitToDiskWithRetry(
                _cacheDir, nodeName, maxAttempts: 3, NullLogger.Instance,
                releaseDir =>
                {
                    // Roslyn wrote a partial DLL, then the compile failed.
                    File.WriteAllBytes(Path.Combine(releaseDir, $"{nodeName}.dll"), new byte[] { 0x4D, 0x5A });
                    throw new CompilationException(nodeName, "CS1002: ; expected");
                }));

        Directory.GetDirectories(_cacheDir, $"{nodeName}_*").Should()
            .BeEmpty("a failed compile must leave no discoverable artifact");
        Directory.GetDirectories(_cacheDir, ".staging-*").Should()
            .BeEmpty("the failed staging dir is cleaned up");
    }
}
