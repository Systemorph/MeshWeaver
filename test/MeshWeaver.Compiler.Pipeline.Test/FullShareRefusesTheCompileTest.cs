using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Persistence;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🔴 <b>The incident, end to end on a real mesh — memex.systemorph.com, 2026-09-08.</b>
///
/// <para>The assembly store of this mesh stands on a volume that keeps only the first 2 KiB of
/// every write and reports nothing — <c>/data</c> at 3 MiB free, as measured. Before this change
/// a compile of a NodeType on such a store SETTLED OK: the Roslyn emit was verified in the local
/// cache, the store "accepted" the copy, the terminal write minted a Release node naming a short
/// file, and the first activation loaded it and hit <c>BadImageFormatException: Bad IL format</c>.
/// Three Releases in two minutes for bytes that were never there.</para>
///
/// <para><b>What must hold now.</b> The compile FAILS — <c>CompilationStatus.Error</c> — with the
/// store's refusal and the disk numbers as the reason; NO Release node is minted; NO version pointer
/// or assembly coordinates are stamped; and the activity says "Assembly NOT PUBLISHED", not "Roslyn
/// failed", because Roslyn did not fail. The unit half (the writer, the store, the reader, the
/// health verdict) is <see cref="ShortWriteIsNotAPublicationTest"/>; this is the pipeline's
/// contract on the record every replica reads.</para>
/// </summary>
public class FullShareRefusesTheCompileTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/FullSharePack";

    /// <summary>The share as measured: a write is accepted, 2 KiB of it is kept, nothing is reported.</summary>
    private const int BytesTheVolumeKeeps = 2048;

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(s =>
        {
            // The base registers a per-class FileSystemAssemblyStore on AssemblyStoreRoot; this
            // stands the SAME store on the lying volume. RemoveAll first: TryAdd would be a no-op.
            s.RemoveAll<IAssemblyStore>();
            return s.AddSingleton<IAssemblyStore>(sp => new FileSystemAssemblyStore(
                AssemblyStoreRoot,
                sp.GetRequiredService<ILogger<FileSystemAssemblyStore>>(),
                FileSystemAssemblyStore.DefaultKeepVersionsPerType,
                path => new TruncatingStream(AtomicFileWrite.OpenTempForWrite(path), BytesTheVolumeKeeps)));
        });

    [Fact(Timeout = 180_000)]
    public async Task ACompileWhoseBytesDoNotLand_FailsLoudly_AndMintsNoRelease()
    {
        await MeshService.CreateNode(MeshNode.FromPath(TypePath) with
            {
                Name = "FullSharePack",
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition
                {
                    Configuration = "config => config.WithContentType<FullSharePackContent>()"
                },
            })
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("api", $"{TypePath}/Source")
            {
                NodeType = "Code",
                Name = "api",
                State = MeshNodeState.Active,
                Content = new CodeConfiguration
                {
                    Language = "csharp",
                    Code = """
                        public record FullSharePackContent { public string Title { get; init; } = ""; }
                        public static class FullSharePackApi { public static int TheAnswer() => 42; }
                        """,
                },
            }))
            .Should().Within(60.Seconds()).Emit();

        var settled = await Mesh.GetMeshNodeStream(TypePath)
            .Should().Within(120.Seconds())
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                is { CompilationStatus: CompilationStatus.Ok or CompilationStatus.Error });
        var def = settled.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;

        // ── THE VERDICT ──────────────────────────────────────────────────────────────────────────
        def.CompilationStatus.Should().Be(CompilationStatus.Error,
            "🚨 a compile whose bytes the store did not keep is a FAILED compile — before this "
            + "change it settled Ok and minted a Release for a file nobody could load. "
            + $"Record: {def.CompilationError}");
        def.CompilationError.Should().NotBeNull()
            .And.Contain("did not keep", "the reason is the store's refusal, not 'Roslyn failed'")
            .And.Contain($"{BytesTheVolumeKeeps:N0}", "with the bytes the volume actually kept")
            .And.Contain("MiB free", "and the volume's capacity, which is what an operator acts on");

        // ── NOTHING WAS PUBLISHED ────────────────────────────────────────────────────────────────
        def.LatestReleasePath.Should().BeNull("no Release node names bytes that are not there");
        def.LastCompiledVersion.Should().BeNull("no version pointer either — a store key with no bytes "
            + "behind it is how activation silently falls back to the default configuration");
        def.LatestAssemblyPath.Should().BeNull();
        def.LatestAssemblyCollection.Should().BeNull();
        def.DispatchedBuildInputs.Should().BeNull("terminal ⇒ no compile in flight (#3390)");

        Directory.Exists(AssemblyStoreRoot).Should().BeTrue();
        Directory.EnumerateFiles(AssemblyStoreRoot, "*.dll", SearchOption.AllDirectories)
            .Should().BeEmpty("no discoverable DLL was left on the store for a refused publication");

        // ── THE ACTIVITY SAYS WHICH FAILURE THIS IS ──────────────────────────────────────────────
        def.LastCompilationActivityPath.Should().NotBeNull();
        var activity = await Mesh.GetMeshNodeStream(def.LastCompilationActivityPath!)
            .Should().Within(60.Seconds())
            .Match(n => n.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions)
                is { Status: ActivityStatus.Failed });
        var log = activity.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions)!;
        log.Messages.Should().Contain(m => m.Message.Contains("Assembly NOT PUBLISHED"),
            "Roslyn produced the assembly; the reader must be sent to the disk, not to the source. "
            + $"Messages: {string.Join(" | ", log.Messages.Select(m => m.Message))}");
        log.Messages.Should().NotContain(m => m.Message.StartsWith("Roslyn failed"));
    }

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
}
