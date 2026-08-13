using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using MeshWeaver.Graph.Configuration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The publication contract of <see cref="MeshNodeCompilationService.EmitToDiskWithRetry"/>:
/// <b>an artifact becomes discoverable only when the file on disk IS the image the emit produced</b>,
/// and a lost write is re-emitted rather than poisoning the NodeType.
///
/// <para>The lost-write half reproduces the prod <c>AgenticPension/Datenpunkt</c> failure
/// (2026-06-22): a Roslyn emit reports success but the assembly is missing on disk afterward (the
/// ephemeral container <c>/tmp</c> cache evicted the just-written file), which used to poison the
/// NodeType with a permanent "Compilation succeeded but DLL not found".</para>
///
/// <para>The integrity half is MeshWeaver#1412. The acceptance test used to be
/// <c>File.Exists(dll) &amp;&amp; new FileInfo(dll).Length &gt; 0</c> — "non-empty", not "the bytes we
/// emitted" — so a truncated PE, or a full-length image with an unwritten region inside its
/// metadata, was published under the name readers discover by and handed straight to
/// <c>AssemblyLoadContext.LoadFromAssemblyPath</c>. Neither is survivable downstream:
/// <c>CompileResultFromAssembly</c> records the load/scan fault as <c>CompilationStatus.Error</c>
/// and the first-build kickoff is gated on <c>Status == null</c>, so it never retries — the bytes
/// may heal, the verdict does not, and the NodeType is PARKED.</para>
/// </summary>
public class EmitToDiskWithRetryTest : IDisposable
{
    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), $"mesh-emit-retry-{Guid.NewGuid():N}");

    public EmitToDiskWithRetryTest() => Directory.CreateDirectory(_cacheDir);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_cacheDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// A REAL compiled assembly, shaped like a dynamic NodeType's output (a record plus a type that
    /// consumes it). Placeholder bytes cannot be used any more, and that is the point: the publisher
    /// now proves the artifact is the emitted image, so a test that stages <c>MZ\0\0</c> and claims
    /// it emitted an assembly would be asserting the very contract this fixes.
    /// </summary>
    internal static byte[] RealAssemblyBytes(string assemblyName = "DynamicNode_TestWidget")
    {
        var tree = CSharpSyntaxTree.ParseText(
            """
            public record Widget { public string Title { get; init; } = string.Empty; }
            public class WidgetProvider
            {
                public string Describe(Widget w) => w.Title;
                public int Count { get; set; }
            }
            """);

        var references = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName, [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success,
            "the fixture assembly must compile: "
            + string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return ms.ToArray();
    }

    /// <summary>Stages <paramref name="image"/> as an honest emit would: write it, report its digest.</summary>
    private static EmittedArtifact Stage(string releaseDir, string nodeName, byte[] image)
    {
        var dllPath = Path.Combine(releaseDir, $"{nodeName}.dll");
        File.WriteAllBytes(dllPath, image);
        return EmittedArtifact.For(dllPath, image);
    }

    /// <summary>
    /// Stages a DAMAGED file while reporting the digest of the image the emit produced — the shape of
    /// every real defect here: Roslyn emitted a good image, the bytes that reached disk are not it.
    /// </summary>
    private static EmittedArtifact StageDamaged(
        string releaseDir, string nodeName, byte[] emitted, byte[] onDisk)
    {
        var dllPath = Path.Combine(releaseDir, $"{nodeName}.dll");
        File.WriteAllBytes(dllPath, onDisk);
        return EmittedArtifact.For(dllPath, emitted);
    }

    [Fact]
    public void Happy_path_persists_on_first_attempt()
    {
        const string nodeName = "Demo_Happy";
        var attempts = 0;

        var result = MeshNodeCompilationService.EmitToDiskWithRetry(
            _cacheDir, nodeName, maxAttempts: 3, NullLogger.Instance,
            releaseDir => { attempts++; return Stage(releaseDir, nodeName, RealAssemblyBytes()); });

        attempts.Should().Be(1, "a persisted artifact needs no re-emit");
        File.Exists(result).Should().BeTrue();
        LoaderVerdict(result).Should().BeNull("the published artifact must load and enumerate");
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
                var staged = Stage(releaseDir, nodeName, RealAssemblyBytes());
                if (attempts == 1)
                    File.Delete(staged.DllPath); // simulate the ephemeral-/tmp eviction
                return staged;
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
        var whole = RealAssemblyBytes();

        var result = MeshNodeCompilationService.EmitToDiskWithRetry(
            _cacheDir, nodeName, maxAttempts: 3, NullLogger.Instance,
            releaseDir =>
            {
                attempts++;
                return attempts == 1
                    ? StageDamaged(releaseDir, nodeName, whole, [])
                    : Stage(releaseDir, nodeName, whole);
            });

        attempts.Should().Be(2);
        new FileInfo(result).Length.Should().Be(whole.Length);
    }

    /// <summary>
    /// 🚨 #1412. A SHORT-but-non-empty artifact passed the old <c>Length &gt; 0</c> gate and was
    /// published. It is not a survivable state: the loader throws <c>BadImageFormatException</c>,
    /// <c>LoadNodeAssembly</c> returns null, and the compile settles at a terminal
    /// <c>CompilationStatus.Error</c> that the kickoff never retries.
    /// </summary>
    [Fact]
    public void Re_emits_when_the_staged_artifact_is_truncated()
    {
        const string nodeName = "Demo_TruncatedPe";
        var attempts = 0;
        var whole = RealAssemblyBytes();

        var result = MeshNodeCompilationService.EmitToDiskWithRetry(
            _cacheDir, nodeName, maxAttempts: 3, NullLogger.Instance,
            releaseDir =>
            {
                attempts++;
                return attempts == 1
                    ? StageDamaged(releaseDir, nodeName, whole, whole[..(whole.Length / 2)])
                    : Stage(releaseDir, nodeName, whole);
            });

        attempts.Should().Be(2, "a truncated image is not a publishable artifact — it must re-emit");
        new FileInfo(result).Length.Should().Be(whole.Length);
        LoaderVerdict(result).Should().BeNull("the published artifact must load and enumerate");
    }

    /// <summary>
    /// 🚨 #1412's actual signature. A full-LENGTH image with an unwritten region inside its metadata
    /// LOADS — the header and assembly identity are intact — and throws
    /// <c>ReflectionTypeLoadException … "because the format is invalid"</c> on the first
    /// <c>GetTypes()</c>, which is what parks the NodeType. A length check cannot see it; comparing
    /// against the emitted image can.
    /// </summary>
    [Fact]
    public void Re_emits_when_the_staged_artifact_is_full_length_but_corrupt_inside()
    {
        const string nodeName = "Demo_MetadataHole";
        var attempts = 0;
        var whole = RealAssemblyBytes();
        var holed = WithMetadataHole(whole);
        holed.Length.Should().Be(whole.Length, "the corruption must be invisible to a length check");
        LoaderVerdict(WriteTemp(holed)).Should().NotBeNull(
            "the fixture must be damage the real loader actually refuses");

        var result = MeshNodeCompilationService.EmitToDiskWithRetry(
            _cacheDir, nodeName, maxAttempts: 3, NullLogger.Instance,
            releaseDir =>
            {
                attempts++;
                return attempts == 1
                    ? StageDamaged(releaseDir, nodeName, whole, holed)
                    : Stage(releaseDir, nodeName, whole);
            });

        attempts.Should().Be(2, "an image that is not what we emitted must re-emit, not publish");
        LoaderVerdict(result).Should().BeNull("the published artifact must load and enumerate");
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
                    var staged = Stage(releaseDir, nodeName, RealAssemblyBytes());
                    File.Delete(staged.DllPath); // every attempt loses the artifact
                    return staged;
                });
        });

        ex.Message.Should().Contain("could not be published");
        ex.Message.Should().Contain(nodeName);
        ex.NodePath.Should().Be(nodeName);
        attempts.Should().Be(3, "it must exhaust all attempts before failing loudly");
    }

    /// <summary>
    /// A persistently damaged artifact must fail LOUDLY and say what was wrong — not park the
    /// NodeType behind a generic "could not be persisted" that sends the operator looking for a
    /// read-only cache directory.
    /// </summary>
    [Fact]
    public void Throws_naming_the_damage_when_every_attempt_is_corrupt()
    {
        const string nodeName = "Demo_AlwaysCorrupt";
        var attempts = 0;
        var whole = RealAssemblyBytes();
        var holed = WithMetadataHole(whole);

        var ex = Assert.Throws<CompilationException>(() =>
            MeshNodeCompilationService.EmitToDiskWithRetry(
                _cacheDir, nodeName, maxAttempts: 3, NullLogger.Instance,
                releaseDir =>
                {
                    attempts++;
                    return StageDamaged(releaseDir, nodeName, whole, holed);
                }));

        attempts.Should().Be(3);
        ex.Message.Should().Contain("last failure:",
            "the terminal error must carry WHY the last publish was refused");
        ex.Message.Should().Contain("differs from the emitted image");
        Directory.GetDirectories(_cacheDir, $"{nodeName}_*").Should()
            .BeEmpty("a damaged artifact must never reach the discovery namespace");
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
            releaseDir => Stage(releaseDir, nodeName, RealAssemblyBytes()));

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

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(_cacheDir, $"probe-{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(p, bytes);
        return p;
    }

    /// <summary>Zeroes bytes in the middle of the image's CLI metadata, keeping the length.</summary>
    internal static byte[] WithMetadataHole(byte[] image, double position = 0.5, int holeBytes = 64)
    {
        int mdOffset, mdSize;
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            mdOffset = pe.PEHeaders.MetadataStartOffset;
            mdSize = pe.PEHeaders.MetadataSize;
        }
        var copy = (byte[])image.Clone();
        var start = Math.Min(mdOffset + (int)(mdSize * position), image.Length - holeBytes);
        Array.Clear(copy, start, holeBytes);
        return copy;
    }

    /// <summary>
    /// What the REAL reader does with this file: load it into a collectible context and enumerate
    /// its types, exactly as <c>CompileResultFromAssembly</c> does. Returns null when that succeeds,
    /// otherwise the failure text — so a test can assert on the loader's verdict rather than on a
    /// guess about which corruptions matter.
    /// </summary>
    internal static string? LoaderVerdict(string dllPath)
    {
        var alc = new AssemblyLoadContext($"verdict-{Guid.NewGuid():N}", isCollectible: true);
        try
        {
            var assembly = alc.LoadFromAssemblyPath(dllPath);
            var types = assembly.GetTypes();
            return types.Length == 0 ? "the assembly exposed no types" : null;
        }
        catch (ReflectionTypeLoadException ex)
        {
            return string.Join("; ", ex.LoaderExceptions
                .Where(e => e is not null).Select(e => e!.Message).Distinct());
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            alc.Unload();
        }
    }
}

/// <summary>
/// 🚨 The census behind MeshWeaver#1412, in the shape #1387 used for the assembly store: over a
/// population of damaged images, measure how many the OLD acceptance test (<c>Length &gt; 0</c>)
/// would have published and how many of those the REAL loader then cannot use. Every one of the
/// latter is a permanently parked NodeType, because <c>CompileResultFromAssembly</c> records the
/// fault as <c>CompilationStatus.Error</c> and the first-build kickoff never retries.
///
/// <para>The gate is asserted against the loader's own verdict, never against a guess about which
/// byte ranges matter — a metadata-walking approximation was tried first and measurably leaked (19
/// of these samples load-fail yet parse cleanly), which is why the shipped gate compares the
/// artifact with the image that was emitted instead.</para>
/// </summary>
public class PublishedAssemblyIsCompleteTest
{
    [Fact(Timeout = 120_000)]
    public void No_damaged_artifact_may_pass_the_publication_gate()
    {
        var whole = EmitToDiskWithRetryTest.RealAssemblyBytes();
        int mdOffset, mdSize;
        using (var pe = new PEReader(new MemoryStream(whole)))
        {
            mdOffset = pe.PEHeaders.MetadataStartOffset;
            mdSize = pe.PEHeaders.MetadataSize;
        }

        var samples = new List<(string Name, byte[] Bytes)> { ("empty", []) };
        foreach (var keep in new[] { 0.05, 0.25, 0.5, 0.75, 0.95, 0.99 })
            samples.Add(($"truncated to {keep:P0}", whole[..(int)(whole.Length * keep)]));
        // Full-LENGTH damage: a 64-byte unwritten region swept across the metadata. This is the
        // shape that loads and then throws "…because the format is invalid" at GetTypes().
        for (var off = mdOffset; off < mdOffset + mdSize - 64; off += 64)
        {
            var copy = (byte[])whole.Clone();
            Array.Clear(copy, off, 64);
            samples.Add(($"metadata hole at +{off - mdOffset}", copy));
        }

        var dir = Path.Combine(Path.GetTempPath(), $"mesh-image-census-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var loaderRefused = 0;
            var oldGateWouldPublish = 0;
            var escapes = new List<string>();

            foreach (var (name, bytes) in samples)
            {
                var path = Path.Combine(dir, $"s{Guid.NewGuid():N}.dll");
                File.WriteAllBytes(path, bytes);

                // The gate as it stood before #1412: present and non-empty.
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    oldGateWouldPublish++;
                if (EmitToDiskWithRetryTest.LoaderVerdict(path) is not null)
                    loaderRefused++;

                // The gate as it stands now: this file is the image the emit produced.
                if (EmittedArtifact.For(path, whole).MatchesFileOnDisk(out _))
                    escapes.Add($"{name} ({bytes.Length} bytes)");
            }

            // The population has to be real, or the assertion below is vacuous.
            loaderRefused.Should().BeGreaterThan(10,
                $"the census must actually damage images ({samples.Count} samples)");
            oldGateWouldPublish.Should().BeGreaterThan(0,
                "if `Length > 0` had rejected every damaged image there would have been nothing to fix");

            escapes.Should().BeEmpty(
                "a damaged artifact must never be published "
                + $"({samples.Count} samples, {loaderRefused} the real loader refuses, "
                + $"{oldGateWouldPublish} the old `Length > 0` gate would have published):"
                + Environment.NewLine + string.Join(Environment.NewLine, escapes));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
