using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #6856, review round 2: an ABORT that reaches the owner before its version check leaves a mark, which
/// is state a message creates — so only a sender who may DELETE the node can create one. Before, any
/// caller could post <see cref="ReleaseDeleteSeal"/> with fresh seal ids and retain one registry entry
/// per id on a live owner. Control: the same abort from a sender holding Delete leaves the mark, and the
/// later check then refuses the seal.
/// </summary>
public class EarlyAbortMarkIsAuthorizedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "SealMarks";
    private const string Path = Partition + "/Doc";
    private const string Deleter = "seal-marks-deleter";
    private const string Intruder = "seal-marks-intruder";

    /// <summary>An Editor: deleting ordinary content demands Update (DeletePermission.RequiredFor), so an
    /// Editor is a deleter the delete admits — and must be able to leave its early-abort mark.</summary>
    private const string EditorDeleter = "seal-marks-editor";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    // ConfigureMeshBase: the default test configuration grants Public Admin, which would let the intruder delete.
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(
            new MeshNode(Partition) { NodeType = "Markdown" },
            AssignmentNodeFactory.UserRole(Deleter, "Admin", Partition),
            AssignmentNodeFactory.UserRole(EditorDeleter, "Editor", Partition),
            new MeshNode("Doc", Partition) { NodeType = "Markdown" });

    private async Task<bool> EarlyAbortFrom(string user, string sealId)
    {
        var answer = await ObserveNodeOperation(
                new ReleaseDeleteSeal(Path, sealId, DeleteSealOutcome.Aborted),
                o => o.WithTarget(new Address(Path)).WithAccessContext(new AccessContext { ObjectId = user, Name = user }))
            .Should().Within(TestTimeouts.Convergence).Emit("the owner answers the release", Ct);
        return answer.Message.Released;
    }

    private static Func<IDisposable, IDisposable> NoLifetime => d => Disposable.Create(() => { });

    [Fact(Timeout = 60_000)]
    public async Task AnEarlyAbortFromASenderWithoutDelete_LeavesNoMark()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();
        (await EarlyAbortFrom(Intruder, "intruder-seal")).Should().BeFalse("there was no seal to release");
        registry.TakeOwnerDeleteSeal("intruder-seal", Path, NoLifetime)
            .Should().BeTrue("a sender who may not delete the node leaves no state on its owner");
        registry.ReleaseOwnerDeleteSeal("intruder-seal", Path, DeleteSealOutcome.Aborted).Should().BeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task AnEarlyAbortFromTheDeleter_LeavesTheMark_AndTheCheckRefusesTheSeal()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();
        (await EarlyAbortFrom(Deleter, "deleter-seal")).Should().BeFalse("there was no seal to release");
        registry.TakeOwnerDeleteSeal("deleter-seal", Path, NoLifetime)
            .Should().BeFalse("control: the authorized abort overtook the check, which now refuses the seal");
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeFalse();
    }

    /// <summary>
    /// The early abort is authorised by the SAME demand as the delete it belongs to. An Editor may delete
    /// ordinary content, so its abort that overtakes the owner's check must leave the mark — otherwise
    /// the delayed check could take a seal after the Editor's only release, and the owner would defer
    /// writes until its activation is torn down.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnEarlyAbortFromAnEditor_LeavesTheMark_BecauseTheEditorMayDeleteContent()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();
        (await EarlyAbortFrom(EditorDeleter, "editor-seal")).Should().BeFalse("there was no seal to release");
        registry.TakeOwnerDeleteSeal("editor-seal", Path, NoLifetime)
            .Should().BeFalse("the Editor's abort overtook the check, which now refuses the seal");
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeFalse();
    }
}
