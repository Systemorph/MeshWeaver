using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the one property <see cref="FileSystemAssemblyStore"/>'s two halves have to agree on:
/// <b>a compiled assembly must not be DISCOVERABLE until it is COMPLETE.</b>
///
/// <para><see cref="FileSystemAssemblyStore.TryGetAssemblyPath"/> finds an assembly by globbing
/// <c>v{version}-{frameworkTag}-*.dll</c>, so the DLL's file name IS its publication — and the
/// path it returns is handed straight to <c>AssemblyLoadContext.LoadFromAssemblyPath</c>.
/// <c>File.WriteAllBytes</c> opens with <c>FileMode.Create</c>: it creates the target FIRST and
/// streams the bytes afterwards, so writing straight to the final name publishes a 0-byte file and
/// fills it in later. A reader that globs inside that window does not get an empty string, it gets
/// a <b>truncated PE image</b> — the header is intact, the load succeeds, and the first
/// <c>Assembly.GetTypes()</c> throws
/// <i>"Could not load type 'X' from assembly 'DynamicNode_…' because the format is invalid"</i>.</para>
///
/// <para>That is not hypothetical: it is MeshWeaver#1387, seen on a CI shard as
/// <c>ReflectionTypeLoadException … NodeType 'TestData/HighWaterType' PARKED after compile
/// failure</c>. And the consequence is terminal, not transient —
/// <c>MeshNodeCompilationService.CompileResultFromAssembly</c> records that load fault as
/// <c>CompilationStatus.Error</c> and the first-build kickoff (gated on <c>Status == null</c>) does
/// not retry, so ONE torn read parks the NodeType until the file is deleted by hand, and a parked
/// NodeType refuses portal readiness. On AKS the cache is <c>/data/assembly-cache</c>, a
/// ReadWriteMany Azure Files share, so the racing reader is usually a different POD.</para>
///
/// <para>🚨 The assertion is on the SIZE of a DISCOVERED assembly, never on write throughput or
/// timing, so it cannot be satisfied by making anything faster — only by making publication atomic.
/// The observation count is asserted non-zero so a probe that never sampled a published assembly
/// cannot pass vacuously, and two deterministic assertions run afterwards regardless of what the
/// probe saw.</para>
/// </summary>
public class FileSystemAssemblyStoreAtomicityTest : IDisposable
{
    private const string NodeTypePath = "AtomicType";
    private const int Versions = 12;

    /// <summary>
    /// Big enough that the byte stream needs several chunks, which is what opens the window between
    /// "file exists under its final, globbable name" and "file holds the whole image". Calibrated
    /// the same way as <see cref="FileSystemVersionStoreAtomicityTest"/>'s payload, and sized so the
    /// whole test writes ~48 MB.
    /// </summary>
    private const int PayloadBytes = 4 * 1024 * 1024;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MeshWeaverAssemblyAtomicity", $"t_{Guid.NewGuid():N}");

    public FileSystemAssemblyStoreAtomicityTest() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Distinct bytes per version so a reader cannot pass by reading someone else's file.</summary>
    private static byte[] Payload(long version)
    {
        var bytes = new byte[PayloadBytes];
        Array.Fill(bytes, (byte)version);
        return bytes;
    }

    private FileSystemAssemblyStore NewStore() =>
        new(_root, NullLogger<FileSystemAssemblyStore>.Instance);

    /// <summary>The exact glob <see cref="FileSystemAssemblyStore.TryGetAssemblyPath"/> discovers by.</summary>
    private static string DiscoveryGlob(long version) =>
        $"v{version}-{FileSystemAssemblyStore.FrameworkTag}-*.dll";

    [Fact(Timeout = 120000)]
    public async Task ADiscoveredAssemblyIsAlwaysComplete()
    {
        var store = NewStore();
        var typeDirectory = Path.Combine(_root, NodeTypePath);

        var writesDone = false;
        var observations = 0;
        var failures = new List<string>();

        // Watches exactly the name space TryGetAssemblyPath globs, with nothing in the loop but the
        // two syscalls, so it samples the publication window rather than spending it elsewhere. A
        // DLL the glob can see must already hold its whole image; any short length IS the truncated
        // PE one step before AssemblyLoadContext meets it.
        var probe = Task.Run(() =>
        {
            while (!Volatile.Read(ref writesDone))
            {
                if (!Directory.Exists(typeDirectory)) continue;
                for (var v = 1; v <= Versions; v++)
                {
                    FileInfo[] listed;
                    try { listed = new DirectoryInfo(typeDirectory).GetFiles(DiscoveryGlob(v)); }
                    catch { continue; }

                    foreach (var f in listed)
                    {
                        long length;
                        try { length = new FileInfo(f.FullName).Length; } catch { continue; }
                        Interlocked.Increment(ref observations);
                        if (length != PayloadBytes)
                            lock (failures)
                                failures.Add(
                                    $"{f.Name} was discoverable at {length} of {PayloadBytes} bytes");
                    }
                }
            }
        });

        for (var v = 1; v <= Versions; v++)
            await store.Put(NodeTypePath, v, Payload(v), pdbBytes: null).FirstAsync();

        Volatile.Write(ref writesDone, true);
        await probe;

        Assert.True(Volatile.Read(ref observations) > 0,
            "the probe never discovered a published assembly at all, so it proved nothing");
        Assert.True(failures.Count == 0,
            "an assembly must not become discoverable before its bytes are on disk "
            + $"({observations} observations, {failures.Distinct().Count()} distinct violations):"
            + Environment.NewLine + string.Join(Environment.NewLine, failures.Distinct()));

        // …and every version the writes leave behind must round-trip through the store's own API.
        // Deterministic, so a probe that happened to sample nothing still cannot make this vacuous.
        for (var v = 1; v <= Versions; v++)
        {
            var path = await store.TryGetAssemblyPath(NodeTypePath, v).FirstAsync();
            Assert.NotNull(path);
            var bytes = await File.ReadAllBytesAsync(path!);
            Assert.Equal(PayloadBytes, bytes.Length);
            Assert.Equal((byte)v, bytes[^1]);
        }
    }

    /// <summary>
    /// The staging name must be invisible to the store's own discovery glob — a temp that still
    /// matched <c>v{version}-{tag}-*.dll</c> would be handed to the assembly loader as if it were a
    /// published assembly, which is the very failure this is preventing. Deterministic: it asserts
    /// the naming convention itself, not a sampled race.
    /// </summary>
    [Fact]
    public void TheStagingNameCannotMatchTheDiscoveryGlob()
    {
        var typeDirectory = Path.Combine(_root, NodeTypePath);
        Directory.CreateDirectory(typeDirectory);

        var target = Path.Combine(
            typeDirectory, $"v7-{FileSystemAssemblyStore.FrameworkTag}-0123456789ab.dll");
        var temp = AtomicFileWrite.TempPathFor(target);
        File.WriteAllBytes(temp, new byte[16]);

        Assert.Empty(new DirectoryInfo(typeDirectory).GetFiles(DiscoveryGlob(7)));
        Assert.Empty(new DirectoryInfo(typeDirectory).GetFiles("*.dll"));
    }

    /// <summary>
    /// 🚨 A genuine IO fault must NOT be mistaken for the benign "another writer won" race and
    /// swallowed into a <c>false</c> return. The two are told apart by the pre-check: having
    /// established the target did not exist, the only way it can exist when the move fails is a
    /// concurrent writer — a real fault leaves it absent, so it propagates.
    ///
    /// <para>Here the parent directory does not exist, so the write throws
    /// <see cref="DirectoryNotFoundException"/> (an <see cref="IOException"/>) with no target
    /// created. Returning <c>false</c> would tell <c>FileSystemAssemblyStore</c> the assembly was
    /// already published and hand back a path to nothing.</para>
    /// </summary>
    [Fact]
    public void AGenuineIoFailureIsNotSwallowedAsTheAlreadyPublishedRace()
    {
        var missingDirectory = Path.Combine(_root, "no-such-dir", "v1-tag-hash.dll");

        Assert.ThrowsAny<IOException>(() => AtomicFileWrite.PublishBytes(missingDirectory, new byte[8]));
        Assert.False(File.Exists(missingDirectory));
    }

    /// <summary>
    /// The other half of that contract: when the target genuinely IS already there, publishing
    /// reports <c>false</c> and leaves the incumbent's bytes untouched.
    /// </summary>
    [Fact]
    public void PublishingOverAnExistingTargetKeepsTheIncumbentAndReportsFalse()
    {
        var typeDirectory = Path.Combine(_root, NodeTypePath);
        Directory.CreateDirectory(typeDirectory);
        var target = Path.Combine(typeDirectory, "v1-tag-hash.dll");
        File.WriteAllBytes(target, [1, 2, 3]);

        Assert.False(AtomicFileWrite.PublishBytes(target, [9, 9, 9, 9]));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));
        Assert.Single(Directory.GetFiles(typeDirectory));
    }

    /// <summary>
    /// A completed publication leaves no residue: exactly the DLL and its PDB, nothing else.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PublishingLeavesNoTemporaryFileBehind()
    {
        var store = NewStore();
        await store.Put(NodeTypePath, 1, new byte[1024], new byte[512]).FirstAsync();

        var files = Directory.GetFiles(Path.Combine(_root, NodeTypePath))
            .Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.StartsWith("v1-", f));
        Assert.Contains(files, f => f!.EndsWith(".dll", StringComparison.Ordinal));
        Assert.Contains(files, f => f!.EndsWith(".pdb", StringComparison.Ordinal));
    }
}
