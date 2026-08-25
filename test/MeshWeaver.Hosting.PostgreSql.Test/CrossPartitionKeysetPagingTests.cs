using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// 🚨 MESH-WIDE KEYSET PAGING WAS BROKEN IN BOTH HALVES — ONE LOUDLY, ONE SILENTLY (issue #2186).
///
/// <para>A cross-partition query is served by <c>public.search_across_schemas</c>, a UNION ALL over
/// every partition schema. Its branches projected 13 columns and the ORDER BY is applied to the
/// OUTER select over that union, so:</para>
/// <list type="bullet">
///   <item><c>sort:path</c> errored <c>42703 column "path" does not exist</c> — the union simply had
///     no such column, whatever the branches' underlying tables carry;</item>
///   <item><c>path:&gt;"cursor"</c> matched NOTHING and said nothing: the parser folded every
///     <c>path:</c> token into the path ANCHOR, so the operator was discarded and the query became an
///     exact lookup at a path nothing lives at.</item>
/// </list>
///
/// <para>The silent half is the dangerous one: a walk built on the standard
/// <c>sort:path</c> + <c>path:&gt;{cursor}</c> pair returns page one, gets an empty page two, and
/// reports success. It hit production twice in MeshWeaver.Plugins' <c>RefreshAppTiles</c> sweep,
/// whose keyset user-walk had therefore never once executed mesh-wide.</para>
///
/// <para>This walks EVERY seeded node across two partitions, page by page, and asserts the walk is
/// complete — plus the negative direction, that a cursor past the last row yields an empty page (so
/// "the walk terminates" cannot be confused with "the filter matches nothing").</para>
/// </summary>
[Collection("PostgreSql")]
public class CrossPartitionKeysetPagingTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    private readonly PostgreSqlFixture _fixture = fixture;
    private readonly JsonSerializerOptions _options = new();

    /// <summary>A node type nothing else in the fixture writes, so the fan-out's result set is
    /// exactly what this test seeded.</summary>
    private const string ProbeNodeType = "KeysetPagingProbe";

    private const int RowsPerPartition = 7;
    private const int PageSize = 3;

    /// <summary>The two seeded partitions. The stored function resolves its own schema list from
    /// <c>public.searchable_schemas</c>; the parameter is part of the shared signature.</summary>
    private static readonly string[] Schemas = ["kalpha", "kbeta"];

    [Fact(Timeout = 180000)]
    public async Task SortByPath_Orders_AndPathCursor_PagesThroughEveryPartition()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var expected = new List<string>();
        expected.AddRange(await SeedAsync("kalpha", "KAlpha", partitionDef, ct));
        expected.AddRange(await SeedAsync("kbeta", "KBeta", partitionDef, ct));
        expected.Sort(StringComparer.Ordinal);

        var cross = new PostgreSqlCrossSchemaQueryProvider(_fixture.DataSource) { SyncTtl = TimeSpan.Zero };
        await cross.SyncSearchableSchemasAsync(ct);
        var parser = new QueryParser();

        // 1. sort:path — the LOUD half. This threw 42703 before the union projected `path`.
        var sorted = await Query(cross, parser, $"nodeType:{ProbeNodeType} sort:path limit:100", ct);
        sorted.Should().Equal(expected,
            "sort:path must order the cross-partition union — it errored 42703 'column \"path\" does "
            + "not exist' because the UNION branches never projected the column (#2186)");

        // 2. The full keyset walk — the SILENT half. Every page after the first used to come back
        //    EMPTY, so a caller saw page one and a clean termination.
        var walked = new List<string>();
        var cursor = "";
        for (var page = 0; page < 20; page++)
        {
            var query = cursor.Length == 0
                ? $"nodeType:{ProbeNodeType} sort:path limit:{PageSize}"
                : $"nodeType:{ProbeNodeType} sort:path path:>\"{cursor}\" limit:{PageSize}";
            var rows = await Query(cross, parser, query, ct);
            output.WriteLine($"page {page} after '{cursor}': {rows.Count} row(s)");
            if (rows.Count == 0)
                break;
            walked.AddRange(rows);
            cursor = rows[^1];
        }

        walked.Should().Equal(expected,
            "the keyset pair (sort:path + path:>cursor) must walk every partition's rows exactly "
            + "once; a discarded comparison operator turned page two into an exact lookup at a path "
            + "nothing lives at and returned zero rows — silently (#2186)");

        // 3. The negative direction: a cursor PAST the last row yields an empty page. Without this
        //    the walk's termination is indistinguishable from the bug it is here to catch.
        var past = await Query(
            cross, parser, $"nodeType:{ProbeNodeType} sort:path path:>\"{expected[^1]}\" limit:{PageSize}", ct);
        past.Should().BeEmpty("nothing sorts after the last path");

        // …and a cursor BEFORE the first row yields the whole set — the filter really is a
        // comparison against the stored path, not a match-nothing.
        var all = await Query(
            cross, parser, $"nodeType:{ProbeNodeType} sort:path path:>\"\" limit:100", ct);
        all.Should().Equal(expected);
    }

    private async Task<List<string>> Query(
        PostgreSqlCrossSchemaQueryProvider cross, QueryParser parser, string query, CancellationToken ct)
    {
        var rows = new List<string>();
        await foreach (var node in cross
                           .QueryAcrossSchemasAsync(parser.Parse(query), _options, Schemas, ct: ct)
                           .WithCancellation(ct))
            rows.Add(node.Path);
        return rows;
    }

    private async Task<IReadOnlyList<string>> SeedAsync(
        string schema, string ns, PartitionDefinition partitionDef, CancellationToken ct)
    {
        var (_, adapter) = await _fixture.CreateSchemaAdapterAsync(
            schema, partitionDef with { Namespace = ns, Schema = schema });

        var paths = new List<string>();
        for (var i = 0; i < RowsPerPartition; i++)
        {
            var node = new MeshNode($"Probe{i:D2}", ns)
            {
                Name = $"Probe {i:D2}",
                NodeType = ProbeNodeType,
                State = MeshNodeState.Active
            };
            await adapter.WriteAsync(node, _options, ct);
            paths.Add(node.Path);
        }
        return paths;
    }
}
