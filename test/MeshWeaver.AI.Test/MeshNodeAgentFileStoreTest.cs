#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI.Stores;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.Agents.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Integration coverage for <see cref="MeshNodeAgentFileStore"/> — our mesh-node implementation of
/// the Microsoft Agent Framework's <c>AgentFileStore</c> abstraction. Everything runs against a real
/// monolith mesh; nothing is mocked.
///
/// <para>The store's own API is reactive throughout, so the tests drive it exactly the way product
/// code does: subscribe and wait on the CONDITION. Listings and searches are LIVE queries whose first
/// emission can legitimately be the empty seed, so those assertions filter for the expected shape
/// rather than sampling one emission.</para>
/// </summary>
public class MeshNodeAgentFileStoreTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>A fresh root per test so one test's files can never satisfy another's assertions.</summary>
    private MeshNodeAgentFileStore NewStore() =>
        new(Mesh, $"rbuergi/agent-files-{Guid.NewGuid():N}");

    [Fact]
    public async Task Write_ThenRead_ReturnsTheContent()
    {
        var store = NewStore();

        await store.Write("notes.md", "# Findings\nThe rate is 4.2%.").FirstAsync().ToTask();

        var content = await store.Read("notes.md").FirstAsync().Timeout(Bound).ToTask();
        content.Should().Be("# Findings\nThe rate is 4.2%.");
    }

    [Fact]
    public async Task Write_LandsAsARealMeshNode_UnderTheStoreRoot()
    {
        var store = NewStore();

        var node = await store.Write("research/summary.md", "hello").FirstAsync().ToTask();

        node.Path.Should().Be($"{store.Root}/research/summary.md",
            "the store maps a relative path onto the mesh path beneath its root — that IS the point: "
            + "an agent's working file is ordinary, addressable content.");
    }

    [Fact]
    public async Task Write_Overwrites_AnExistingFile()
    {
        var store = NewStore();

        await store.Write("notes.md", "first").FirstAsync().ToTask();
        await store.Write("notes.md", "second").FirstAsync().ToTask();

        var content = await store.Read("notes.md")
            .Where(text => text == "second")
            .FirstAsync().Timeout(Bound).ToTask();
        content.Should().Be("second");
    }

    [Fact]
    public async Task Exists_IsTrueAfterWrite_AndFalseForAMissingFile()
    {
        var store = NewStore();
        await store.Write("present.md", "x").FirstAsync().ToTask();

        (await store.Exists("present.md").FirstAsync().Timeout(Bound).ToTask())
            .Should().BeTrue();
        (await store.Exists("absent.md").FirstAsync().Timeout(Bound).ToTask())
            .Should().BeFalse("an absent node never activates its per-node hub, so absence is read "
                              + "off the bounded probe rather than off an emission");
    }

    [Fact]
    public async Task Read_OfAMissingFile_YieldsNull_RatherThanHanging()
    {
        var store = NewStore();

        var content = await store.ReadAsync("nope.md");

        content.Should().BeNull();
    }

    [Fact]
    public async Task ListChildren_ReportsFilesAndDirectories_DirectoriesFirst()
    {
        var store = NewStore();
        await store.CreateDirectory("research").FirstAsync().ToTask();
        await store.Write("notes.md", "x").FirstAsync().ToTask();

        var entries = await store.ListChildren("")
            .Where(list => list.Count >= 2)
            .FirstAsync().Timeout(Bound).ToTask();

        entries.Select(e => e.Name).Should().Contain(["research", "notes.md"]);
        entries.First().Type.Should().Be(FileStoreEntry.Directory,
            "MAF's contract lists subdirectories before files");
        entries.Single(e => e.Name == "notes.md").Type.Should().Be(FileStoreEntry.File);
    }

    [Fact]
    public async Task Search_FindsMatchingLines_WithLineNumbers()
    {
        var store = NewStore();
        await store.Write("a.md", "alpha\nthe rate is 4.2%\ngamma").FirstAsync().ToTask();
        await store.Write("b.md", "nothing to see").FirstAsync().ToTask();

        var results = await store.Search("", "rate")
            .Where(list => list.Count > 0)
            .FirstAsync().Timeout(Bound).ToTask();

        var match = results.Should().ContainSingle().Subject;
        match.FileName.Should().Be("a.md");
        match.MatchingLines.Should().ContainSingle()
            .Which.LineNumber.Should().Be(2, "line numbers are 1-based, as MAF's shape expects");
    }

    [Fact]
    public async Task Search_HonoursTheGlobFilter()
    {
        var store = NewStore();
        await store.Write("keep.md", "needle").FirstAsync().ToTask();
        await store.Write("skip.txt", "needle").FirstAsync().ToTask();

        var results = await store.Search("", "needle", globPattern: "*.md")
            .Where(list => list.Count > 0)
            .FirstAsync().Timeout(Bound).ToTask();

        results.Select(r => r.FileName).Should().Equal("keep.md");
    }

    [Fact]
    public async Task Delete_RemovesTheFile_AndReportsWhetherItExisted()
    {
        var store = NewStore();
        await store.Write("temp.md", "x").FirstAsync().ToTask();

        (await store.Delete("temp.md").FirstAsync().Timeout(Bound).ToTask())
            .Should().BeTrue();
        (await store.Delete("temp.md").FirstAsync().Timeout(Bound).ToTask())
            .Should().BeFalse("deleting what is not there is reported, not an error");
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("research/../../escape.md")]
    [InlineData("/rooted.md")]
    public async Task PathsThatEscapeTheRoot_AreRejected(string path)
    {
        var store = NewStore();

        var write = () => store.Write(path, "x").FirstAsync().ToTask();

        await write.Should().ThrowAsync<ArgumentException>(
            "MAF requires implementations to guarantee a store path can never escape its root");
    }

    [Fact]
    public async Task SearchAsync_ReturnsOneSnapshotOfTheLiveSearch()
    {
        var store = NewStore();
        await store.Write("a.md", "alpha\nthe rate is 4.2%\ngamma").FirstAsync().ToTask();
        await store.Write("b.md", "nothing to see").FirstAsync().ToTask();

        var results = await store.SearchAsync("", "rate");

        var match = results.Should().ContainSingle().Subject;
        match.FileName.Should().Be("a.md");
        match.MatchingLines.Should().ContainSingle().Which.LineNumber.Should().Be(2);
    }

    [Fact]
    public async Task SearchAsync_WithNoMatches_ReturnsEmpty_RatherThanHanging()
    {
        var store = NewStore();
        await store.Write("a.md", "alpha").FirstAsync().ToTask();

        (await store.SearchAsync("", "nothing-matches-this")).Should().BeEmpty();
    }

    [Fact]
    public async Task Search_NonRecursive_SkipsNestedFiles_AndRecursiveFindsThem()
    {
        var store = NewStore();
        await store.Write("top.md", "needle").FirstAsync().ToTask();
        await store.Write("nested/deep.md", "needle").FirstAsync().ToTask();

        // Default scope is the direct children only.
        var shallow = await store.Search("", "needle")
            .Where(list => list.Count > 0).FirstAsync().Timeout(Bound).ToTask();
        shallow.Select(r => r.FileName).Should().Equal("top.md");

        // Recursive reaches descendants, reporting each path relative to the search root.
        var deep = await store.Search("", "needle", recursive: true)
            .Where(list => list.Count > 1).FirstAsync().Timeout(Bound).ToTask();
        deep.Select(r => r.FileName).Order().Should().Equal("nested/deep.md", "top.md");
    }

    // ── Liveness ──────────────────────────────────────────────────────────────────────────────
    // The reactive methods are LIVE: a consumer binds once and keeps seeing the truth. Each of
    // these subscribes BEFORE the change, so a one-shot implementation would hang them.

    [Fact]
    public async Task Read_StaysLive_AndReEmitsWhenTheFileChanges()
    {
        var store = NewStore();
        await store.Write("live.md", "first").FirstAsync().ToTask();

        var next = store.Read("live.md").Where(text => text == "second")
            .FirstAsync().Timeout(Bound).ToTask();
        await store.Write("live.md", "second").FirstAsync().ToTask();

        (await next).Should().Be("second");
    }

    [Fact]
    public async Task ListChildren_StaysLive_AndReEmitsWhenAFileAppears()
    {
        var store = NewStore();
        await store.Write("one.md", "x").FirstAsync().ToTask();

        var next = store.ListChildren("").Where(list => list.Any(e => e.Name == "two.md"))
            .FirstAsync().Timeout(Bound).ToTask();
        await store.Write("two.md", "y").FirstAsync().ToTask();

        (await next).Select(e => e.Name).Should().Contain(["one.md", "two.md"]);
    }

    [Fact]
    public async Task Search_StaysLive_AndReEmitsWhenMatchingContentAppears()
    {
        var store = NewStore();
        await store.Write("a.md", "nothing yet").FirstAsync().ToTask();

        var next = store.Search("", "needle").Where(list => list.Count > 0)
            .FirstAsync().Timeout(Bound).ToTask();
        await store.Write("b.md", "a needle appears").FirstAsync().ToTask();

        (await next).Select(r => r.FileName).Should().Equal("b.md");
    }

    // ── The Microsoft Agent Framework surface, end to end ─────────────────────────────────────

    [Fact]
    public async Task CreateDirectoryAsync_AndDeleteAsync_WorkThroughTheMafSurface()
    {
        AgentFileStore store = NewStore();

        await store.CreateDirectoryAsync("folder");
        await store.WriteAsync("folder/note.md", "x");

        (await store.ListChildrenAsync("")).Single(e => e.Name == "folder")
            .Type.Should().Be(FileStoreEntry.Directory);
        (await store.ListChildrenAsync("folder")).Select(e => e.Name).Should().Equal("note.md");

        (await store.DeleteAsync("folder/note.md")).Should().BeTrue();
        (await store.DeleteAsync("folder/note.md")).Should().BeFalse();
        (await store.FileExistsAsync("folder/note.md")).Should().BeFalse();
    }

    [Fact]
    public async Task CreateDirectoryAsync_IsIdempotent()
    {
        AgentFileStore store = NewStore();

        await store.CreateDirectoryAsync("folder");
        await store.CreateDirectoryAsync("folder");

        (await store.ListChildrenAsync("")).Where(e => e.Name == "folder").Should().ContainSingle();
    }

    [Fact]
    public async Task TheStoreIsReadableThroughMafsOwnAbstraction()
    {
        // The whole point of implementing MAF's abstraction rather than inventing our own: code
        // holding only an AgentFileStore reference works against mesh nodes without knowing it.
        AgentFileStore store = NewStore();

        await store.WriteAsync("via-maf.md", "portable");

        (await store.ReadAsync("via-maf.md")).Should().Be("portable");
        (await store.FileExistsAsync("via-maf.md")).Should().BeTrue();
        (await store.ListChildrenAsync("")).Select(e => e.Name).Should().Contain("via-maf.md");
        (await store.SearchAsync("", "portable")).Select(r => r.FileName).Should().Contain("via-maf.md");
    }
}
