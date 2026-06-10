using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.ContentCollections;
using MeshWeaver.Graph;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// DISTRIBUTED (Orleans) gate for the static-repo content sync (#11) — the exact path that deadlocked
/// in prod when the copy was an async operation on a hub path. The importer runs on the silo's mesh
/// hub and, after the node upsert, posts the canonical <see cref="ImportContentRequest"/> to the
/// OWNING node's GRAIN; the handler copies the source folder into the node's per-node <c>content</c>
/// collection, sealed in the file-system <c>IIoPool</c>. The test asserts the import (a) completes
/// within the timeout — a grain-reentrancy/pool deadlock trips the <c>ct</c> — and (b) lands the
/// binary asset byte-for-byte where the per-node collection serves it.
///
/// <para>Companion to the monolith <c>ContentImportSyncTest</c>; this exercises the cross-grain
/// request/response + per-node-grain handler that the monolith can't.</para>
/// </summary>
public class OrleansContentImportSyncTest(ITestOutputHelper output)
    : OrleansTestBase<OrleansContentImportSyncTest.ContentImportConfigurator>(output)
{
    internal static readonly string Partition = "TestContent" + Guid.NewGuid().ToString("N")[..8];
    internal static readonly FakeContentSource Source = new(Partition);

    // FileSystem content roots (content collections are independent of the in-memory node store).
    internal static readonly string Root =
        Path.Combine(Path.GetTempPath(), "OrleansContentImportSyncTest", Guid.NewGuid().ToString("N"));
    internal static string SourceDir => Path.Combine(Root, "source");
    internal static string ContentRoot => Path.Combine(Root, "content");

    // Bytes that are NOT valid UTF-8 — proves the copy is stream-to-stream, not text-mangled.
    private static readonly byte[] BinaryAsset =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0x42, 0x7E];

    private IMessageHub Mesh =>
        ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>();

    private CancellationToken Ct => new CancellationTokenSource(75.Seconds()).Token;

    [Fact(Timeout = 120000)]
    public async Task Import_SyncsContentFile_IntoNodeContentCollection_OnGrain()
    {
        Directory.CreateDirectory(SourceDir);
        await File.WriteAllBytesAsync(Path.Combine(SourceDir, "logo.png"), BinaryAsset, Ct);

        var results = await StaticRepoImporter.ImportAll(Mesh).ToList().FirstAsync().ToTask(Ct);
        var mine = results.FirstOrDefault(r => r.Partition == Partition);
        Output.WriteLine($"import: partition={mine?.Partition} outcome={mine?.Outcome} count={mine?.Count}");
        mine.Should().NotBeNull("the content source partition must be imported");
        mine!.Outcome.Should().Be("Imported");

        // The asset must land where the per-node "content" collection serves it, byte-for-byte —
        // and the import must have returned (no grain/pool deadlock) for us to get here.
        var landed = Directory.GetFiles(ContentRoot, "logo.png", SearchOption.AllDirectories);
        landed.Should().HaveCount(1, "the import copies the asset into the node's content directory");
        File.ReadAllBytes(landed[0]).SequenceEqual(BinaryAsset)
            .Should().BeTrue("binary content is copied stream-to-stream across the grain, not corrupted");
    }

    /// <summary>Source: one Markdown page + a Space root + a content import of the source folder.</summary>
    public sealed class FakeContentSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;

        public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
        [
            new MeshNode("Page1", partition)
            {
                NodeType = "Markdown", Name = "Page 1", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "# Page 1\n\n@@content/logo.png" }
            }
        ];

        public MeshNode? PartitionRoot => new(partition)
        {
            Name = partition, NodeType = "Space", State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = $"# {partition}\n\nwelcome" }
        };

        public IReadOnlyList<StaticContentImport> EnumerateContentImports() =>
        [
            new StaticContentImport(
                NodePath: $"{partition}/Page1",
                SourceCollection: "TestSource",
                SourcePath: "",
                TargetCollection: "content",
                TargetPath: "")
        ];
    }

    /// <summary>Standard silo + Space type + the source + per-node content collections.</summary>
    public class ContentImportConfigurator : TestSiloConfigurator
    {
        protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
            builder
                .AddSpaceType()
                .ConfigureServices(s => s.AddSingleton<IStaticRepoSource>(Source))
                // Give every node hub the source + a writable per-node "content" collection,
                // mirroring the portal's ConfigureDefaultNodeHub content wiring.
                .ConfigureDefaultNodeHub(config => config
                    .AddContentCollections()
                    .AddFileSystemContentCollection("TestSource", _ => SourceDir)
                    .AddContentCollection(_ => new ContentCollectionConfig
                    {
                        Name = "content",
                        SourceType = "FileSystem",
                        BasePath = Path.Combine(ContentRoot, config.Address.ToString()),
                        IsEditable = true,
                        ExposeInChildren = true
                    }));
    }
}
