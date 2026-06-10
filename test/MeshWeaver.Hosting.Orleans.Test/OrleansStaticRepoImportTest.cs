using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// DISTRIBUTED (Orleans) repro for the atioz "/Doc shows no content" bug, on the STANDARD
/// in-memory Orleans fixture (<see cref="TestSiloConfigurator"/> via <see cref="OrleansTestBase{T}"/>)
/// — the same wiring as the distributed portal (<c>ConfigurePortalMesh</c> + <c>AddDocumentation</c>
/// + <c>AddRowLevelSecurity</c>), plus <c>AddSpaceType</c> and the fake static-repo source.
///
/// <para>The monolith <c>StaticRepoImporterTests</c> already cover the importer in-process; this
/// exercises the real cross-hub create/update path on a silo. It asserts the canonical upsert
/// (<c>CreateOrUpdateNodeRequest</c>) the importer uses (a) materializes the Space root + child
/// content on first import and (b) REPAIRS a content-NULL node on re-import (the migration-backfill
/// shadow that left atioz's pages blank).</para>
/// </summary>
public class OrleansStaticRepoImportTest(ITestOutputHelper output)
    : OrleansTestBase<OrleansStaticRepoImportTest.ImportConfigurator>(output)
{
    // Fresh per test-run partition so reruns don't collide on the shared in-memory store.
    internal static readonly string Partition = "TestRepo" + Guid.NewGuid().ToString("N")[..8];
    internal static readonly FakeRepoSource Source = new(Partition);

    // The importer + persistence + IStaticRepoSource (registered via ConfigureMesh) all live on the
    // SILO's mesh hub — that's where prod runs AddStaticRepoSync. The client SP is a separate
    // container, so resolve the source/import/reads from the silo.
    private IServiceProvider SiloServices => ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;
    private IMessageHub Mesh => SiloServices.GetRequiredService<IMessageHub>();

    private CancellationToken Ct => new CancellationTokenSource(55.Seconds()).Token;

    /// <summary>Read a node's authoritative state under System (bypasses RLS) and wait for a
    /// predicate — the canonical single-node read (GetMeshNodeStream), not the lagged query.</summary>
    private async Task<MeshNode?> ReadWhen(string path, Func<MeshNode, bool> predicate, CancellationToken ct)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.ImpersonateAsSystem())
            return await Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Where(n => n is not null && predicate(n))
                .Take(1)
                .Timeout(30.Seconds())
                .Catch((Exception _) => Observable.Return<MeshNode?>(null))
                .FirstAsync()
                .ToTask(ct);
    }

    private static string? MarkdownOf(MeshNode? n) => (n?.Content as MarkdownContent)?.Content;

    [Fact(Timeout = 90000)]
    public async Task Import_CreatesSpaceRoot_AndChildContent()
    {
        var ct = Ct;
        Source.Body = "ORIGINAL body";

        var results = await StaticRepoImporter.ImportAll(Mesh).ToList().FirstAsync().ToTask(ct);
        foreach (var r in results)
            Output.WriteLine($"import: partition={r.Partition} outcome={r.Outcome} count={r.Count}");

        // The child page must be persisted WITH content (the core of the /Doc-empty failure).
        var page = await ReadWhen($"{Partition}/Page1",
            n => MarkdownOf(n)?.Contains("ORIGINAL") == true, ct);
        page.Should().NotBeNull("import must create Page1 with content via the canonical upsert");
        MarkdownOf(page).Should().Contain("ORIGINAL");

        // The Space partition root (namespace="", id=Partition) is a STANDARD import step.
        var root = await ReadWhen(Partition, n => n.NodeType == "Space", ct);
        root.Should().NotBeNull("import must create the partition Space root");
        root!.NodeType.Should().Be("Space");
    }

    [Fact(Timeout = 90000)]
    public async Task Reimport_ChangedContent_RepairsContentNullPage()
    {
        var ct = Ct;
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // v1: import a page WITH content.
        Source.Body = "ORIGINAL body";
        await StaticRepoImporter.ImportAll(Mesh).ToList().FirstAsync().ToTask(ct);
        (await ReadWhen($"{Partition}/Page1", n => MarkdownOf(n)?.Contains("ORIGINAL") == true, ct))
            .Should().NotBeNull();

        // Simulate the migration-backfill shadow: blank the content (content-NULL row).
        using (access.ImpersonateAsSystem())
            await Mesh.GetWorkspace().GetMeshNodeStream($"{Partition}/Page1")
                .Update(n => n with { Content = null })
                .Take(1).Timeout(30.Seconds()).ToTask(ct);
        (await ReadWhen($"{Partition}/Page1", n => n.Content is null, ct))
            .Should().NotBeNull("blanking must leave a content-NULL Page1 (the backfill shadow)");

        // v2: changed body → new fingerprint → re-import → upsert must REPAIR the NULL content.
        Source.Body = "REPAIRED body";
        await StaticRepoImporter.ImportAll(Mesh).ToList().FirstAsync().ToTask(ct);

        var repaired = await ReadWhen($"{Partition}/Page1",
            n => MarkdownOf(n)?.Contains("REPAIRED") == true, ct);
        repaired.Should().NotBeNull("re-import must refill content over the content-NULL shadow row");
        MarkdownOf(repaired).Should().Contain("REPAIRED");
    }

    /// <summary>Fake static-repo source: one Markdown page + a Space partition root.</summary>
    public sealed class FakeRepoSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public string Body { get; set; } = "body";

        public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
        [
            new MeshNode("Page1", partition)
            {
                NodeType = "Markdown", Name = "Page 1", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = $"# Page 1\n\n{Body}" }
            }
        ];

        public MeshNode? PartitionRoot => new(partition)
        {
            Name = partition, NodeType = "Space", State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = $"# {partition}\n\nwelcome" }
        };
    }

    /// <summary>Standard in-memory silo fixture + Space type + the fake source.</summary>
    public class ImportConfigurator : TestSiloConfigurator
    {
        protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
            builder
                .AddSpaceType()
                .ConfigureServices(s => s.AddSingleton<IStaticRepoSource>(Source));
    }
}
