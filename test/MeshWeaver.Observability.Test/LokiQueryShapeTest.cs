using MeshWeaver.Observability;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// Pins the one property of the Loki query that the aggregator tests cannot see, and that a
/// production deploy caught on day one: <b>the query must not filter to red header lines</b>.
///
/// <para>A .NET console error is a header (<c>fail: Category[0]</c>) plus indented continuation
/// lines — the message, then the stack trace — stored by Loki as separate entries. With a
/// <c>|~ "^(fail|crit):"</c> line filter only the header comes back, so
/// <see cref="BurstAggregator"/> never receives the lines it exists to re-attach: no exception, no
/// top frame, and every fault in a category collapsing to ONE fingerprint. The aggregator's own
/// tests kept passing because they feed it synthetic multi-line input — a path the real query never
/// produced. This test closes that gap from the query side.</para>
/// </summary>
public class LokiQueryShapeTest
{
    [Fact]
    public void NamespaceQuery_SelectsTheNamespace()
        => LokiQuery.ForNamespace("memex").Should().Be("{namespace=\"memex\"}");

    [Fact]
    public void NamespaceQuery_CarriesNoLineFilter()
    {
        var query = LokiQuery.ForNamespace("memex");

        // `|~` / `|=` would drop the continuation lines the grouper needs. If someone re-adds a
        // filter "to cut volume", this fails and the comment above says what it costs.
        query.Should().NotContain("|~");
        query.Should().NotContain("|=");
    }

    [Fact]
    public void AggregatorFindsTheFault_OnlyWhenContinuationLinesSurvive()
    {
        var t0 = new DateTimeOffset(2026, 8, 8, 20, 0, 0, TimeSpan.Zero);
        LogLine L(string line) => new(t0, "memex", "pod-a", line);

        // What Loki returns with NO line filter — the whole burst.
        var whole = new[]
        {
            L("fail: MeshWeaver.Data.MeshDataSource[0]"),
            L("      Update failed for node rbuergi/Foo/7a2f1c4e-9b3d-4a21-8f65-0c1d2e3f4a5b"),
            L("      System.InvalidOperationException: Sequence contains no elements"),
            L("         at MeshWeaver.Data.MeshDataSource.Apply(MeshNode node)"),
        };
        // What the OLD filtered query returned — headers only.
        var headersOnly = new[] { whole[0] };

        var fromWhole = BurstAggregator.Aggregate(whole, maxSamples: 5, maxSampleLength: 2000)[0];
        var fromHeaders = BurstAggregator.Aggregate(headersOnly, maxSamples: 5, maxSampleLength: 2000)[0];

        // With the burst intact the incident identifies the actual defect…
        fromWhole.ExceptionType.Should().Be("System.InvalidOperationException");
        fromWhole.TopFrame.Should().Be("MeshWeaver.Data.MeshDataSource.Apply(MeshNode node)");

        // …and with headers alone it cannot: no exception, no frame, and the "message" degrades to
        // the category itself — which is why every fault in a category shared one fingerprint.
        fromHeaders.ExceptionType.Should().BeNull();
        fromHeaders.TopFrame.Should().BeNull();
        fromHeaders.Fingerprint.Should().NotBe(fromWhole.Fingerprint);
    }
}
