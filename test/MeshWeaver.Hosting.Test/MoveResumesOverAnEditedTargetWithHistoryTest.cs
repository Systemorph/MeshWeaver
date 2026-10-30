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

    /// <summary>
    /// #7352: a Markdown node — written by the file-system store as <c>.md</c> front matter — is
    /// recognised as itself relocated, so the retry of its half-landed move finishes it. Before
    /// #7352 the front matter carried neither <c>CreatedBy</c> nor <c>CreatedDate</c>, the "same
    /// node" check had nothing to compare, and this retry was refused <c>TargetAlreadyExists</c>
    /// on every attempt.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task ARetry_OfAHalfLandedMarkdownMove_Resumes()
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/note-{suffix}";
        var target = $"{TestPartition}/filed-{suffix}";
        await Seed(source, "Note", MarkdownNodeType);
        var seeded = (await Stored(source))!;
        Output.WriteLine($"stored stamps: type={seeded.NodeType} by={seeded.CreatedBy} created={seeded.CreatedDate:O}");
        seeded.CreatedBy.Should().NotBeNullOrEmpty("precondition: the .md file carries who created the node");
        seeded.CreatedDate.Should().NotBe(default, "precondition: the .md file carries when the node was created");
        await HalfLand(source, target, TestContext.Current.CancellationToken);

        var retry = await Move(source, target);
        Output.WriteLine($"markdown retry: success={retry.Success} reason={retry.RejectionReason} error={retry.Error}");

        retry.Success.Should().BeTrue(retry.Error
            ?? "the target holds this Markdown node relocated, and the source is unchanged — the move resumes");
        (await Stored(target))!.Name.Should().Be("Note", "the resume stands in for the create");
        (await Stored(source)).Should().BeNull("the move completed: the source is gone");
    }

    /// <summary>
    /// Negative control for #7352: carrying authorship in the front matter must not make two
    /// DIFFERENT Markdown nodes look alike. A separately created node at the target has its own
    /// <c>CreatedDate</c>, so the move is still refused and nothing is deleted or overwritten.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task AMarkdownMove_OntoADifferentMarkdownNode_IsStillRefused()
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/note-{suffix}";
        var target = $"{TestPartition}/occupied-{suffix}";
        await Seed(source, "Note", MarkdownNodeType);
        await Seed(target, "Someone else's note", MarkdownNodeType);

        var moved = (await ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken))
            .Message;
        Output.WriteLine($"markdown collision: success={moved.Success} reason={moved.RejectionReason} error={moved.Error}");

        moved.Success.Should().BeFalse("the target holds a different node");
        moved.RejectionReason.Should().Be(NodeMoveRejectionReason.TargetAlreadyExists);
        (await Stored(source))!.Name.Should().Be("Note", "a refused move removes nothing");
        (await Stored(target))!.Name.Should().Be("Someone else's note", "a refused move overwrites nothing");
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

    private const string MarkdownNodeType = "Markdown";

    // A JSON-stored type (`Group`) by default; the Markdown arms pass "Markdown", which the
    // file-system store writes as `.md` front matter (#7352).
    private async Task Seed(string path, string name, string nodeType = "Group") =>
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name,
            NodeType = nodeType,
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
