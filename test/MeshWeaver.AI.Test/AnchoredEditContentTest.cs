using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Coverage for the ATOMIC anchored edit (#1716): <c>edit_content</c>'s check-and-replace runs
/// INSIDE <c>GetMeshNodeStream(path).Update(...)</c> against the LIVE node, not against a
/// pre-read snapshot, and its success string is gated on the edit provably landing.
///
/// <para>Unit half: pins the pure primitive <see cref="MeshOperations.ApplyAnchoredEdit(MeshNode,
/// string, string, bool)"/> — text extraction per content shape (Markdown / Code / raw string),
/// the distinct 0-match and ambiguous-match exception types, replaceAll semantics, and the
/// markdown re-render — plus <c>CollaborationPlugin.Splice</c>'s delegation to the same
/// primitive.</para>
///
/// <para>Integration half: the tool contract strings are unchanged (the exceptions thrown inside
/// the write lambda map back to the exact pre-#1716 error messages), and two CONCURRENT
/// non-overlapping edits both land — the clobber case the snapshot-based implementation risked.
/// A true mid-flight interleaving (pausing EditContent between its pre-flight fetch and its
/// write) has no deterministic seam in the harness; the concurrency property is pinned via the
/// serialised per-path write queue instead.</para>
/// </summary>
public class AnchoredEditContentTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;
    private MeshOperations Ops => new(Mesh);
    private string Json(MeshNode node) => JsonSerializer.Serialize(node, Mesh.JsonSerializerOptions);

    private Task<string> Run(IObservable<string> op) =>
        op.FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static MeshNode Markdown(string id, string content, string ns = TestPartition) =>
        new(id, ns) { Name = id, NodeType = "Markdown", Content = new MarkdownContent { Content = content } };

    // ── ApplyAnchoredEdit: the pure primitive ────────────────────────────────────────────────

    [Fact]
    public void ApplyAnchoredEdit_Markdown_ReplacesAndRerenders_PreservingMetadata()
    {
        var node = Markdown("m1", "# Title\n\nalpha beta") with
        {
            Content = new MarkdownContent
            {
                Content = "# Title\n\nalpha beta",
                Abstract = "keep-me",
                Authors = new[] { "author-1" }
            }
        };

        var result = MeshOperations.ApplyAnchoredEdit(node, "beta", "gamma", replaceAll: false, out var count);

        count.Should().Be(1);
        var md = result.Content.Should().BeOfType<MarkdownContent>().Subject;
        md.Content.Should().Be("# Title\n\nalpha gamma");
        md.Abstract.Should().Be("keep-me", "the rebuild must preserve front-matter metadata");
        md.Authors.Should().ContainSingle().Which.Should().Be("author-1");
        md.PrerenderedHtml.Should().NotBeNullOrEmpty("the markdown re-render must run on edit")
            .And.Contain("gamma", "the re-rendered HTML must reflect the NEW text");
        result.PreRenderedHtml.Should().Be(md.PrerenderedHtml,
            "the node-level prerendered HTML must be rebuilt alongside the content's");
    }

    [Fact]
    public void ApplyAnchoredEdit_Code_ReplacesCode_PreservingConfiguration()
    {
        var node = new MeshNode("c1", TestPartition)
        {
            Name = "c1",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = "var x = 1;", IsExecutable = true }
        };

        var result = MeshOperations.ApplyAnchoredEdit(node, "1", "2", replaceAll: false, out var count);

        count.Should().Be(1);
        var code = result.Content.Should().BeOfType<CodeConfiguration>().Subject;
        code.Code.Should().Be("var x = 2;");
        code.IsExecutable.Should().BeTrue("the rebuild must not reset the configuration's other fields");
    }

    [Fact]
    public void ApplyAnchoredEdit_RawString_Replaces()
    {
        var node = new MeshNode("s1", TestPartition) { Name = "s1", NodeType = "Markdown", Content = "hello world" };

        var result = MeshOperations.ApplyAnchoredEdit(node, "world", "mesh", replaceAll: false, out var count);

        count.Should().Be(1);
        result.Content.Should().Be("hello mesh");
    }

    [Fact]
    public void ApplyAnchoredEdit_ZeroMatches_ThrowsAnchorNotFound_WithContentLength()
    {
        var node = Markdown("m2", "alpha beta");

        Action act = () => MeshOperations.ApplyAnchoredEdit(node, "GONE", "x", replaceAll: false);

        act.Should().Throw<AnchorNotFoundException>()
            .Which.ContentLength.Should().Be("alpha beta".Length,
                "the error must name the live content's length so the agent knows what was checked");
    }

    [Fact]
    public void ApplyAnchoredEdit_AmbiguousMatch_ThrowsAmbiguousAnchor_WithOccurrenceCount()
    {
        var node = Markdown("m3", "dup one dup two dup");

        Action act = () => MeshOperations.ApplyAnchoredEdit(node, "dup", "x", replaceAll: false);

        act.Should().Throw<AmbiguousAnchorException>()
            .Which.OccurrenceCount.Should().Be(3);
    }

    [Fact]
    public void ApplyAnchoredEdit_ReplaceAll_ReplacesEveryOccurrence()
    {
        var node = Markdown("m4", "dup one dup two dup");

        var result = MeshOperations.ApplyAnchoredEdit(node, "dup", "DUP", replaceAll: true, out var count);

        count.Should().Be(3);
        ((MarkdownContent)result.Content!).Content.Should().Be("DUP one DUP two DUP");
    }

    [Fact]
    public void ApplyAnchoredEdit_NonTextContent_ThrowsInvalidOperation()
    {
        var node = new MeshNode("n1", TestPartition) { Name = "n1", NodeType = "Markdown", Content = 42 };

        Action act = () => MeshOperations.ApplyAnchoredEdit(node, "a", "b", replaceAll: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not editable text*");
    }

    // ── Splice delegates to the same primitive ───────────────────────────────────────────────

    [Fact]
    public void Splice_SingleOccurrence_Replaces()
        => CollaborationPlugin.Splice("alpha beta gamma", "beta", "BETA")
            .Should().Be("alpha BETA gamma");

    [Fact]
    public void Splice_EmptyOriginal_Prepends()
        => CollaborationPlugin.Splice("body", "", "head ")
            .Should().Be("head body");

    [Fact]
    public void Splice_MissingText_ThrowsWithLegacyMessage()
    {
        Action act = () => CollaborationPlugin.Splice("alpha", "GONE", "x");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*is not present in the document*",
                "SuggestEdit's user-visible not-found message must not change");
    }

    [Fact]
    public void Splice_AmbiguousText_RefusesInsteadOfSplicingFirst()
    {
        Action act = () => CollaborationPlugin.Splice("dup and dup", "dup", "x");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*occurs 2 times*",
                "an ambiguous anchor must refuse — splicing an arbitrary occurrence corrupts the document");
    }

    // ── IsEditApplied: the landed-write verification predicate ───────────────────────────────

    [Fact]
    public void IsEditApplied_LiveMatchesExpectedText_IsTrue()
        => MeshOperations.IsEditApplied(Markdown("v1", "abc NEW def"), "OLD", "NEW", "abc NEW def")
            .Should().BeTrue();

    [Fact]
    public void IsEditApplied_ReplacementAbsent_IsFalse()
        => MeshOperations.IsEditApplied(Markdown("v2", "abc OLD def"), "OLD", "NEW", "abc NEW def")
            .Should().BeFalse();

    /// <summary>
    /// Regression pin for the Copilot finding on #1717: when newText ALREADY occurred elsewhere
    /// in the document before the edit, a bare "contains newText" probe reports a LOST write as
    /// landed. The structural predicate must refuse: the live text still holds the anchor and
    /// only the pre-existing newText occurrence, not the occurrence the edit would have added.
    /// </summary>
    [Fact]
    public void IsEditApplied_NewTextPreexistingElsewhere_LostWrite_IsFalse()
        => MeshOperations.IsEditApplied(
                Markdown("v3", "x NEW y OLD z"), "OLD", "NEW", expectedText: "x NEW y NEW z")
            .Should().BeFalse("a pre-existing newText occurrence must not satisfy the gate for a write that never landed");

    [Fact]
    public void IsEditApplied_ConcurrentLaterEdit_StillRecognizesOurLandedEdit()
        // Our edit (OLD→NEW) landed, then a concurrent writer appended text — the mirror never
        // shows exactly our expected text, but the occurrence structure proves the edit is in.
        => MeshOperations.IsEditApplied(
                Markdown("v4", "x NEW y NEW z PLUS"), "OLD", "NEW", expectedText: "x NEW y NEW z")
            .Should().BeTrue("a later concurrent edit on top of ours must not read as a conflict");

    [Fact]
    public void IsEditApplied_ShrinkingReplace_LostWrite_IsFalse()
        // newText is a substring of oldText ("cats"→"cat"): the live text trivially contains
        // newText, so only the anchor-count bound can tell a lost write from a landed one.
        => MeshOperations.IsEditApplied(Markdown("v5", "cats"), "cats", "cat", expectedText: "cat")
            .Should().BeFalse("the anchor still being present proves the write did not land");

    [Fact]
    public void IsEditApplied_Deletion_TrueOnlyWhenAnchorGone()
    {
        MeshOperations.IsEditApplied(Markdown("v6", "abc def"), "OLD", "", "abc def").Should().BeTrue();
        MeshOperations.IsEditApplied(Markdown("v7", "abc OLD def"), "OLD", "", "abc def").Should().BeFalse();
    }

    // ── Integration: the tool contract through the real write pipeline ───────────────────────

    [Fact(Timeout = 60000)]
    public async Task EditContent_ReplacesAnchoredText_AndReadBackSeesIt()
    {
        var id = Unique("edit");
        var path = $"{TestPartition}/{id}";
        (await Run(Ops.Create(Json(Markdown(id, "# Doc\n\nalpha beta gamma"))))).Should().StartWith("Created");

        var result = await Run(Ops.EditContent(path, "beta", "BETA"));

        result.Should().Be($"Edited: {path} (1 replacement)");
        (await Run(Ops.Get(path))).Should().Contain("alpha BETA gamma",
            because: "the read after the gated success must see the applied edit");
    }

    [Fact(Timeout = 60000)]
    public async Task EditContent_AnchorMissing_ReturnsContractErrorString()
    {
        var id = Unique("miss");
        var path = $"{TestPartition}/{id}";
        var content = "# Doc\n\nalpha";
        await Run(Ops.Create(Json(Markdown(id, content))));

        var result = await Run(Ops.EditContent(path, "NOTTHERE", "x"));

        // The exact pre-#1716 contract string, now produced from the exception thrown
        // inside the write lambda.
        result.Should().Be(
            $"Error: the text to replace was not found in {path}. Get the node and copy the " +
            "exact text — including whitespace and line breaks — then retry. " +
            $"(Current content is {content.Length} chars.)");
    }

    [Fact(Timeout = 60000)]
    public async Task EditContent_AmbiguousAnchor_ReturnsContractErrorString()
    {
        var id = Unique("ambi");
        var path = $"{TestPartition}/{id}";
        await Run(Ops.Create(Json(Markdown(id, "dup and dup"))));

        var result = await Run(Ops.EditContent(path, "dup", "x"));

        result.Should().Be(
            $"Error: the text to replace occurs 2 times in {path}. Include more " +
            "surrounding context to make the match unique, or set replaceAll=true to change every occurrence.");
    }

    [Fact(Timeout = 60000)]
    public async Task EditContent_ReplaceAll_ReplacesEveryOccurrence()
    {
        var id = Unique("rall");
        var path = $"{TestPartition}/{id}";
        await Run(Ops.Create(Json(Markdown(id, "dup and dup"))));

        var result = await Run(Ops.EditContent(path, "dup", "DUP", replaceAll: true));

        result.Should().Be($"Edited: {path} (2 replacements)");
        (await Run(Ops.Get(path))).Should().Contain("DUP and DUP");
    }

    [Fact(Timeout = 60000)]
    public async Task EditContent_Deletion_RemovesAnchor()
    {
        var id = Unique("del");
        var path = $"{TestPartition}/{id}";
        await Run(Ops.Create(Json(Markdown(id, "keep REMOVE keep"))));

        var result = await Run(Ops.EditContent(path, " REMOVE", ""));

        result.Should().Be($"Edited: {path} (1 replacement)");
        (await Run(Ops.Get(path))).Should().NotContain("REMOVE");
    }

    [Fact(Timeout = 60000)]
    public async Task EditContent_CodeNode_ReplacesSource()
    {
        var id = Unique("code");
        var path = $"{TestPartition}/{id}";
        var node = new MeshNode(id, TestPartition)
        {
            Name = id,
            NodeType = "Code",
            Content = new CodeConfiguration { Code = "Console.WriteLine(\"before\");" }
        };
        (await Run(Ops.Create(Json(node)))).Should().StartWith("Created");

        var result = await Run(Ops.EditContent(path, "before", "after"));

        result.Should().Be($"Edited: {path} (1 replacement)");
        (await Run(Ops.Get(path))).Should().Contain("after");
    }

    /// <summary>
    /// The clobber case #1716 exists for: two writers edit DIFFERENT parts of the same document
    /// concurrently. The snapshot-based implementation computed both whole contents off the same
    /// base, so the second write could silently revert the first edit. With the check-and-replace
    /// inside the write lambda, the per-path serial write queue runs the second lambda against
    /// the state the first already produced — BOTH edits must always land.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task EditContent_ConcurrentNonOverlappingEdits_BothLand()
    {
        var id = Unique("race");
        var path = $"{TestPartition}/{id}";
        await Run(Ops.Create(Json(Markdown(id, "alpha beta gamma"))));

        var first = Run(Ops.EditContent(path, "alpha", "ALPHA"));
        var second = Run(Ops.EditContent(path, "gamma", "GAMMA"));
        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(r => r.StartsWith("Edited:"),
            "both anchors stay present regardless of apply order, so neither edit may fail: {0}",
            string.Join(" | ", results));
        var got = await Run(Ops.Get(path));
        got.Should().Contain("ALPHA", because: "the first edit must not be clobbered by the second");
        got.Should().Contain("GAMMA", because: "the second edit must land too");
    }
}
