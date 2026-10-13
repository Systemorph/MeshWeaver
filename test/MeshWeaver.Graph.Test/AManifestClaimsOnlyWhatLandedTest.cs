using MeshWeaver.Messaging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The per-node import manifest claims only what LANDED (issue #6628).</b>
///
/// <para>The manifest is a <c>{path → source-token}</c> map, and an entry is a claim: "the mesh holds
/// this token's content at this path". The next import skips a present node whose entry matches its
/// source token without looking at the node, and the partition-level short-circuit skips the whole
/// partition when every entry matches. So an entry recorded for content that never landed is
/// permanent: on memex.systemorph.com two Code nodes stayed on a day-old revision while every later
/// non-force update answered "an earlier FULL import already recorded this exact content".</para>
///
/// <para>A FULL (unscoped) run used to record every source path at its new token unless the write
/// FAILED — including paths it preserved under the two-way policy, skipped as claimed, or whose
/// upsert was answered "already exists" without being applied. These tests pin each of those
/// shapes. Negative control: against the pre-fix <c>WriteManifest</c> the two end-to-end tests fail
/// at the third run's <c>WrittenPaths</c> assertion (the node is skipped as "unchanged"), and the
/// manifest test fails because the unwritten path carries the new token.</para>
/// </summary>
public class AManifestClaimsOnlyWhatLandedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly AccessContext Author = new()
    {
        ObjectId = "alice",
        Name = "Alice",
        Email = "alice@acme.com",
    };

    /// <summary>
    /// (a) A FULL run that PRESERVES a server-newer node did not write it, so it must not claim the
    /// source's new token for it — the next run without the two-way protection rewrites the node.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task FullRun_APreservedNode_IsNotClaimed_AndTheNextRunRewritesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = "Mc" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{partition}/A";
        var horizon = DateTimeOffset.UtcNow.AddMinutes(-1);
        var source = new FakeRepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "A", "repo-v1"), Page(partition, "B", "repo-v1")],
        };

        (await StaticRepoImporter.ImportSource(Mesh, source)
                .FirstAsync().Timeout(120.Seconds()).Await(ct))
            .Outcome.Should().Be("Imported");

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        IObservable<MeshNode> edit;
        using (access.SwitchAccessContext(Author))
            edit = Mesh.GetWorkspace().GetMeshNodeStream(path).Update(node => node with
            {
                Content = new MarkdownContent { Content = "# Page A\n\nlocal-edit" }
            });
        await edit.FirstAsync().Timeout(60.Seconds()).Await(ct);

        // 2. FULL run (no git-diff scope) under two-way protection: A is preserved, not written.
        source.Nodes = [Page(partition, "A", "repo-v2"), Page(partition, "B", "repo-v1")];
        var preserved = await StaticRepoImporter.ImportSource(
                Mesh, source,
                policy: new ImportConflictPolicy(PreserveServerNewer: true, Since: horizon))
            .FirstAsync().Timeout(120.Seconds()).Await(ct);
        Output.WriteLine($"preserving run: outcome={preserved.Outcome} preserved={preserved.Preserved} "
            + $"written=[{string.Join(", ", preserved.WrittenPaths)}]");
        preserved.Preserved.Should().Be(1, "the fixture must reach the preserved state first");
        preserved.WrittenPaths.Should().NotContain(path);

        // 3. The server copy no longer wins (no two-way policy — the local edit is discarded). The
        //    manifest must not say "already at repo-v2" for a node that never received it.
        var after = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(120.Seconds()).Await(ct);
        Output.WriteLine($"later run: outcome={after.Outcome} written=[{string.Join(", ", after.WrittenPaths)}]");
        after.WrittenPaths.Should().Contain(path,
            "the preserving FULL run did not write repo-v2, so it may not have claimed repo-v2's token; "
            + "a claim would make this run skip the node as unchanged, for good");
        (await BodyContaining(path, "repo-v2")).Should().Contain("repo-v2");
        after.WrittenPaths.Should().NotContain($"{partition}/B",
            "a node the earlier runs DID write keeps its honest claim — the incremental skip still fires");
    }

    /// <summary>
    /// (a') A FULL run that SKIPS a node as claimed (SyncBehavior other than Include) did not write
    /// it either. Once the claim is lifted, the next run must bring the node to the source.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task FullRun_AClaimedNode_IsNotClaimed_AndIsRewrittenOnceTheClaimIsLifted()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = "Mk" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{partition}/A";
        var source = new FakeRepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "A", "repo-v1")],
        };

        (await StaticRepoImporter.ImportSource(Mesh, source)
                .FirstAsync().Timeout(120.Seconds()).Await(ct))
            .Outcome.Should().Be("Imported");

        await SetSyncBehavior(path, SyncBehavior.ExcludeThisAndChildren);

        source.Nodes = [Page(partition, "A", "repo-v2")];
        var claimed = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(120.Seconds()).Await(ct);
        Output.WriteLine($"claimed run: outcome={claimed.Outcome} written=[{string.Join(", ", claimed.WrittenPaths)}]");
        claimed.WrittenPaths.Should().NotContain(path, "the fixture must reach the claimed skip first");
        (await Body(path)).Should().Contain("repo-v1");

        await SetSyncBehavior(path, SyncBehavior.Include);

        var after = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(120.Seconds()).Await(ct);
        Output.WriteLine($"later run: outcome={after.Outcome} written=[{string.Join(", ", after.WrittenPaths)}]");
        after.Outcome.Should().NotBe("Skipped",
            "the claimed run wrote nothing at A, so its manifest cannot match the source and the "
            + "partition-level short-circuit must not fire");
        after.WrittenPaths.Should().Contain(path,
            "the claimed FULL run did not write repo-v2, so the manifest may not say it did");
        (await BodyContaining(path, "repo-v2")).Should().Contain("repo-v2");
    }

    /// <summary>
    /// (b) The manifest itself: a source path this run neither wrote nor failed (preserved, claimed,
    /// skipped, or answered without being applied) keeps the PREVIOUS entry — or none — and only an
    /// acknowledged write records the new token. Pure, so it pins the rule without depending on how
    /// the write went missing.
    /// </summary>
    [Fact]
    public void ComputeManifest_ClaimsTheNewTokenOnlyForWrittenPaths()
    {
        var opts = Mesh.JsonSerializerOptions;
        var partition = "Mp";
        var v1 = new[] { Page(partition, "A", "v1"), Page(partition, "B", "v1"), Page(partition, "C", "v1") };
        var v2 = new[] { Page(partition, "A", "v2"), Page(partition, "B", "v2"), Page(partition, "C", "v2"), Page(partition, "D", "v2") };
        var previous = StaticRepoImporter.ComputeManifest(
            v1, ImmutableDictionary<string, string>.Empty, [], [], v1.Select(n => n.Path).ToArray(), opts);
        string Token(MeshNode n) => PartitionSourceFingerprint.ComputeNodeToken(n, opts);

        var next = StaticRepoImporter.ComputeManifest(
            v2, previous, prunedPaths: [], failures: [],
            writtenPaths: [$"{partition}/A"], opts);

        next[$"{partition}/A"].Should().Be(Token(v2[0]), "A was written — the only new claim");
        next[$"{partition}/B"].Should().Be(Token(v1[1]),
            "B was not written (preserved / claimed / not applied): it keeps what the last run that "
            + "wrote it recorded, so the next pass sees the token differ and evaluates it");
        next[$"{partition}/C"].Should().Be(Token(v1[2]));
        next.ContainsKey($"{partition}/D").Should().BeFalse(
            "D never landed and no earlier run recorded it — there is nothing to claim");
    }

    /// <summary>
    /// (b') An upsert answered "already exists" applied NOTHING. It satisfies presence (the root
    /// path needs only that), but it is not an acknowledged write and must never be counted as one —
    /// that is what let a write that did not land put its token in the manifest.
    /// </summary>
    [Fact]
    public void AnAlreadyExistsAnswer_IsPresence_NotAnAcknowledgedWrite()
    {
        var node = Page("Mx", "A", "v1");
        StaticRepoImporter.AcknowledgedWrites(CreateOrUpdateNodeResponse.Updated(node)).Should().Be(1);
        StaticRepoImporter.AcknowledgedWrites(
                CreateOrUpdateNodeResponse.Fail("Node already exists at path: Mx/A"))
            .Should().Be(0, "nothing was applied, so nothing may be claimed");
        StaticRepoImporter.AcknowledgedWrites(
                CreateOrUpdateNodeResponse.Fail("validation failed", NodeUpsertRejectionReason.ValidationFailed))
            .Should().BeNull("any other refusal faults the write");
    }

    private async Task SetSyncBehavior(string path, SyncBehavior behavior)
    {
        var ct = TestContext.Current.CancellationToken;
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        IObservable<MeshNode> update;
        using (access.SwitchAccessContext(Author))
            update = Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Update(node => node with { SyncBehavior = behavior });
        await update.FirstAsync().Timeout(60.Seconds()).Await(ct);

        // The importer decides claims from its subtree snapshot (an eventually-consistent query), so
        // wait until that read reflects the change — otherwise the run measures the lag, not the rule.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}").AsSystem())
                .Take(1))
            .Where(c => c.Items.Any(n =>
                string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase) && n.SyncBehavior == behavior))
            .FirstAsync().Timeout(60.Seconds()).Await(ct);
    }

    /// <summary>The live markdown body, read through the authoritative node stream.</summary>
    private async Task<string> Body(string path)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .FirstAsync().Timeout(30.Seconds()).Await();
        return node.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content ?? "";
    }

    /// <summary>Waits for the write's content change to reach the live node stream.</summary>
    private async Task<string> BodyContaining(string path, string expected)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content?.Contains(expected) == true)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        return node.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content ?? "";
    }

    private static MeshNode Space(string partition) => new(partition)
    {
        Name = "Manifest Claims", NodeType = "Space", State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = "# Manifest Claims\n\nfixture." }
    };

    private static MeshNode Page(string partition, string id, string revision) =>
        new(id, partition)
        {
            NodeType = "Markdown", Name = $"Page {id}", State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = $"# Page {id}\n\n{revision}" }
        };

    private sealed class FakeRepoSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public List<MeshNode> Nodes { get; set; } = [];
        public MeshNode? Root { get; set; }
        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
    }
}
