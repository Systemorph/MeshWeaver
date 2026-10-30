using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="MarkdownBodyKeyRepair"/> — the data half of #6639. Fixtures are written straight to the
/// STORAGE adapter, because the rows under repair were written by hand-built JSON that the typed
/// write path would never produce; each seed reads back and asserts the row really is the refused
/// shape (<c>ReadMarkdownContent</c> → Unreadable), so the suite cannot pass on healthy fixtures.
/// </summary>
public class MarkdownBodyKeyRepairTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Budget = TestTimeouts.Convergence;

    private async Task<MeshNode> SeedAsync(string id, string ns, string contentJson)
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var node = new MeshNode(id, ns)
        {
            Name = id,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = JsonDocument.Parse(contentJson).RootElement.Clone(),
        };
        var written = await storage.Write(node, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(Budget).Await(TestContext.Current.CancellationToken);
        written.Should().NotBeNull("the storage adapter must accept the seeded path");
        return await ReadRawAsync(node.Path);
    }

    private async Task<MeshNode> ReadRawAsync(string path)
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var node = await storage.Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(Budget).Await(TestContext.Current.CancellationToken);
        node.Should().NotBeNull($"'{path}' must exist in storage");
        return node!;
    }

    private Task<MarkdownBodyKeyRepairReport> RepairAsync(params string[] roots)
        => MarkdownBodyKeyRepair.Repair(Mesh, roots).Timeout(Budget).Await(TestContext.Current.CancellationToken);

    private Task<MarkdownBodyKeyRepairReport> DetectAsync(params string[] roots)
        => MarkdownBodyKeyRepair.Detect(Mesh, roots).Timeout(Budget).Await(TestContext.Current.CancellationToken);

    private static MarkdownOverviewLayoutArea.MarkdownContentState StateOf(MeshNode node)
        => MarkdownOverviewLayoutArea.ReadMarkdownContent(node).State;

    /// <summary>
    /// The measured shapes: the body under <c>body</c>, <c>markdown</c> or <c>text</c>. Before the
    /// repair the row is Unreadable; after it binds as <see cref="MarkdownContent"/> with the SAME
    /// text, and the declared members it carried survive.
    /// </summary>
    [Theory(Timeout = 120000)]
    [InlineData("body")]
    [InlineData("markdown")]
    [InlineData("text")]
    public async Task A_body_under_a_legacy_key_is_moved_under_content(string key)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var ns = $"BodyKey{key}";
        var seeded = await SeedAsync("Doc", ns,
            $$"""{"$type":"MarkdownContent","{{key}}":"# Title\n\nBody text.","tags":["a","b"]}""");
        StateOf(seeded).Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Unreadable,
            "the fixture must be the refused shape — otherwise this test proves nothing");

        var report = await RepairAsync(ns);

        report.Findings.Should().ContainSingle();
        report.Findings[0].Outcome.Should().Be(MarkdownBodyKeyOutcome.Repaired);
        report.Findings[0].Detail.Should().Be(key);
        report.Findings[0].Error.Should().BeNull();

        var repaired = await ReadRawAsync(seeded.Path);
        var content = repaired.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions);
        content.Should().NotBeNull("the repaired row must bind to MarkdownContent");
        content!.Content.Should().Be("# Title\n\nBody text.", "the body is moved unchanged");
        string.Join(",", content.Tags ?? []).Should().Be("a,b", "declared members are kept");
        StateOf(repaired).Should().NotBe(MarkdownOverviewLayoutArea.MarkdownContentState.Unreadable);

        var second = await RepairAsync(ns);
        second.Findings.Should().BeEmpty("a repaired row no longer matches — the pass is idempotent");
        second.NodesRead.Should().BeGreaterThan(0, "and the clean result was measured on real rows");
    }

    /// <summary>
    /// Negative control: a payload whose body is not mechanically identifiable (two stray members,
    /// or an unknown key) is REPORTED and never written.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task An_ambiguous_payload_is_reported_for_review_and_left_untouched()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        const string ns = "BodyKeyAmbiguous";
        var two = await SeedAsync("Two", ns, """{"$type":"MarkdownContent","body":"x","text":"y"}""");
        var unknown = await SeedAsync("Unknown", ns, """{"$type":"MarkdownContent","summary":"z"}""");

        var report = await RepairAsync(ns);

        report.Findings.Should().HaveCount(2);
        report.Findings.Should().OnlyContain(f => f.Outcome == MarkdownBodyKeyOutcome.NeedsReview);
        report.RepairedCount.Should().Be(0);
        StateOf(await ReadRawAsync(two.Path)).Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Unreadable,
            "an ambiguous row is never rewritten");
        StateOf(await ReadRawAsync(unknown.Path)).Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Unreadable);
    }

    /// <summary>Healthy Markdown rows are not findings, and Detect writes nothing.</summary>
    [Fact(Timeout = 120000)]
    public async Task Detect_reports_without_writing_and_ignores_healthy_rows()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        const string ns = "BodyKeyDetect";
        var broken = await SeedAsync("Broken", ns, """{"$type":"MarkdownContent","body":"keep me"}""");
        await SeedAsync("Healthy", ns, """{"$type":"MarkdownContent","content":"fine"}""");

        var report = await DetectAsync(ns);

        report.Wrote.Should().BeFalse();
        report.Findings.Should().ContainSingle(f => f.Path == broken.Path && f.Outcome == MarkdownBodyKeyOutcome.Repairable);
        report.NodesRead.Should().BeGreaterThanOrEqualTo(2);
        StateOf(await ReadRawAsync(broken.Path)).Should().Be(MarkdownOverviewLayoutArea.MarkdownContentState.Unreadable,
            "Detect must not write");
    }
}
