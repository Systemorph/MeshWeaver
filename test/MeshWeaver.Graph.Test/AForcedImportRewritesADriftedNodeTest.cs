using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A FORCED import puts a drifted node back to the repository's copy, even when the
/// repository file never changed</b> (#6639).
///
/// <para><b>Measured on the public instance, 2026-10-31.</b> <c>ReinsuranceDemo/Cedents</c> and
/// <c>ReinsuranceDemo/Claims</c> were imported from the package repository, then overwritten on
/// 2026-08-08 by a runtime writer (the demo installer) with a body the repository never held. The
/// repository files did not change after 2026-08-05, so every routine sync skipped them. A forced
/// GitSync update then reported <c>Imported (171 node(s))</c> and left both rows at their drifted
/// version 9: the per-node incremental skip ("the source token equals the manifest's token") ran
/// even under <see cref="ImportConflictPolicy.Force"/>. The manifest token describes what the
/// import last WROTE, not what the node holds now, so it cannot tell a drifted node from an
/// untouched one. A forced import exists to discard exactly that drift.</para>
///
/// <para><b>Shape.</b> Pass 1 imports a page. A runtime writer (not a person, the shape of the
/// installer) replaces its body. Pass 2 re-imports the SAME source. The routine pass leaves the
/// drift in place (the incremental skip, by design: it is what stops every webhook from
/// re-materialising the partition), which is also the control proving the fixture reached the
/// drifted state. The forced pass must write the node and restore the repository's body.</para>
/// </summary>
public class AForcedImportRewritesADriftedNodeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A service identity, the shape of the installer that wrote the drifted rows.</summary>
    private static readonly AccessContext RuntimeWriter = new()
    {
        ObjectId = "demo-installer",
        Name = "Demo installer",
    };

    private sealed class RepoSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public ImmutableList<MeshNode> Nodes { get; init; } = [];
        public MeshNode? Root { get; init; }

        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
        public IReadOnlyList<StaticContentSync> EnumerateInlineContentSyncs() => [];
    }

    private const string RepoBody = "# Cedents\n\nthe repository's body";
    private const string DriftedBody = "# Cedents\n\nwritten at runtime";

    private static MeshNode Space(string partition) =>
        new(partition) { Name = partition, NodeType = "Space", State = MeshNodeState.Active };

    private static MeshNode Page(string partition) =>
        new("Cedents", partition)
        {
            Name = "Cedents",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = RepoBody },
        };

    /// <summary>
    /// The routine re-import keeps the drift (incremental skip); the forced re-import of the same,
    /// unchanged source writes the node and restores the repository's body.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ADriftedNodeWithAnUnchangedSourceFile_IsRewrittenByAForcedImport()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = "Fd" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{partition}/Cedents";
        RepoSource Source() => new(partition) { Root = Space(partition), Nodes = [Page(partition)] };

        var first = await StaticRepoImporter.ImportSource(Mesh, Source())
            .FirstAsync().Timeout(240.Seconds()).Await(ct);
        first.Outcome.Should().Be("Imported");
        (await BodyContaining(path, RepoBody)).Should().Be(RepoBody);

        // A runtime writer replaces the body. The repository file does not change.
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        IObservable<MeshNode> drift;
        using (access.SwitchAccessContext(RuntimeWriter))
            drift = Mesh.GetWorkspace().GetMeshNodeStream(path).Update(node => node with
            {
                Content = new MarkdownContent { Content = DriftedBody },
            });
        await drift.FirstAsync().Timeout(60.Seconds()).Await(ct);
        (await BodyContaining(path, DriftedBody)).Should().Be(DriftedBody);

        // Control: a routine re-import of the unchanged source skips the node (by design).
        var routine = await StaticRepoImporter.ImportSource(Mesh, Source())
            .FirstAsync().Timeout(240.Seconds()).Await(ct);
        Output.WriteLine($"routine: outcome={routine.Outcome} written=[{string.Join(", ", routine.WrittenPaths)}]");
        routine.WrittenPaths.Should().NotContain(path,
            "the CONTROL: an unchanged source file is skipped by a routine import, so the node is "
            + "still drifted when the forced pass runs");
        (await BodyContaining(path, DriftedBody)).Should().Be(DriftedBody);

        // The forced re-import of the same source must discard the drift.
        var forced = await StaticRepoImporter.ImportSource(
                Mesh, Source(), policy: new ImportConflictPolicy(PreserveServerNewer: false, Since: null, Force: true))
            .FirstAsync().Timeout(240.Seconds()).Await(ct);
        Output.WriteLine($"forced: outcome={forced.Outcome} written=[{string.Join(", ", forced.WrittenPaths)}]");
        forced.WrittenPaths.Should().Contain(path,
            "a forced import overwrites local changes back to the repository copy; the manifest token "
            + "records what the import last wrote, not what the node holds now (#6639)");
        (await BodyContaining(path, RepoBody)).Should().Be(RepoBody);
    }

    private async Task<string> BodyContaining(string path, string expected)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content == expected)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        return node.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content ?? "";
    }
}
