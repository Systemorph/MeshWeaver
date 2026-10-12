using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;   // MeshOperations - its namespace is a frozen binary contract (#2370)
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// THE SEARCH ENVELOPE STATES WHICH PARTITIONS THE CALLER READS ONLY IN PART. MeshWeaver #6564.
///
/// <para><b>What production showed</b> (memex.meshweaver.cloud, 2026-10-12): the pre-deploy NodeType
/// sweep answered <c>count: 0</c> with <c>coverage.partitions</c> listing <c>meshweaver</c>, while a
/// broken NodeType in that partition was unreadable to the caller (row-level security hid its
/// subtree). <c>coverage.partitions</c> is the partitions a provider READ FROM; it never said that
/// the caller reads one of them only in part.</para>
///
/// <para><b>What this pins.</b> A query provider that reports both the partitions it read and the
/// subset read only in part is registered beside the in-memory store. The envelope must carry that
/// subset as <c>coverage.partial</c> - and must tell three states apart: a non-empty list (read in
/// part), an EMPTY list (every partition read in full) and <c>null</c> (no provider reported it, so it
/// is unknown, never read as in full). The Postgres provider that fills the field is the
/// MeshWeaver.Plugins half; its own test lives there.</para>
/// </summary>
public class SearchCoveragePartialTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "PartlyReadPartition";
    private const string OtherPartition = "FullyReadPartition";
    private const string PartialMarker = "PartialCoverageMarker";
    private const string FullMarker = "FullCoverageMarker";
    private const string UnreportedMarker = "UnreportedCoverageMarker";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services =>
        {
            services.AddSingleton<IMeshQueryProvider>(new CoverageReportingProvider());
            return services;
        });

    private async Task<JsonElement> DeclaredCoverage(string marker, CancellationToken cancellationToken)
    {
        var answer = await new MeshOperations(Mesh)
            .Search($"nodeType:Markdown name:{marker} {ParsedQuery.CrossPartitionQualifier}")
            .FirstAsync().Timeout(Budget).Await(cancellationToken);

        Output.WriteLine(answer);
        answer.Should().NotStartWith("Error:");
        var coverage = JsonDocument.Parse(answer).RootElement.GetProperty("coverage");
        coverage.GetProperty("scope").GetString().Should().Be("declared");
        return coverage;
    }

    /// <summary>
    /// The sweep shape of #6564: a partition the caller is a member of but reads only in part is named
    /// in <c>coverage.partial</c>, and stays in <c>coverage.partitions</c> - the denominator is
    /// unchanged, it is QUALIFIED.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task APartitionReadOnlyInPart_IsNamedInCoveragePartial_AndStaysInThePartitionsList()
    {
        var coverage = await DeclaredCoverage(PartialMarker, TestContext.Current.CancellationToken);

        coverage.GetProperty("partial").EnumerateArray().Select(p => p.GetString())
            .Should().Equal(new[] { Partition },
                "the provider read this partition only in part and the envelope states it - a zero over it is a zero over the part the caller can see");
        coverage.GetProperty("partitions").EnumerateArray().Select(p => p.GetString())
            .Should().Equal(new[] { Partition, OtherPartition },
                "a partition read in part is still one the query was read from");
    }

    /// <summary>
    /// The control that keeps the field meaningful: providers that read every partition in full say so
    /// with an EMPTY list. A statement, not silence.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task EveryPartitionReadInFull_IsAnEmptyPartialList_NotNull()
    {
        var coverage = await DeclaredCoverage(FullMarker, TestContext.Current.CancellationToken);

        var partial = coverage.GetProperty("partial");
        partial.ValueKind.Should().Be(JsonValueKind.Array,
            "a provider that reported full reads said so - an empty list is a statement, not an absent value");
        partial.GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// The negative control: when no provider reports how fully it read, the envelope says null -
    /// unknown - and never an empty list, which would read as every partition read in full.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AnUnreportedPartialSet_IsNull_NeverEmpty()
    {
        var coverage = await DeclaredCoverage(UnreportedMarker, TestContext.Current.CancellationToken);

        coverage.GetProperty("partial").ValueKind.Should().Be(JsonValueKind.Null,
            "nobody reported how fully the partitions were read, and unknown must never read as in full");
        coverage.GetProperty("partitions").ValueKind.Should().Be(JsonValueKind.Null,
            "nobody reported the partitions either");
    }

    /// <summary>
    /// A query provider that answers every query with an empty Initial and stays open, and that REPORTS
    /// coverage only for the queries carrying one of this test's markers - so the rest of the mesh is
    /// untouched.
    /// </summary>
    private sealed class CoverageReportingProvider : IMeshQueryProvider
    {
        public string Name => "Test.CoverageReporting";

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        {
            var query = string.Join(" | ", request.EffectiveQueries);
            IReadOnlyList<string>? partitions = null;
            IReadOnlyList<string>? partial = null;
            if (query.Contains(PartialMarker, StringComparison.Ordinal))
            {
                partitions = [Partition, OtherPartition];
                partial = [Partition];
            }
            else if (query.Contains(FullMarker, StringComparison.Ordinal))
            {
                partitions = [Partition, OtherPartition];
                partial = [];
            }

            return Observable.Return(new QueryResultChange<T>
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = Array.Empty<T>(),
                    Timestamp = DateTimeOffset.UtcNow,
                    Partitions = partitions,
                    PartiallyReadablePartitions = partial,
                })
                .Concat(Observable.Never<QueryResultChange<T>>());
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
}
