using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="StaleMainNodeRepair"/> — the backward half of #2939, filed as #2970.
///
/// <para><b>Why every fixture is seeded through the STORAGE adapter and not through
/// <c>CreateNode</c>.</b> The forward fix runs <c>RepairStaleSelfDefaultMainNode</c> on every create
/// and every upsert, so a corrupt node handed to the ordinary write path is healed on the way in and
/// the test would be asserting on a node that was never corrupt. The rows this repair exists for
/// were written BEFORE that fix, which is a durable row the serve path never re-stamped —
/// so the fixture writes one, and <see cref="SeedCorruptAsync"/> reads it back and FAILS if the
/// stale pointer did not survive. Without that read-back the whole suite could pass against nodes
/// that were healthy all along.</para>
///
/// <para><b>The assertions are on observable state</b> — the node's persisted
/// <see cref="MeshNode.MainNode"/> and what a real query shape returns — never on a log line or a
/// count the test itself kept. Nothing is mocked: the repair runs against the same monolith mesh,
/// storage adapter and query providers the portal uses.</para>
/// </summary>
public class StaleMainNodeRepairTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The one place a wait is decided (<see cref="TestTimeouts"/>) — never a hand-written literal,
    /// which is a guess about machine speed that CI's ~1.7× ratio invalidates. It stays strictly
    /// below each <c>[Fact(Timeout = …)]</c> above, so a wedge is reported as the assertion that did
    /// not converge rather than as an anonymous xunit kill.
    /// </summary>
    private static readonly TimeSpan Budget = TestTimeouts.Convergence;

    /// <summary>
    /// Writes a node straight to storage carrying <paramref name="mainNode"/>, then reads it back and
    /// asserts the stale pointer PERSISTED — so a fixture that the storage layer silently normalised
    /// fails here rather than turning a later assertion green for the wrong reason.
    /// </summary>
    private async Task<MeshNode> SeedCorruptAsync(string id, string ns, string mainNode)
    {
        var seeded = await SeedRawAsync(id, ns, mainNode);
        seeded.MainNode.Should().Be(mainNode,
            "the fixture must actually be corrupt — otherwise this suite proves nothing");
        seeded.IsStaleSelfDefaultMainNode().Should().BeTrue(
            "the shared predicate must recognise the fixture as the shape under repair");
        return seeded;
    }

    /// <summary>Writes a node straight to storage and returns it as persisted.</summary>
    private async Task<MeshNode> SeedRawAsync(string id, string ns, string mainNode)
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var node = new MeshNode(id, ns)
        {
            MainNode = mainNode,
            Name = id,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };
        var written = await storage.Write(node, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(Budget).Await();
        written.Should().NotBeNull("the storage adapter must own and accept the seeded path");
        return await ReadRawAsync(node.Path);
    }

    /// <summary>Reads the DURABLE row — the thing the repair has to move.</summary>
    private async Task<MeshNode> ReadRawAsync(string path)
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var node = await storage.Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(Budget).Await();
        node.Should().NotBeNull($"'{path}' must exist in storage");
        return node!;
    }

    private Task<StaleMainNodeRepairReport> RepairAsync(params string[] roots)
        => StaleMainNodeRepair.Repair(Mesh, roots).Timeout(Budget).Await();

    private Task<StaleMainNodeRepairReport> DetectAsync(params string[] roots)
        => StaleMainNodeRepair.Detect(Mesh, roots).Timeout(Budget).Await();

    /// <summary>
    /// The mutual cycle from the issue: two Active copies of one node in different partitions, each
    /// naming the other. BOTH ends must end up self-pointing — repairing one and leaving the other
    /// is the failure mode the issue calls out.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task A_cycle_pair_is_repaired_at_both_ends()
    {
        // Alpha/Skill/deployment ⇄ CycleSkill/deployment — the Hosting/Skill ⇄ Skill shape.
        var left = await SeedCorruptAsync("deployment", "Alpha/Skill", "CycleSkill/deployment");
        var right = await SeedCorruptAsync("deployment", "CycleSkill", "Alpha/Skill/deployment");

        var report = await RepairAsync("Alpha", "CycleSkill");

        report.Findings.Should().HaveCount(2, "both ends carry the shape independently");
        report.Findings.Should().OnlyContain(f => f.Shape == StaleMainNodeShape.Cycle,
            "each end's pointer resolves to a node that points back at it");
        report.Findings.Should().OnlyContain(f => f.Repaired && f.Error == null);
        report.RepairedCount.Should().Be(2);

        (await ReadRawAsync(left.Path)).MainNode.Should().Be(left.Path,
            "the left end is self-pointing again");
        (await ReadRawAsync(right.Path)).MainNode.Should().Be(right.Path,
            "the right end is repaired on its own merits, not as a side effect of the left");
    }

    /// <summary>
    /// The shape the issue does NOT describe and which a cycle-only repair would skip: the pointer
    /// names a node that does not exist. Two of the seven measured on memex are this.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task A_dangling_pointer_is_repaired_when_its_target_does_not_exist()
    {
        var node = await SeedCorruptAsync("email", "Beta/Skill", "NoSuchPartition/email");

        var report = await RepairAsync("Beta");

        report.Findings.Should().ContainSingle();
        report.Findings[0].Shape.Should().Be(StaleMainNodeShape.DanglingMissingTarget,
            "nothing lives at the pointed-at path");
        report.Findings[0].Repaired.Should().BeTrue(
            "a missing partner must not make the node unrepairable");
        report.Findings[0].StaleMainNode.Should().Be("NoSuchPartition/email",
            "the report carries the pointer as found, as evidence");

        (await ReadRawAsync(node.Path)).MainNode.Should().Be(node.Path);
    }

    /// <summary>
    /// The second dangling flavour: the target EXISTS but names something else, so there is no cycle
    /// to close. A repair that assumed a partner pointing back would mis-handle this.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task A_pointer_to_a_node_that_does_not_point_back_is_repaired()
    {
        // The target is a perfectly healthy node in another partition — it does NOT point back.
        var target = await SeedRawAsync("instance", "Gamma", "Gamma/instance");
        target.MainNode.Should().Be(target.Path, "the target is healthy and must stay that way");

        var node = await SeedCorruptAsync("instance", "Delta/Skill", "Gamma/instance");

        var report = await RepairAsync("Delta", "Gamma");

        report.Findings.Should().ContainSingle("only the corrupt node carries the shape");
        report.Findings[0].Path.Should().Be(node.Path);
        report.Findings[0].Shape.Should().Be(StaleMainNodeShape.DanglingUnrelatedTarget);
        report.Findings[0].Repaired.Should().BeTrue();

        (await ReadRawAsync(node.Path)).MainNode.Should().Be(node.Path);
        (await ReadRawAsync(target.Path)).MainNode.Should().Be(target.Path,
            "a healthy node that merely happens to be pointed AT is never rewritten");
    }

    /// <summary>
    /// The false-positive arm. A healthy main node and a DELIBERATE cross-node pointer (a satellite,
    /// whose MainNode names its owner under a different id) must both be left alone — the predicate
    /// that separates them is the one the write paths already apply.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task Healthy_nodes_and_deliberate_pointers_are_left_untouched()
    {
        var healthy = await SeedRawAsync("readme", "Epsilon", "Epsilon/readme");
        // A deliberate pointer: MainNode's last segment is NOT this node's id, so it is a real
        // reference to another node rather than a frozen self-default.
        var deliberate = await SeedRawAsync("_Policy", "Epsilon", "Epsilon/readme");

        var report = await RepairAsync("Epsilon");

        report.Findings.Should().BeEmpty(
            "neither shape is a stale self-default, so neither is a candidate");
        // 🚨 The clean result must be measured on a NON-EMPTY set, or it is the "verification that
        // cannot fail" trap: a sweep that enumerated nothing and read nothing also reports zero
        // findings, and the two are indistinguishable without these two counts.
        report.PathsScanned.Should().BeGreaterThan(0,
            "the sweep must actually have enumerated this partition");
        report.NodesRead.Should().BeGreaterThan(0,
            "and must actually have READ the nodes it then judged clean");

        (await ReadRawAsync(healthy.Path)).MainNode.Should().Be(healthy.Path);
        (await ReadRawAsync(deliberate.Path)).MainNode.Should().Be("Epsilon/readme",
            "a deliberate pointer must survive the sweep verbatim");
    }

    /// <summary>Detect is the measurement pass: it finds the node and writes nothing.</summary>
    [Fact(Timeout = 120000)]
    public async Task Detect_reports_the_finding_without_writing()
    {
        var node = await SeedCorruptAsync("policy", "Zeta/Skill", "OtherZeta/policy");

        var report = await DetectAsync("Zeta");

        report.Wrote.Should().BeFalse();
        report.Findings.Should().ContainSingle();
        report.Findings[0].Repaired.Should().BeFalse();
        report.RepairedCount.Should().Be(0);

        (await ReadRawAsync(node.Path)).MainNode.Should().Be("OtherZeta/policy",
            "a detect-only pass must leave the row exactly as it found it");
    }

    /// <summary>
    /// Idempotence, and the reason it holds: the repair writes the one value the predicate cannot
    /// match. The second pass is a no-op because there is nothing left to find — not because the
    /// repair remembers having run.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task A_second_run_is_a_no_op_and_an_unaffected_mesh_is_safe()
    {
        var node = await SeedCorruptAsync("remote", "Eta/Skill", "OtherEta/remote");

        var first = await RepairAsync("Eta");
        first.Findings.Should().ContainSingle();
        first.RepairedCount.Should().Be(1);

        var second = await RepairAsync("Eta");
        second.Findings.Should().BeEmpty("the repaired node no longer matches the predicate");
        second.RepairedCount.Should().Be(0);
        second.PathsScanned.Should().BeGreaterThan(0,
            "the second pass still enumerated the partition — it found nothing, it did not skip");

        (await ReadRawAsync(node.Path)).MainNode.Should().Be(node.Path,
            "the second pass did not disturb the repaired value");

        // A partition with nothing wrong in it: zero findings, zero writes, no fault.
        await SeedRawAsync("clean", "Theta", "Theta/clean");
        var healthyMesh = await RepairAsync("Theta");
        healthyMesh.Findings.Should().BeEmpty();
        healthyMesh.RepairedCount.Should().Be(0);
    }

    /// <summary>
    /// The DEFAULT call shape — no roots, so the sweep starts from the storage adapter's own root
    /// listing and walks the whole tree. Scoping every other test would leave
    /// <c>ListChildPaths(null)</c> — the branch a real run against a portal takes — never executed.
    ///
    /// <para>The node is seeded in a partition the test never names to the repair, so finding it is
    /// the enumeration's own work rather than something the caller pointed at. A second node is
    /// seeded in a DIFFERENT top-level partition, and the path count must cover both: that is what
    /// distinguishes a root listing that fans out across partitions from one that reached only the
    /// first thing it found. (The count is exactly the durable rows this test wrote — the harness's
    /// other nodes are statically seeded, not storage rows, so they are legitimately not walked.)
    /// </para>
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task An_unscoped_sweep_walks_the_whole_tree_and_finds_it()
    {
        var node = await SeedCorruptAsync("platform-update", "Kappa/Skill", "OtherKappa/platform-update");
        var elsewhere = await SeedRawAsync("bystander", "Lambda", "Lambda/bystander");

        var report = await StaleMainNodeRepair.Repair(Mesh).Timeout(Budget).Await();

        report.Findings.Select(f => f.Path).Should().Contain(node.Path,
            "an unscoped sweep must reach a partition nobody named");
        report.NodesRead.Should().BeGreaterThanOrEqualTo(2,
            "the root listing must fan out across BOTH seeded partitions, not stop at the first");

        (await ReadRawAsync(node.Path)).MainNode.Should().Be(node.Path);
        (await ReadRawAsync(elsewhere.Path)).MainNode.Should().Be(elsewhere.Path,
            "the healthy bystander in the other partition is read and left alone");
    }

    /// <summary>
    /// 🚨 <b>The one that matters.</b> The user-visible defect is not the field, it is that the node
    /// is missing from listings — so the repair is only proven by a query shape that could not see
    /// the node before and can see it after.
    ///
    /// <para>The shape is <c>is:main</c> (SQL <c>n.main_node = n.path</c>), named explicitly because
    /// the issue asks for it to be: it is the framework's literal definition of a main node, it is
    /// what the home catalog's own listings carry (<c>namespace:{path} is:main</c>), and Postgres'
    /// <c>search_across_schemas</c> hard-filters every union branch on that same predicate
    /// unconditionally — which is why on memex the node is absent from every listing and not merely
    /// from this one.</para>
    ///
    /// <para>The first assertion is the CONTROL ARM: without <c>is:main</c> the very same query
    /// returns the node. Without it a green result here could just as well mean the node was never
    /// indexed at all, and the test would prove nothing about MainNode.</para>
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task A_repaired_node_becomes_visible_to_the_listing_that_could_not_see_it()
    {
        var node = await SeedCorruptAsync("ci-policy", "Iota/Skill", "OtherIota/ci-policy");
        var service = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        async Task<string[]> PathsAsync(string query)
        {
            var results = await service.QueryAsync<MeshNode>(query).ToArrayAsync();
            return results.Select(n => n.Path).ToArray();
        }

        // CONTROL: the node IS in the index, Active and fully formed.
        (await PathsAsync($"namespace:{node.Namespace}"))
            .Should().Contain(node.Path,
                "the corrupt node is present and indexed — its absence below is caused by MainNode, "
                + "not by the node being missing");

        // THE DEFECT: the same listing with is:main cannot see it.
        (await PathsAsync($"namespace:{node.Namespace} is:main"))
            .Should().NotContain(node.Path,
                "main_node != path drops the node out of is:main — this is the user-visible symptom");

        var report = await RepairAsync("Iota");
        report.RepairedCount.Should().Be(1);

        // THE OUTCOME: it is back in the listing.
        (await PathsAsync($"namespace:{node.Namespace} is:main"))
            .Should().Contain(node.Path,
                "restoring MainNode == Path puts the node back into is:main — the whole point");
    }
}
