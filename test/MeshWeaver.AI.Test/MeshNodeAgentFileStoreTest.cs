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
    public async Task SearchAsync_TheOneShotMafSignature_IsRefused()
    {
        var store = NewStore();

        var search = () => store.SearchAsync("", "anything");

        (await search.Should().ThrowAsync<NotSupportedException>(
                "a mesh content search is a live query; collapsing it to a single snapshot is the "
                + "stale-read shape CQRS forbids"))
            .WithMessage("*Search*");
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
    }
}
