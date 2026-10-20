using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #6310 on a store that KEEPS version history (the file-system store's
/// <see cref="FileSystemVersionStore"/>). A half-landed move whose relocated node was edited at the
/// target before the retry is still resumable here: the source is provably unchanged since the copy
/// because the target once carried the source's exact <c>LastModified</c> — in a recorded version.
/// The retry finishes the move, the target keeps its NEWER state (the resume never overwrites it),
/// and only the stale source is removed.
///
/// <para>The negative control is the mirror edit: the SOURCE written after the copy. Its new stamp
/// is in no version of the target, so the retry is refused and both sides stay as they are — the
/// source's edit is never destroyed by the delete leg.</para>
/// </summary>
public class MoveResumesOverAnEditedTargetWithHistoryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mw-resume-{Guid.NewGuid():N}");

    /// <inheritdoc />
    protected override IMeshBootstrap Bootstrap => new FileSystemBootstrap(root);

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    [Fact(Timeout = 90_000)]
    public async Task ARetry_OverATargetEditedAfterTheCopy_ResumesAndKeepsTheTargetsNewerState()
    {
        Access.SetCircuitContext(TestUsers.Admin);
        Mesh.ServiceProvider.GetRequiredService<IVersionQuery>().RetainsHistory.Should().BeTrue(
            "precondition: this arm is the store that can prove the source unchanged");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/submission-{suffix}";
        var target = $"{TestPartition}/inbox-{suffix}";
        await Seed(source, "Submission");
        await HalfLand(source, target, TestContext.Current.CancellationToken);

        await Edit(target, "Submission, triaged");

        var retry = await Move(source, target);
        Output.WriteLine($"retry over an edited target: success={retry.Success} reason={retry.RejectionReason} error={retry.Error}");

        retry.Success.Should().BeTrue(retry.Error
            ?? "the target carried the source's stamp in a recorded version — the source is unchanged, so the move resumes");
        (await Stored(target))!.Name.Should().Be("Submission, triaged",
            "the resume stands in for the create: the target's newer edit is kept, never overwritten");
        (await Stored(source)).Should().BeNull("the move completed: the stale source is gone");
    }

    [Fact(Timeout = 90_000)]
    public async Task ARetry_AfterTheSourceWasEdited_IsRefused_EvenWithHistory()
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/submission-{suffix}";
        var target = $"{TestPartition}/inbox-{suffix}";
        await Seed(source, "Submission");
        await HalfLand(source, target, TestContext.Current.CancellationToken);

        await Edit(source, "Submission, edited after the copy");

        var retry = await Move(source, target);
        Output.WriteLine($"retry after a source edit: success={retry.Success} reason={retry.RejectionReason} error={retry.Error}");

        retry.Success.Should().BeFalse("no version of the target carries the source's newer stamp");
        retry.RejectionReason.Should().Be(NodeMoveRejectionReason.TargetAlreadyExists);
        retry.Error.Should().Contain("relocated by an earlier attempt",
            "refused by the source-unchanged check, after the target was recognised as this node relocated");
        (await Stored(source))!.Name.Should().Be("Submission, edited after the copy", "a refused retry deletes nothing at the source");
        (await Stored(target))!.Name.Should().Be("Submission", "and leaves the target as the copy left it");
    }

    /// <summary>The copy leg a move issues, and no delete leg — the half-landing of #6107.</summary>
    private async Task HalfLand(string source, string target, System.Threading.CancellationToken ct)
    {
        var copied = await ObserveNodeOperation(new CopyNodeRequest(source, target) { PreserveAuthorship = true })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        copied.Message.Success.Should().BeTrue(copied.Message.Error ?? "precondition: the copy leg lands");
    }

    private async Task Edit(string path, string name) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(path).Update(node => node with { Name = name })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private async Task<MoveNodeResponse> Move(string source, string target) =>
        (await ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken))
        .Message;

    private async Task<MeshNode?> Stored(string path) =>
        await Storage.Read(path, Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    // A JSON-stored type, deliberately not Markdown: the file-system store writes a Markdown node as
    // `.md` front matter, which keeps neither CreatedBy nor CreatedDate and reads LastModified from
    // the file's timestamp, so the identity a resume needs is not there to compare and such a retry
    // is refused (fail-closed). Measured while writing this test: `by= created=0001-01-01…`.
    private async Task Seed(string path, string name) =>
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name,
            NodeType = "Group",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    /// <summary>The monolith mesh over a file-system store rooted at a per-test directory.</summary>
    private sealed class FileSystemBootstrap(string baseDirectory) : IMeshBootstrap
    {
        /// <inheritdoc />
        public string Name => "monolith-filesystem";

        /// <inheritdoc />
        public MeshBuilder Bootstrap(MeshBuilder builder)
        {
            Directory.CreateDirectory(baseDirectory);
            return builder.UseMonolithMesh().AddFileSystemPersistence(baseDirectory);
        }
    }
}
