using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A node whose path carries a SPACE must be movable — Systemorph/MeshWeaver#7542 (and #6310 before it).
///
/// <para>Measured on a production portal: a rename whose whole purpose was to remove the space from a
/// leaf segment failed in its copy leg with <i>"Copy reached no verdict: '… Rubin' is stored, but the
/// subtree query under the caller did not return it yet"</i>, on every retry. The copy leg reads its
/// source through the caller-scoped query <c>path:{source} scope:subtree</c>, and the path was spliced
/// into the query UNQUOTED: the tokenizer splits on whitespace, so <c>path:P/Lic Rubin</c> became the
/// filter <c>path:P/Lic</c> plus the free-text word <c>Rubin</c> — a query for a different node. Storage,
/// enumerated by raw prefix, still listed the source, so the two surfaces disagreed permanently and
/// the move refused deterministically. The path is now written as a quoted literal
/// (<see cref="QueryParser.QuoteLiteral"/>).</para>
///
/// <para>The control in the same suite: a spaceless move of the same shape succeeds either way, so the
/// whitespace is the only variable.</para>
/// </summary>
public class MoveRenamesOffAWhitespacePathTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    [Theory(Timeout = 90_000)]
    [InlineData("License Email {0} Rubin")]
    [InlineData("plain-{0}")]

    public async Task AMoveOffAPath_CarriesTheSubtree_WhateverWhitespaceTheLeafHolds(string leafPattern)
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/{string.Format(leafPattern, id)}";
        var target = $"{TestPartition}/LicenseEmail{id}";
        await Create(source, "Source", ct);
        await Create($"{source}/child", "Child", ct);

        var moved = await Move(source, target, ct);

        moved.Success.Should().BeTrue($"'{source}' is an ordinary stored node the caller owns; the move answered: {moved.Error}");
        moved.Node!.Path.Should().Be(target);
        (await StoredAt(target, ct)).Should().NotBeNull("the source arrived at the target");
        (await StoredAt($"{target}/child", ct)).Should().NotBeNull("its descendant travelled with it");
        (await StoredAt(source, ct)).Should().BeNull("the delete leg removed the source after a complete copy");
    }

    /// <summary>
    /// A COPY reads its source through the same subtree query. A path with BOTH quote characters (and no
    /// whitespace) has no quoted literal, and the bare term always read it whole, so quoting must not make
    /// it uncopyable (review of #7627). The whitespace case and the plain control run beside it.
    /// </summary>
    [Theory(Timeout = 90_000)]
    [InlineData("it's-\"{0}\"")]
    [InlineData("License Email {0} Rubin")]
    [InlineData("plain-{0}")]
    public async Task ACopyOfAPath_CarriesTheSubtree_WhateverQuotesOrWhitespaceTheLeafHolds(string leafPattern)
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/{string.Format(leafPattern, id)}";
        var target = $"{TestPartition}/Copied{id}";
        await Create(source, "Source", ct);
        await Create($"{source}/child", "Child", ct);

        var copied = await ObserveNodeOperation(new CopyNodeRequest(source, target))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"COPY {source} -> {target}: success={r.Success} error={r.Error}"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        copied.Success.Should().BeTrue($"'{source}' is an ordinary stored node the caller owns; the copy answered: {copied.Error}");
        (await StoredAt(target, ct)).Should().NotBeNull("the source arrived at the target");
        (await StoredAt($"{target}/child", ct)).Should().NotBeNull("its descendant travelled with it");
        (await StoredAt(source, ct)).Should().NotBeNull("a copy leaves the source in place");
    }

    private Task<MoveNodeResponse> Move(string source, string target, CancellationToken ct) =>
        ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"MOVE {source} -> {target}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<MeshNode?> StoredAt(string path, CancellationToken ct) =>
        Storage.Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<MeshNode> Create(string path, string name, CancellationToken ct) =>
        NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                Name = name,
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
}

/// <summary>
/// <see cref="QueryParser.PathLiteral"/>'s contract: a value that reads back as exactly the one
/// path, or a refusal by name — never a query for different nodes (review of #7627). A path with both
/// quote characters is written bare, and a bare value still carries query syntax, so <c>|</c> would
/// make it two alternatives and <c>*</c>, <c>(</c> or <c>)</c> a pattern or a group.
/// </summary>
public class PathLiteralReadsBackAsOnePathTest
{
    [Theory]
    [InlineData("TestData/plain")]
    [InlineData("TestData/License Email Rubin")]
    [InlineData("TestData/it's-\"x\"")]
    public void A_writable_path_reads_back_as_exactly_itself(string path)
    {
        var parsed = new QueryParser().Parse($"path:{QueryParser.PathLiteral(path)} scope:subtree");
        Assert.Equal(path, parsed.Path);
        Assert.True(parsed.Paths is null || (parsed.Paths.Count == 1 && parsed.Paths[0] == path),
            $"one path alternative expected, got [{string.Join(", ", parsed.Paths ?? [])}]");
        Assert.Null(parsed.Filter);
        Assert.True(string.IsNullOrEmpty(parsed.TextSearch));
    }

    [Theory]
    [InlineData("TestData/a'b\"c|TestData/other")]
    [InlineData("TestData/a'b\"c*")]
    [InlineData("TestData/a'b\"c)")]
    [InlineData("TestData/a'b\"(c")]
    [InlineData("TestData/a'b \"c")]
    [InlineData("'TestData/a\"b")]
    public void A_path_no_value_can_hold_is_refused_by_name(string path)
    {
        var ex = Assert.Throws<ArgumentException>(() => QueryParser.PathLiteral(path));
        Assert.Contains(path, ex.Message);
    }
}
