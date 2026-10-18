using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Mesh.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// #6940 — memex-cloud, 2026-10-18 05:25–06:42Z: the shared <c>/data</c> volume was out of space,
/// and every NodeType publication failed with the runtime's bare
/// <c>IOException: No space left on device : '/data/assembly-cache/…/.tmp-….pdb.tmp'</c> — a staging
/// path that reads like a defect in the assembly store. The refusal must NAME the cause: the volume
/// is full, with its capacity, and nothing was published.
///
/// <para>The full volume is an INJECTED writer that throws exactly what the runtime throws on
/// <c>ENOSPC</c> — an <see cref="IOException"/> whose <see cref="Exception.HResult"/> is the raw errno
/// (the Win32 code on Windows). The negative control throws the same message with another errno and
/// must NOT be renamed, so a classifier that matched every IO fault could not pass.</para>
/// </summary>
public class VolumeFullIsANamedRefusalTest : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "MeshWeaverVolumeFull", $"t_{Guid.NewGuid():N}");

    public VolumeFullIsANamedRefusalTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }

    private static int OutOfSpaceCode => OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28;

    /// <summary>Opens nothing: the volume refuses the staging file the way the runtime reports it.</summary>
    private static Func<string, Stream> Refuses(int hresult)
        => path => throw new IOException($"No space left on device : '{path}'", hresult);

    [Fact]
    public void An_out_of_space_refusal_is_named_and_leaves_nothing_behind()
    {
        var target = Path.Combine(root, "v1-tag-abc.dll");

        var refusal = Assert.Throws<VolumeFullException>(
            () => AtomicFileWrite.PublishBytesWith(target, new byte[4096], Refuses(OutOfSpaceCode)));

        refusal.Message.Should().Contain("OUT OF SPACE").And.Contain("Nothing was published");
        refusal.AttemptedBytes.Should().Be(4096);
        refusal.Path.Should().Be(target, "the refusal names the file, not the staging name");
        refusal.InnerException.Should().BeOfType<IOException>("the runtime's own fault is kept");
        File.Exists(target).Should().BeFalse();
        Directory.GetFiles(root, ".tmp-*").Should().BeEmpty();
    }

    /// <summary>The negative control: an IO fault that is not out-of-space keeps its own type.</summary>
    [Fact]
    public void Another_io_fault_is_not_renamed()
    {
        const int permissionDenied = 13;
        var fault = Assert.Throws<IOException>(
            () => AtomicFileWrite.PublishBytesWith(
                Path.Combine(root, "v2-tag-abc.dll"), new byte[16], Refuses(permissionDenied)));
        fault.Should().NotBeOfType<VolumeFullException>();
    }

    [Fact]
    public async Task The_assembly_store_refuses_the_publication_with_the_named_cause()
    {
        var store = new FileSystemAssemblyStore(
            root, NullLogger<FileSystemAssemblyStore>.Instance,
            FileSystemAssemblyStore.DefaultKeepVersionsPerType, Refuses(OutOfSpaceCode));

        // The compile pipeline wraps this in AssemblyPublicationException, whose message is what the
        // NodeType record and the incident line carry — so the name has to survive the store.
        await Assert.ThrowsAsync<VolumeFullException>(async () =>
            await store.PutWithLocation("Store/CouponClaim", 73, new byte[2048], new byte[512])
                .Timeout(TestTimeouts.Convergence).Await());
        Directory.GetFiles(Path.Combine(root, "Store_CouponClaim")).Should().BeEmpty();
    }
}
