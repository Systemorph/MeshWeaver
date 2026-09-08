using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Issue #3589 — a file the repository RETIRED stayed in the mesh, compiled, for ever.</b>
///
/// <para><b>Measured on memex.systemorph.com, 2026-09-07/08.</b> Three <c>Crm/Source/Mail*</c> code
/// nodes deleted from <c>MeshWeaver.Crm</c> on 09-06 were still in the partition — and still compiled
/// into every Crm type — two days later, and <c>Edu/Course</c> had been deleted upstream for
/// <b>53 days</b>. Six retired nodes across two of the portal's fourteen synced partitions; two of
/// them parked at <c>compilationStatus: Error</c> and still being recompiled every boot. Because the
/// #2813 gate hashes the LIVE source set, every Crm bundle was refused on its fingerprint for as long
/// as the three files stayed.</para>
///
/// <para><b>The two halves of the mechanism, and what pins each.</b></para>
///
/// <para><b>1. Ownership.</b> The prune's protection (<see cref="ImportConflictPolicy.PreservesFromPruneOf"/>)
/// asks "was this node changed on the server since the last sync?". The horizon is HELD while
/// anything is preserved, so a previous import's OWN writes sit after it and read as "newer on the
/// server" for ever. The 10:15Z Crm import found all five nodes the repo had deleted and kept all
/// five: <c>"Imported 23 node(s), kept 5 local change(s), pruned 0"</c>.
/// <see cref="ImportConflictPolicy.IsHumanEdit"/> (core#3727) is the ownership test —
/// <see cref="ARetiredFileLeavesTheMesh_AnAuthoredOneStays"/> is that test end to end, against a
/// live mesh rather than a constructed <c>MeshNode</c>.</para>
///
/// <para><b>2. The marker.</b> Fixing the rule is not enough to retire what is already there, and
/// that is what this file exists for. The run that kept those five back stamped its content-addressed
/// marker <c>Succeeded</c> anyway; the next trigger read the marker, answered <c>Skipped</c> WITHOUT
/// READING THE PARTITION, and reported <c>Preserved = 0</c> — a zero nothing measured — which
/// <c>GitHubSyncService.MayAdvanceBaseline</c> took for "the mesh is in sync" and used to advance
/// <c>LastSyncCommitSha</c> to the branch head. The partition was then declared converged to a commit
/// whose tree it did not match, and the prune phase — where every other guard lives — was never
/// reached again. A marker now records what its run LEFT UNDONE, and a run that kept anything back
/// does not licence the next skip.</para>
///
/// <para>🚨 The danger here is the OPPOSITE of the bug: a deletion propagated too eagerly destroys
/// authored content. Both tests below therefore carry an authored node through every pass and assert
/// it survives — including the re-import the fix newly causes.</para>
/// </summary>
public class RetiredSourceLeavesTheMeshTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The person whose edit must survive every prune — the control on the opposite danger.</summary>
    private static readonly AccessContext Author = new()
    {
        ObjectId = "alice",
        Name = "Alice",
        Email = "alice@acme.com",
    };

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>A source that ships exactly the nodes it is given — the repo tree, in memory.</summary>
    private sealed class RepoSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public List<MeshNode> Nodes { get; init; } = [];
        public MeshNode? Root { get; init; }

        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
        public IReadOnlyList<StaticContentSync> EnumerateInlineContentSyncs() => [];
    }

    private static MeshNode Space(string partition) =>
        new(partition) { Name = partition, NodeType = "Space", State = MeshNodeState.Active };

    private static MeshNode Page(string partition, string id) =>
        new(id, partition)
        {
            Name = id,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = $"# {id}" },
        };

    private static string NewPartition() => "Rt" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Two-way, with a horizon set BEFORE anything in the test was written — so every live node
    /// reads as "changed on the server since the last sync" and only
    /// <see cref="ImportConflictPolicy.IsHumanEdit"/> can separate them. That is the live Crm
    /// configuration: the horizon is held while anything is preserved, so it falls ever further
    /// behind the writes the import itself makes.
    /// </summary>
    private static ImportConflictPolicy TwoWaySince(DateTimeOffset horizon) =>
        new(PreserveServerNewer: true, Since: horizon);

    /// <summary>Creates a node AS A PERSON — the identity is captured on the calling thread, so the
    /// scope has to be open across the call that builds the write, not across the subscribe.</summary>
    private async Task CreateAsAuthor(MeshNode node)
    {
        IObservable<MeshNode> write;
        using (Access.SwitchAccessContext(Author))
            write = MeshService.CreateNode(node);
        await write.FirstAsync().Timeout(60.Seconds());
    }

    /// <summary>The partition's immediate children, read through the query index. Bounded retry on
    /// the CONDITION (never a delay): the read model trails the writes the import has already
    /// confirmed through <c>PrunedPaths</c>.</summary>
    private IObservable<IReadOnlyCollection<string>> ChildrenWhen(
        string partition, Func<IReadOnlyCollection<string>, bool> settled) =>
        Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{partition} scope:children"))
                .Take(1))
            .Select(c => (IReadOnlyCollection<string>)c.Items
                .Select(n => n.Path)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray())
            .Where(settled)
            .FirstAsync();

    /// <summary>
    /// 🚨 <b>THE OWNERSHIP TEST, end to end.</b> The repository retires two nodes at once: one the
    /// IMPORT wrote (the <c>Crm/Source/Mail*</c> shape — system identity, or no author at all) and
    /// one a PERSON authored on the portal. Both are absent from the source; both are newer than the
    /// sync horizon. Only authorship separates them, and it must: the import's own writes are the
    /// import's to retire, a person's are not.
    ///
    /// <para>Then the falsifier for the second half of the fix: once that pass CONVERGED — nothing
    /// kept back — the marker is a licence to skip again, and the next pass must take it. A change
    /// that simply stopped trusting the marker would fix #3589 by re-importing every partition on
    /// every boot for ever, which is the far more expensive mistake (#3146: 19 full passes in three
    /// hours on memex-cloud).</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARetiredFileLeavesTheMesh_AnAuthoredOneStays()
    {
        var partition = NewPartition();
        var horizon = DateTimeOffset.UtcNow.AddMinutes(-1);
        var kept = $"{partition}/Kept";
        var retired = $"{partition}/Retired";
        var authored = $"{partition}/Authored";

        // Pass 1 — the repository ships both files; the import writes both, as the import.
        var first = await StaticRepoImporter
            .ImportSource(Mesh, new RepoSource(partition)
            {
                Root = Space(partition),
                Nodes = [Page(partition, "Kept"), Page(partition, "Retired")],
            })
            .FirstAsync().Timeout(240.Seconds());
        Output.WriteLine($"pass 1 = {first.Outcome} ({first.Count} node(s))");
        first.Outcome.Should().Be("Imported");

        // A person adds a node of their own, on the portal, that the repository has never carried.
        await CreateAsAuthor(Page(partition, "Authored"));

        // Pass 2 — the repository has DELETED Retired. Two prune candidates now: the node the import
        // wrote, and the node the person wrote. Both are newer than the horizon.
        var afterRetirement = new RepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "Kept")],
        };
        var second = await StaticRepoImporter
            .ImportSource(Mesh, afterRetirement, policy: TwoWaySince(horizon))
            .FirstAsync().Timeout(240.Seconds());
        Output.WriteLine(
            $"pass 2 = {second.Outcome}, preserved {second.Preserved}, pruned [{string.Join(", ", second.PrunedPaths)}]");

        second.PrunedPaths.Should().Contain(retired,
            "the repository deleted this file and the IMPORT is what wrote it — an import's own "
            + "writes are not a server edit, and judging them by timestamp alone is why three "
            + "Crm/Source/Mail* files stayed compiled for two days after the commit that removed them");
        second.PrunedPaths.Should().NotContain(authored,
            "a person authored this one; a deletion propagated too eagerly destroys authored content, "
            + "which is the failure this fix must not trade for the one it repairs");
        second.Preserved.Should().Be(1,
            "exactly one node was kept back — the authored one — and the count is what the marker "
            + "and the sync baseline both read");

        var settled = await ChildrenWhen(partition,
                children => !children.Contains(retired, StringComparer.OrdinalIgnoreCase))
            .Timeout(120.Seconds());
        settled.Should().Contain(authored,
            "the authored node is still in the mesh after the prune that removed the retired one");
        settled.Should().Contain(kept, "and so is the file the repository still ships");

        // Pass 3 — the SAME source again. Pass 2 kept a node back, so it did NOT converge to the
        // content its marker names, and its marker must not licence a skip. Pre-fix this answered
        // "Skipped" — the exact state memex.systemorph.com was frozen in.
        var third = await StaticRepoImporter
            .ImportSource(Mesh, afterRetirement, policy: TwoWaySince(horizon))
            .FirstAsync().Timeout(240.Seconds());
        Output.WriteLine($"pass 3 = {third.Outcome}, preserved {third.Preserved}");

        third.Outcome.Should().NotBe("Skipped",
            "the run that wrote this marker kept a node back, so the partition does not hold the "
            + "content the marker's id names. Answering Skipped here reports a success on evidence "
            + "the run never gathered, and it is what made the residue permanent: GitHubSyncService "
            + "reads Skipped's unmeasured Preserved = 0 as 'in sync' and advances LastSyncCommitSha "
            + "to the branch head, after which no import ever looks again");

        var afterThird = await ChildrenWhen(partition, children => children.Contains(authored))
            .Timeout(120.Seconds());
        afterThird.Should().Contain(authored,
            "🚨 and the re-import this fix newly causes must not be the thing that finally deletes "
            + "the authored node — the ownership test governs every pass, not just the first");
        afterThird.Should().NotContain(retired, "the retired file does not come back");
    }

    /// <summary>
    /// 🚨 <b>The falsifier.</b> A pass that CONVERGED — pruned what the repository dropped and kept
    /// nothing back — still stamps a marker the next pass may trust, and the next pass must answer
    /// <c>Skipped</c>. Without this, "stop trusting the marker" would pass the test above while
    /// re-importing every partition on every boot for ever.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AConvergedImport_StillShortCircuits()
    {
        var partition = NewPartition();
        var horizon = DateTimeOffset.UtcNow.AddMinutes(-1);
        var retired = $"{partition}/Retired";

        await StaticRepoImporter
            .ImportSource(Mesh, new RepoSource(partition)
            {
                Root = Space(partition),
                Nodes = [Page(partition, "Kept"), Page(partition, "Retired")],
            })
            .FirstAsync().Timeout(240.Seconds());

        var afterRetirement = new RepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "Kept")],
        };

        var converging = await StaticRepoImporter
            .ImportSource(Mesh, afterRetirement, policy: TwoWaySince(horizon))
            .FirstAsync().Timeout(240.Seconds());
        Output.WriteLine(
            $"converging = {converging.Outcome}, preserved {converging.Preserved}, "
            + $"pruned [{string.Join(", ", converging.PrunedPaths)}]");

        converging.PrunedPaths.Should().Contain(retired);
        converging.Preserved.Should().Be(0, "nothing was kept back — this pass converged the partition");

        var again = await StaticRepoImporter
            .ImportSource(Mesh, afterRetirement, policy: TwoWaySince(horizon))
            .FirstAsync().Timeout(240.Seconds());
        Output.WriteLine($"again = {again.Outcome}");

        again.Outcome.Should().Be("Skipped",
            "a converged run's marker is a claim the partition satisfies, so the short-circuit stays "
            + "exactly as cheap as it was. A fix that re-imported here would trade #3589 for #3146");
    }
}
