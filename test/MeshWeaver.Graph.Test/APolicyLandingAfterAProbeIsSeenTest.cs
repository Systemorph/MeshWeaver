using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A read verdict cached before a partition's access landed must not outlive it</b> — found
/// chasing the intermittent Education e2e denial on a freshly installed package page.
///
/// <para><b>The shape.</b> A viewer opens a package page while the package is still installing: the
/// partition has no <c>_Policy</c> yet, so the read gate of <c>IMeshNodeStreamCache</c> evaluates a
/// denial and caches it per <c>(path, user)</c>. The installer then writes the partition root, its
/// PublicRead <c>_Policy</c> and the page. The permission fold sees the policy at once — and the
/// read gate went on serving the denial it had cached, because nothing tied that entry to the
/// fold's inputs: only its 30 s TTL could end it.</para>
///
/// <para><b>Measured on the unfixed build</b> (this fixture): the fold answered Read = true
/// immediately after the writes, the <c>[RequiresPermission]</c> delivery gate on the page's own hub
/// answered the node, and <c>get</c> — which reads through the stream cache — answered
/// <c>Not found</c> for 30 s, then the page. Fixed, the first read after the fold converges is the
/// page. The revocation direction is the mirror image: a cached GRANT outlived the policy that gave
/// it.</para>
///
/// <para>Each test waits for the FOLD to reach the new verdict first — that is the propagation the
/// read is entitled to wait for — and then reads exactly once. No poll, no retry: a cached verdict
/// that survives the change fails the single read.</para>
/// </summary>
public class APolicyLandingAfterAProbeIsSeenTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Reader = "late-policy-reader";

    // ConfigureMeshBase, never ConfigureMesh: the default test mesh grants Public Admin, under which
    // nothing is ever denied and the probe below would measure nothing.
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder);

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static AccessContext Identity(string userId) => new() { ObjectId = userId, Name = userId };

    /// <summary>The MCP / REST <c>get</c> as the reader — it reads through the stream cache's read gate.</summary>
    private async Task<string> Get(string path, CancellationToken cancellationToken)
    {
        Access.SetContext(Identity(Reader));
        var answer = await new MeshOperations(Mesh).Get("@" + path)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("get must answer", cancellationToken);
        Output.WriteLine($"get {path} as {Reader}: {answer[..Math.Min(120, answer.Length)]}");
        return answer;
    }

    /// <summary>Waits until the permission fold itself answers <paramref name="readable"/>.</summary>
    private Task<bool> FoldReaches(string path, bool readable, CancellationToken cancellationToken)
        => Mesh.CheckPermission(path, Reader, Permission.Read)
            .Where(granted => granted == readable)
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"the fold must reach Read={readable} on {path}", cancellationToken);

    private Task<MeshNode> CreateAsSystem(MeshNode node, CancellationToken cancellationToken)
        => Access.RunAsSystem(() => NodeFactory.CreateNode(node))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: cancellationToken);

    private static string NewPartition(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static bool IsThePage(string answer) => answer.Contains("\"Page\"", StringComparison.Ordinal)
        && !answer.StartsWith("Not found", StringComparison.Ordinal);

    [Fact(Timeout = 90_000)]
    public async Task AReadDeniedBeforeThePolicyLanded_IsServedOnceItHas()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = NewPartition("LatePolicy");
        var page = $"{partition}/Page";

        // 1. The probe, before the partition has any policy: denied (answered as absent), and the
        //    verdict is now cached for (page, Reader).
        (await Get(page, ct)).Should().StartWith("Not found",
            "the control: nothing grants the reader anything here yet");

        // 2. The install: root, PublicRead policy, page.
        await CreateAsSystem(new MeshNode(partition) { Name = partition, NodeType = "Markdown" }, ct);
        await CreateAsSystem(AssignmentNodeFactory.Policy(partition, new PartitionAccessPolicy { PublicRead = true }), ct);
        await CreateAsSystem(new MeshNode("Page", partition) { Name = "Page", NodeType = "Markdown" }, ct);

        // 3. Once the fold sees the policy, the very next read must see it too.
        await FoldReaches(page, readable: true, ct);
        IsThePage(await Get(page, ct)).Should().BeTrue(
            "the fold grants Read, so a read that still answers the denial is serving a verdict "
            + "cached from inputs that no longer exist");
    }

    [Fact(Timeout = 90_000)]
    public async Task AReadGrantedByAPolicyThatIsWithdrawn_IsRefusedOnceItIs()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = NewPartition("GonePolicy");
        var page = $"{partition}/Page";
        await CreateAsSystem(new MeshNode(partition) { Name = partition, NodeType = "Markdown" }, ct);
        var policy = await CreateAsSystem(
            AssignmentNodeFactory.Policy(partition, new PartitionAccessPolicy { PublicRead = true }), ct);
        await CreateAsSystem(new MeshNode("Page", partition) { Name = "Page", NodeType = "Markdown" }, ct);

        await FoldReaches(page, readable: true, ct);
        IsThePage(await Get(page, ct)).Should().BeTrue("the control: the policy publishes the page");

        await Access.RunAsSystem(() => NodeFactory.DeleteNode(policy.Path))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        await FoldReaches(page, readable: false, ct);
        (await Get(page, ct)).Should().StartWith("Not found",
            "the policy that granted the read is gone and the fold says so — a cached grant must not "
            + "outlive it");
    }

    /// <summary>
    /// 🚨 <b>The read taken INSIDE the fold's own change callback</b> (review of #7046). A fold
    /// subscriber is notified while the policy query is still delivering its new snapshot, so a
    /// read it opens from that callback runs before the query has finished committing. It must
    /// not be answered from the verdict cached under the previous snapshot. Measured: with the
    /// generation advanced only after delivery, this read was refused from the cached denial.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task AReadOpenedFromTheFoldCallback_SeesTheChangeThatFiredIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = NewPartition("CallbackPolicy");
        var page = $"{partition}/Page";
        await CreateAsSystem(new MeshNode(partition) { Name = partition, NodeType = "Markdown" }, ct);
        await CreateAsSystem(new MeshNode("Page", partition) { Name = "Page", NodeType = "Markdown" }, ct);

        // The denial is cached for (page, Reader) before the policy exists.
        (await Get(page, ct)).Should().StartWith("Not found", "the control: no policy grants anything yet");

        var readFromCallback = new ReplaySubject<string>(1);
        using var callbackReads = new CompositeDisposable();
        using var fold = Mesh.CheckPermission(page, Reader, Permission.Read)
            .Where(granted => granted)
            .Take(1)
            .Subscribe(_ =>
            {
                // Synchronously, on the fold's delivery: the read gate decides at subscribe time.
                using (Access.SwitchAccessContext(Identity(Reader)))
                    callbackReads.Add(Mesh.GetWorkspace().GetMeshNodeStream(page)
                        .Where(n => n is not null)
                        .Take(1)
                        .Select(n => "read:" + n!.Path)
                        .Catch((UnauthorizedAccessException ex) => Observable.Return("denied:" + ex.Message))
                        .Subscribe(readFromCallback));
            });

        await CreateAsSystem(AssignmentNodeFactory.Policy(partition, new PartitionAccessPolicy { PublicRead = true }), ct);

        var outcome = await readFromCallback.Should().Within(TestTimeouts.Convergence)
            .Emit("the read opened from the fold callback must answer", ct);
        Output.WriteLine($"read from the fold callback: {outcome}");
        outcome.Should().Be("read:" + page,
            "the fold that fired this callback already grants Read, so the read must not be answered "
            + "from the denial cached under the previous snapshot");
    }
}
