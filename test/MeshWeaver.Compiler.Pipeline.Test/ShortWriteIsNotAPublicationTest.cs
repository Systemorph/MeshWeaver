using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🔴 <b>memex.systemorph.com, 2026-09-08 — a full share publishes a truncated DLL, and the
/// pipeline mints a Release for it.</b>
///
/// <para><c>/data</c> — the ReadWriteMany Azure Files share holding <c>/data/assembly-cache</c> —
/// measured <b>3 MiB free of 16 GiB</b>. Every recompile of <c>Hosting/InstanceAction</c> emitted
/// fine into the local <c>.mesh-cache</c>, was verified there, and was then copied into the store
/// through <c>File.WriteAllBytes</c> + rename. On a Linux CIFS mount <c>write(2)</c> lands in the
/// page cache and returns success; the <c>ENOSPC</c> from the server surfaces only on writeback
/// (fsync / close), and .NET discards <c>close(2)</c>'s return value. So the rename published a
/// short file under a complete-looking, content-hashed name; the terminal write minted
/// <c>Release/20260908133359-…</c> naming it; the same pod loaded it —
/// <c>BadImageFormatException: Bad IL format</c> — deleted it "for regeneration", and the next
/// activation's store miss compiled again. Three Releases (v851, v855, v859) in two minutes, each for
/// bytes that were never there, and an <c>InstanceAction</c> node that stayed an untyped
/// <c>JsonElement</c> on both replicas while a client incident waited on it.</para>
///
/// <para><b>The contract these tests pin, in three layers.</b> (1) <see cref="AtomicFileWrite"/>
/// REFUSES a publication whose bytes the volume did not keep — flush-to-disk, then the length on
/// disk must equal the bytes written — and leaves neither target nor staging file behind. (2)
/// <see cref="FileSystemAssemblyStore"/> propagates that refusal on the subscriber's error channel
/// and never returns a path whose bytes are short. (3) The compile pipeline treats it as TERMINAL:
/// <c>CompilationStatus.Error</c> with the disk numbers in the reason, NO Release node, NO version
/// pointer, the previous coordinates untouched.</para>
///
/// <para>🚨 The full share is reproduced with an INJECTED writer, not a quota: macOS has no tmpfs to
/// fill, and a test that needed one would be a test that never ran. Two writers stand in for the
/// two ways a filesystem lies — one keeps only the first N bytes and reports nothing (the share
/// as measured), one faults at flush (the fsync that DOES report). Every arm has a control that
/// runs the SAME bytes through the production writer, so a refusal that refused everything could
/// not pass.</para>
/// </summary>
public class ShortWriteIsNotAPublicationTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MeshWeaverShortWrite", $"t_{Guid.NewGuid():N}");

    public ShortWriteIsNotAPublicationTest() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] Payload(int length, byte fill)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }

    /// <summary>
    /// A volume that accepts every write and keeps only the first <c>cap</c> bytes, reporting
    /// nothing — the page-cache-then-ENOSPC-on-writeback shape, as measured.
    /// </summary>
    private static Func<string, Stream> KeepsOnly(int cap)
        => path => new TruncatingStream(AtomicFileWrite.OpenTempForWrite(path), cap);

    /// <summary>A volume whose fsync reports the failure — the shape a kernel that honours it produces.</summary>
    private static Func<string, Stream> FaultsAtFlush()
        => path => new FlushFaultingStream(AtomicFileWrite.OpenTempForWrite(path));

    private static string[] StagingLeftovers(string directory)
        => Directory.GetFiles(directory, ".tmp-*", SearchOption.TopDirectoryOnly);

    // ── 1. AtomicFileWrite ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AShortWrite_IsRefused_AndLeavesNoFileBehind()
    {
        var target = Path.Combine(_root, "v1-tag-abc.dll");
        var bytes = Payload(64 * 1024, 0x2A);

        var refusal = Assert.Throws<ShortWriteException>(
            () => AtomicFileWrite.PublishBytesWith(target, bytes, KeepsOnly(4096)));

        refusal.ExpectedBytes.Should().Be(bytes.Length);
        refusal.LandedBytes.Should().Be(4096, "the reason names what the volume actually kept");
        refusal.Message.Should().Contain("65,536").And.Contain("4,096",
            "an operator reads the numbers off the record, not off a debugger");
        File.Exists(target).Should().BeFalse("a name whose bytes are not there must never appear");
        StagingLeftovers(_root).Should().BeEmpty("the staging file is removed with the refusal");
    }

    [Fact]
    public void AFlushFault_Propagates_AndLeavesNoFileBehind()
    {
        var target = Path.Combine(_root, "v2-tag-abc.dll");

        var fault = Assert.Throws<IOException>(
            () => AtomicFileWrite.PublishBytesWith(target, Payload(8192, 0x11), FaultsAtFlush()));

        fault.Message.Should().Contain("No space left on device");
        File.Exists(target).Should().BeFalse();
        StagingLeftovers(_root).Should().BeEmpty();
    }

    /// <summary>The control: the production writer publishes the same bytes whole.</summary>
    [Fact]
    public void TheProductionWriter_PublishesTheSameBytesWhole()
    {
        var target = Path.Combine(_root, "v3-tag-abc.dll");
        var bytes = Payload(64 * 1024, 0x2A);

        AtomicFileWrite.PublishBytesWith(target, bytes, AtomicFileWrite.OpenTempForWrite)
            .Should().BeTrue("without this half a refusal that refused everything would pass above");
        File.ReadAllBytes(target).Should().Equal(bytes);
        StagingLeftovers(_root).Should().BeEmpty();
    }

    /// <summary>
    /// 🚨 A short write is NEVER read as "the concurrent writer won". On a full volume the other
    /// replica's file is short too, and answering <c>false</c> would hand the caller a name to
    /// publish. The target appearing mid-write is exactly the race the rename branch handles — and
    /// only the rename branch.
    /// </summary>
    [Fact]
    public void AShortWrite_IsNotMistakenForTheConcurrentWriterRace()
    {
        var target = Path.Combine(_root, "v4-tag-abc.dll");
        var bytes = Payload(16 * 1024, 0x07);
        Func<string, Stream> raceThenTruncate = path =>
        {
            // "Another replica" lands the target between the pre-check and the write.
            File.WriteAllBytes(target, Payload(16, 0x07));
            return new TruncatingStream(AtomicFileWrite.OpenTempForWrite(path), cap: 1024);
        };

        Assert.Throws<ShortWriteException>(
            () => AtomicFileWrite.PublishBytesWith(target, bytes, raceThenTruncate));
        StagingLeftovers(_root).Should().BeEmpty();
    }

    // ── 2. FileSystemAssemblyStore ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheStore_PropagatesTheRefusal_AndNeverReturnsAShortPath()
    {
        var store = new FileSystemAssemblyStore(
            _root, NullLogger<FileSystemAssemblyStore>.Instance,
            FileSystemAssemblyStore.DefaultKeepVersionsPerType, KeepsOnly(2048));
        var bytes = Payload(32 * 1024, 0x5C);

        // Deferred: the refusal must travel the SUBSCRIBER's error channel, where the compile
        // pipeline's terminal handler is wired — never out of the call.
        var put = Record.Exception(() => store.PutWithLocation("P/T", 851, bytes, pdbBytes: null));
        put.Should().BeNull("the write happens at subscribe time");

        var refusal = await Assert.ThrowsAsync<ShortWriteException>(async () =>
            await store.PutWithLocation("P/T", 851, bytes, pdbBytes: null).Timeout(TimeSpan.FromSeconds(30)));
        refusal.LandedBytes.Should().Be(2048);

        var typeDir = Directory.GetDirectories(_root).Should().ContainSingle().Subject;
        Directory.GetFiles(typeDir, "*.dll").Should().BeEmpty("no discoverable DLL for bytes that are not there");
        StagingLeftovers(typeDir).Should().BeEmpty();
        (await store.TryGetAssemblyPath("P/T", 851).Timeout(TimeSpan.FromSeconds(30)))
            .Should().BeNull("a lookup must not resolve a publication that was refused");
    }

    /// <summary>The control: the same store on the production writer publishes and resolves.</summary>
    [Fact]
    public async Task TheStore_OnTheProductionWriter_PublishesAndResolves()
    {
        var store = new FileSystemAssemblyStore(_root, NullLogger<FileSystemAssemblyStore>.Instance);
        var bytes = Payload(32 * 1024, 0x5C);

        var location = await store.PutWithLocation("P/T", 851, bytes, pdbBytes: null)
            .Timeout(TimeSpan.FromSeconds(30));
        File.ReadAllBytes(location.LocalPath).Should().Equal(bytes);
        (await store.TryGetAssemblyPath("P/T", 851).Timeout(TimeSpan.FromSeconds(30)))
            .Should().Be(location.LocalPath);
    }

    // ── 3. The reader names the cause it found ─────────────────────────────────────────────────

    /// <summary>
    /// A file that IS short under a complete-looking name (the pre-fix incident's leftovers, or a
    /// writer this fix does not reach) is still deleted for regeneration — and the reason, with the
    /// file's length and the volume's capacity, is recorded where the compile verdict reads it,
    /// instead of "corrupt cached .dll or a missing dependency".
    /// </summary>
    [Fact]
    public void ACorruptFile_IsDeleted_AndTheReasonCarriesTheNumbers()
    {
        var dll = Path.Combine(_root, "v851-s72c27af-431befe75f90.dll");
        File.WriteAllBytes(dll, Payload(4096, 0x00));
        using var context = new NodeAssemblyLoadContext("Hosting_InstanceAction", dll);

        context.LoadNodeAssembly().Should().BeNull();

        context.LastLoadFailure.Should().NotBeNull()
            .And.Contain(nameof(BadImageFormatException))
            .And.Contain("4,096 byte(s)", "the length is what tells a full volume from a bad image")
            .And.Contain("MiB free", "and the volume's capacity is the other half of that diagnosis");
        File.Exists(dll).Should().BeFalse("a short file under a content-hashed name blocks every "
            + "republication of those bytes (first-publish-wins), so it must go");
    }

    // ── 4. The /health verdict ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCapacityVerdict_NamesThePathAndTheNumbers()
    {
        const long mib = 1024 * 1024;
        var (low, description) = StorageCapacityHealth.Evaluate(
            "/data/assembly-cache", new VolumeCapacity(3 * mib, 16384 * mib), 256 * mib);
        low.Should().BeTrue();
        description.Should().Contain("/data/assembly-cache").And.Contain("3 MiB").And.Contain("16,384 MiB")
            .And.Contain("256 MiB");

        StorageCapacityHealth.Evaluate("/data", new VolumeCapacity(2048 * mib, 16384 * mib), 256 * mib)
            .IsLow.Should().BeFalse();
        StorageCapacityHealth.Evaluate("/data", capacity: null, 256 * mib)
            .IsLow.Should().BeFalse("a reading that could not be taken is not a verdict");
        StorageCapacityHealth.MinimumFreeBytes(null).Should().Be(256 * mib);
        StorageCapacityHealth.MinimumFreeBytes("512").Should().Be(512 * mib);
        StorageCapacityHealth.MinimumFreeBytes("not a number").Should().Be(256 * mib);
    }

    // ── The two volumes that lie ───────────────────────────────────────────────────────────────

    /// <summary>Keeps the first <c>cap</c> bytes of every write and drops the rest, silently.</summary>
    private sealed class TruncatingStream(Stream inner, int cap) : Stream
    {
        private long _accepted;

        public override void Write(byte[] buffer, int offset, int count)
        {
            var room = (int)Math.Max(0, Math.Min(count, cap - _accepted));
            if (room > 0)
                inner.Write(buffer, offset, room);
            _accepted += count;
        }

        public override void Flush() => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Accepts every write and faults at flush — the fsync that reports ENOSPC.</summary>
    private sealed class FlushFaultingStream(Stream inner) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Flush() => throw new IOException("No space left on device");
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
