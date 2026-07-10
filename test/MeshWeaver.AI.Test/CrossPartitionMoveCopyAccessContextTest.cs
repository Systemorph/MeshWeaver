using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Regression guard for the production failure (memex.systemorph.com): a cross-partition
/// <c>move</c>/<c>copy</c> of a node SUBTREE fails with
/// <c>"AccessContext must never be null for an application post … message=CreateNodeRequest"</c>.
///
/// <para><b>Root cause under test.</b> <see cref="MeshOperations.Copy"/> /
/// <see cref="MeshOperations.Move"/> recreate the subtree at the target by posting one
/// <c>CreateNodeRequest</c> per descendant. The FIRST create (the subtree root) is posted while the
/// caller's identity is still live on the request-scoped AsyncLocal, so it carries the caller's
/// <see cref="AccessContext"/> and succeeds. The RECURSIVE child creates are subscribed on a
/// workspace-emission / Rx scheduler thread where the AsyncLocal is wiped — so they post with a NULL
/// AccessContext, the PostPipeline fails closed, and the target ends up with only the root node while
/// the source stays intact.</para>
///
/// <para><b>Why cross-partition.</b> A same-partition move/copy can route the child creates through
/// the local data source without a fresh cross-hub post; the routed cross-partition
/// <c>CreateNodeRequest</c> is what surfaces the lost identity — exactly the production shape
/// (<c>PartnerRe/AIConsulting -&gt; ClientPartnerRe/AIConsulting</c>).</para>
///
/// <para><b>Why we do NOT lean on DevLogin's persistent context.</b> <see cref="TestUsers.DevLogin"/>
/// stamps the Admin identity via <see cref="AccessService.SetCircuitContext"/>, which also writes the
/// process-wide <c>persistentCircuitContext</c> fallback field — that field survives scheduler hops
/// and would MASK the very identity-loss this test must reproduce (see
/// <c>CircuitContextIsolationTest</c>). We therefore clear it and set only the request-scoped
/// AsyncLocal (<see cref="AccessService.SetContext"/>), which is precisely the state a live hub
/// handler has after restoring <c>delivery.AccessContext</c> — and precisely the state that a
/// production Blazor circuit's AsyncLocal is in (no persistent fallback there either).</para>
/// </summary>
public class CrossPartitionMoveCopyAccessContextTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Each [Fact] mints unique partition names, so the shared mesh is safe and cheaper.
    protected override bool ShareMeshAcrossTests => true;

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

    private MeshOperations Ops => new(Mesh);
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Runs <paramref name="op"/> under the FAITHFUL production identity shape: request-scoped
    /// AsyncLocal only (like a live handler after restoring <c>delivery.AccessContext</c>), with the
    /// masking persistent-circuit fallback cleared so a lost-on-hop identity truly surfaces as null.
    /// Restores DevLogin (persistent Admin) afterward so the verification READS authorise.
    /// </summary>
    private async Task<string> RunAsUser(AccessContext user, IObservable<string> op)
    {
        Access.ClearPersistentCircuitContext();
        try
        {
            using (Access.SwitchAccessContext(user))
                return await op.FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);
        }
        finally
        {
            // Reads below (ReadNode / WaitUntil) are access-gated — re-establish the Admin
            // circuit identity the fixture logged in with, since we cleared the persistent
            // fallback above to faithfully reproduce the lost-on-hop write condition.
            TestUsers.DevLogin(Mesh);
        }
    }

    /// <summary>Creates a Space partition root (the sanctioned top-level container; its creator is
    /// granted Admin on it, so nested child writes authorise).</summary>
    private Task<MeshNode> CreateSpace(string id) =>
        MeshService.CreateNode(new MeshNode(id) { Name = id, NodeType = "Space", State = MeshNodeState.Active })
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask(Ct);

    private Task<MeshNode> CreateChild(string path) =>
        MeshService.CreateNode(MeshNode.FromPath(path) with
        {
            Name = path.Split('/')[^1],
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = $"# {path}" },
            State = MeshNodeState.Active
        }).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask(Ct);

    /// <summary>Reads a node authoritatively; null when absent/timeout.</summary>
    private Task<MeshNode?> Read(string path) =>
        ReadNode(path).FirstAsync().Timeout(TimeSpan.FromSeconds(15)).ToTask(Ct);

    /// <summary>Waits until the node at <paramref name="path"/> either exists or is gone.</summary>
    private Task<MeshNode?> WaitUntil(string path, bool shouldExist) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => ReadNode(path))
            .Where(n => shouldExist ? n is not null : n is null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(20))
            .ToTask(Ct);

    // ────────────────────────────────────────────────────────────────────────────────────────

    [Fact(Timeout = 120000)]
    public async Task Copy_SubtreeCrossPartition_AllDescendantsLandAtTarget()
    {
        var srcSpace = Unique("SrcCopy");
        var dstSpace = Unique("DstCopy");
        await CreateSpace(srcSpace);
        await CreateSpace(dstSpace);

        // Subtree: parent + two children (one nested). The recursive child creates are the ones
        // that lose AccessContext across the scheduler hop in the buggy path.
        var root = $"{srcSpace}/Consulting";
        await CreateChild(root);
        await CreateChild($"{root}/Alice");
        await CreateChild($"{root}/Team/Bob");

        // Copy the subtree cross-partition (srcSpace -> dstSpace). NodeCopyHelper remaps
        // srcSpace/Consulting -> dstSpace/Consulting, etc.
        var result = await RunAsUser(TestUsers.Admin, Ops.Copy(root, dstSpace, force: false));

        result.Should().NotContain("AccessContext",
            "the recursive per-descendant CreateNodeRequest must carry the caller's identity — a null " +
            "AccessContext DeliveryFailure is the production bug being guarded against");
        result.Should().StartWith("Copied", "copy of a subtree the Admin owns must succeed");
        // "Copied N node(s): …" — N must be 3 (root + 2 descendants). A recursive create that
        // silently lost its AccessContext would drop children and report fewer than 3.
        result.Should().Contain("Copied 3 node(s)",
            "all 3 subtree nodes (root + Alice + Team/Bob) must be created at the target");

        // EVERY node (root + both descendants) must exist at the TARGET.
        (await WaitUntil($"{dstSpace}/Consulting", shouldExist: true)).Should().NotBeNull("root must be copied");
        (await WaitUntil($"{dstSpace}/Consulting/Alice", shouldExist: true)).Should().NotBeNull("child Alice must be copied");
        (await WaitUntil($"{dstSpace}/Consulting/Team/Bob", shouldExist: true)).Should().NotBeNull("nested child Bob must be copied");

        // Copy leaves the SOURCE intact.
        (await Read(root)).Should().NotBeNull("source root must remain after copy");
        (await Read($"{root}/Alice")).Should().NotBeNull("source child must remain after copy");
        (await Read($"{root}/Team/Bob")).Should().NotBeNull("source nested child must remain after copy");
    }

    [Fact(Timeout = 120000)]
    public async Task Move_SubtreeCrossPartition_AllDescendantsMoveAndSourceIsGone()
    {
        var srcSpace = Unique("SrcMove");
        var dstSpace = Unique("DstMove");
        await CreateSpace(srcSpace);
        await CreateSpace(dstSpace);

        // Move remaps sourcePath -> targetPath verbatim, so target the full destination path.
        var source = $"{srcSpace}/Consulting";
        var target = $"{dstSpace}/Consulting";
        await CreateChild(source);
        await CreateChild($"{source}/Alice");
        await CreateChild($"{source}/Team/Bob");

        var result = await RunAsUser(TestUsers.Admin, Ops.Move(source, target));

        result.Should().NotContain("AccessContext",
            "the recursive per-descendant CreateNodeRequest inside the copy-then-delete move must carry " +
            "the caller's identity — a null AccessContext DeliveryFailure is the production bug");
        result.Should().StartWith("Moved", "move of a subtree the Admin owns must succeed");

        // EVERY node lands at the TARGET.
        (await WaitUntil(target, shouldExist: true)).Should().NotBeNull("root must be moved to target");
        (await WaitUntil($"{target}/Alice", shouldExist: true)).Should().NotBeNull("child Alice must be moved to target");
        (await WaitUntil($"{target}/Team/Bob", shouldExist: true)).Should().NotBeNull("nested child Bob must be moved to target");

        // NONE remain at the SOURCE.
        (await WaitUntil(source, shouldExist: false)).Should().BeNull("source root must be gone after move");
        (await WaitUntil($"{source}/Alice", shouldExist: false)).Should().BeNull("source child must be gone after move");
        (await WaitUntil($"{source}/Team/Bob", shouldExist: false)).Should().BeNull("source nested child must be gone after move");
    }
}
