using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// <see cref="MeshNode.Version"/> is the node's own revision counter: <b>+1 per REAL change, and
/// nothing at all for a write that changes nothing</b> (Doc/Architecture/MeshNodeVersioning.md).
///
/// <para>Two defects this pins, both of which produced version numbers that had nothing to do with
/// how often the node was edited:</para>
/// <list type="number">
/// <item><b>The hub clock leaked into the node.</b> The old mint was
/// <c>max(Hub.Version, current + 1)</c>, so unrelated traffic on the owning hub moved the number
/// (3 → 47) and a recycle — which resets that clock to 0 — rolled it BACKWARD (#325).</item>
/// <item><b>Writes that changed nothing still minted.</b> Record equality compares
/// <see cref="MeshNode.Content"/> (an <c>object?</c>) by reference, so a lambda that rebuilt
/// identical content slipped past the no-op check; and the <see cref="MeshNode.LastModified"/>
/// audit stamp was applied BEFORE the diff, manufacturing the very difference that made the write
/// look like an edit. A node re-saved / re-imported / re-asserted N times gained N revisions and N
/// history rows — the "v1170 with no edits" report.</item>
/// </list>
/// </summary>
public class NodeVersionCountsRealChangesTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Body = "# body";

    /// <summary>
    /// Each real edit advances the counter by EXACTLY one — not by the hub clock's jump, and not
    /// by two (the write path mints, and <c>MeshNodeTypeSource.UpdateImpl</c> must NOT re-stamp a
    /// node whose version the write path already advanced).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RealChange_AdvancesVersionByExactlyOne()
    {
        var path = $"{TestPartition}/version-counts-{Guid.NewGuid():N}";
        await NodeFactory.CreateNode(NewNode(path, "v1")).Should().Within(30.Seconds()).Emit();

        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var created = await ReadStable(storage, path);
        created.Version.Should().Be(1, "a freshly created node starts at revision 1");

        var stream = GetClient(c => c.AddData()).GetWorkspace().GetMeshNodeStream(path);

        stream.Update(n => n with { Name = "v2" })
            .Subscribe(_ => { }, ex => Output.WriteLine($"[version] write 1 error: {ex.Message}"));
        var afterFirst = await ReadStable(storage, path, n => n.Name == "v2");
        afterFirst.Version.Should().Be(created.Version + 1,
            $"one edit is one revision — got {created.Version} → {afterFirst.Version}. A jump means "
            + "the hub clock leaked back into the mint; +2 means the write path AND the persistence "
            + "re-stamp both counted the same edit.");

        stream.Update(n => n with { Name = "v3" })
            .Subscribe(_ => { }, ex => Output.WriteLine($"[version] write 2 error: {ex.Message}"));
        var afterSecond = await ReadStable(storage, path, n => n.Name == "v3");
        afterSecond.Version.Should().Be(afterFirst.Version + 1,
            "the counter keeps advancing by exactly one per edit");
    }

    /// <summary>
    /// A cross-hub <c>stream.Update</c> whose lambda produces an identical node — including a
    /// REBUILT-but-equal <see cref="MeshNode.Content"/>, which record equality alone reports as
    /// changed — must leave both the version and the audit stamp untouched.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task NoOpUpdate_LeavesVersionAndLastModifiedUntouched()
    {
        var path = $"{TestPartition}/version-noop-{Guid.NewGuid():N}";
        await NodeFactory.CreateNode(NewNode(path, "stable")).Should().Within(30.Seconds()).Emit();

        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var before = await ReadStable(storage, path);

        var stream = GetClient(c => c.AddData()).GetWorkspace().GetMeshNodeStream(path);
        // Same values throughout, but a FRESH content instance: reference equality fails, so only
        // a serialized diff can recognise this as the no-op it is.
        stream.Update(n => n with
            {
                Name = n.Name,
                Content = new MarkdownContent { Content = Body },
            })
            .Subscribe(_ => { }, ex => Output.WriteLine($"[version] no-op error: {ex.Message}"));

        var after = await ReadStable(storage, path);
        after.Version.Should().Be(before.Version,
            "a write that changes nothing must not mint a revision (or a version-history row)");
        after.LastModified.Should().Be(before.LastModified,
            "the LastModified audit stamp is applied AFTER the diff — a no-op never earns it");
    }

    /// <summary>
    /// The same rule on the <see cref="IMeshService.UpdateNode"/> surface (the MCP <c>update</c>
    /// tool, importers, GitSync): re-asserting the node exactly as it stands is not an edit.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task IdenticalUpdateNode_LeavesVersionUntouched()
    {
        var path = $"{TestPartition}/version-reassert-{Guid.NewGuid():N}";
        await NodeFactory.CreateNode(NewNode(path, "reassert")).Should().Within(30.Seconds()).Emit();

        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var before = await ReadStable(storage, path);

        // Re-assert the live node verbatim — the shape an importer / re-install produces.
        await NodeFactory.UpdateNode(before).Should().Within(30.Seconds()).Emit();

        var after = await ReadStable(storage, path);
        after.Version.Should().Be(before.Version,
            "re-asserting unchanged state through UpdateNode must not mint a revision");

        // …and the guard must not over-skip: one changed field still advances the counter.
        await NodeFactory.UpdateNode(before with { Name = "edited" })
            .Should().Within(30.Seconds()).Emit();
        var edited = await ReadStable(storage, path, n => n.Name == "edited");
        edited.Version.Should().Be(before.Version + 1,
            "a genuine field change is still exactly one revision");
    }

    private MeshNode NewNode(string path, string name) =>
        new(path.Split('/')[^1], TestPartition)
        {
            Name = name,
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = Body },
            State = MeshNodeState.Active,
        };

    // Reads until the persisted node satisfies the predicate AND its Version is unchanged across
    // 4 consecutive samples (~1.2s quiet — past the 200ms persist debounce), so an enrichment /
    // debounce trail cannot masquerade as churn, and a stray no-op write cannot hide behind the
    // read. The quiet window is the only way to assert that NOTHING happened.
    private async Task<MeshNode> ReadStable(
        IStorageAdapter storage, string path, Func<MeshNode, bool>? predicate = null)
    {
        MeshNode? last = null;
        var stable = 0;
        for (var i = 0; i < 100 && stable < 4; i++)
        {
            var current = await storage.Read(path, Mesh.JsonSerializerOptions).FirstAsync().ToTask();
            stable = current is not null && last is not null
                     && current.Version == last.Version
                     && current.LastModified == last.LastModified
                     && (predicate is null || predicate(current))
                ? stable + 1
                : 0;
            last = current ?? last;
            await Task.Delay(300);
        }
        last.Should().NotBeNull($"node {path} must be persisted");
        // 🚨 The loop can also exit on the iteration cap. Returning an UNSTABLE snapshot would
        // silently weaken every assertion built on it (a "version unchanged" check would be
        // reading a value that is still moving), so the cap is a failure, not a result.
        stable.Should().BeGreaterThanOrEqualTo(4,
            $"node {path} must settle (4 consecutive samples with the same Version + LastModified) "
            + "before it can be asserted on — the read hit the iteration cap while still churning");
        return last!;
    }
}
