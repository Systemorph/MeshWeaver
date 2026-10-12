using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 THE MCP <c>search</c> ENVELOPE NAMES THE PARTITIONS IT READ ONLY IN PART. MeshWeaver #6564.
///
/// <para>A pre-deploy sweep answered <c>count: 0</c> with the partition in
/// <c>coverage.partitions</c>, while a NodeType under a sub-path the caller held no grant on sat at
/// <c>Error</c>. The provider now reports those partitions
/// (<see cref="QueryResultChange{T}.PartiallyReadPartitions"/>) when the request asks
/// (<see cref="MeshQueryRequest.ReportPartialReads"/>), and <c>search</c> renders them as
/// <c>coverage.partial</c>. The merge tests stop before this serialization layer, so these cases pin
/// the envelope itself: the three verdicts stay distinct, the hint appears only for a non-empty
/// list, and the opt-in reaches the provider (it reports only when asked, so a dropped flag turns
/// the non-empty case null).</para>
/// </summary>
public class SearchReportsPartialReadsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Unassessed = "PartialReadUnassessed";
    private const string Covered = "PartialReadCovered";
    private const string InPart = "PartialReadInPart";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    /// <summary>
    /// Stands in for a provider behind row-level security. It answers only the three marker
    /// queries, reports reading <c>alpha</c> and <c>beta</c>, and states its partial-read verdict
    /// ONLY when the request opted in, so the non-empty case also proves the flag reached it.
    /// </summary>
    private sealed class PartialReportingProvider : IMeshQueryProvider
    {
        public string Name => nameof(PartialReportingProvider);

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        {
            var text = request.Query ?? string.Empty;
            IReadOnlyList<string>? partitions = null;
            IReadOnlyList<string>? partial = null;
            if (text.Contains(Unassessed))
                partitions = ["alpha", "beta"];
            else if (text.Contains(Covered))
                (partitions, partial) = (["alpha", "beta"], request.ReportPartialReads ? [] : null);
            else if (text.Contains(InPart))
                (partitions, partial) = (["alpha", "beta"], request.ReportPartialReads ? ["beta"] : null);

            return Observable.Return(new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Initial,
                Items = Array.Empty<T>(),
                Timestamp = DateTimeOffset.UtcNow,
                Partitions = partitions,
                PartiallyReadPartitions = partial,
            });
        }

        public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request, JsonSerializerOptions options)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<IMeshQueryProvider, PartialReportingProvider>());

    private async Task<(JsonElement Coverage, JsonElement Envelope)> Read(string marker, CancellationToken cancellationToken)
    {
        var answer = await new MeshOperations(Mesh)
            .Search($"nodeType:Markdown name:{marker} {ParsedQuery.CrossPartitionQualifier}", null, 50)
            .FirstAsync().Timeout(Budget).Await(cancellationToken);
        Output.WriteLine(answer);
        var envelope = JsonDocument.Parse(answer).RootElement;
        var coverage = envelope.GetProperty("coverage");
        coverage.GetProperty("partitions").EnumerateArray().Select(p => p.GetString())
            .Should().Equal(new[] { "alpha", "beta" }, "the provider's denominator reaches the envelope");
        return (coverage, envelope);
    }

    /// <summary>Not assessed: <c>partial</c> is JSON null (unknown), never an empty list.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AnUnassessedVerdict_IsNull_NotEmpty()
    {
        var (coverage, envelope) = await Read(Unassessed, TestContext.Current.CancellationToken);

        coverage.GetProperty("partial").ValueKind.Should().Be(JsonValueKind.Null,
            "a provider that did not assess must never read as 'none partial'");
        envelope.TryGetProperty("partialHint", out _).Should().BeFalse();
    }

    /// <summary>Assessed and covered: an empty list, distinct from null, and no hint.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AnAssessedCoveredVerdict_IsEmpty_AndCarriesNoHint()
    {
        var (coverage, envelope) = await Read(Covered, TestContext.Current.CancellationToken);

        coverage.GetProperty("partial").ValueKind.Should().Be(JsonValueKind.Array,
            "search opted in, so the provider assessed; an empty list is the only passing verdict");
        coverage.GetProperty("partial").GetArrayLength().Should().Be(0);
        envelope.TryGetProperty("partialHint", out _).Should().BeFalse("nothing was read in part");
    }

    /// <summary>
    /// Read in part: the partition is named and the hint says the count is a floor there. The
    /// provider reports only when asked, so this also proves <c>search</c> set the opt-in.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task APartialVerdict_NamesThePartition_AndCarriesTheHint()
    {
        var (coverage, envelope) = await Read(InPart, TestContext.Current.CancellationToken);

        coverage.GetProperty("partial").EnumerateArray().Select(p => p.GetString())
            .Should().Equal(new[] { "beta" }, "search sets ReportPartialReads, and the verdict survives into the envelope");
        envelope.GetProperty("partialHint").GetString().Should()
            .Contain("coverage.partial").And.Contain("floor");
    }
}
