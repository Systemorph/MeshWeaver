using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins the cross-schema fan-out's TIMING instrumentation.
///
/// <para>Why this exists: <c>search_across_schemas</c> builds a <c>UNION ALL</c> over every row of
/// <c>public.searchable_schemas</c>, so a query with no <c>path:</c>/<c>namespace:</c> anchor scans
/// EVERY partition — every plugin and every user partition. Its cost therefore grows with the number
/// of users, and it degrades over months with no code change. Before this, the provider logged the
/// WHERE clause but never a duration, so a slow query was indistinguishable from a bad filter and
/// the fan-out width was invisible.</para>
///
/// <para>The assertions are about what an operator can ACT on: a slow fan-out must be visible at
/// default log level (Warning, not Debug), must state how many schemas it spanned, and must name the
/// fix. A fast one must stay quiet.</para>
/// </summary>
public class CrossSchemaFanOutTimingTests
{
    private sealed record Entry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<PostgreSqlCrossSchemaQueryProvider>
    {
        public readonly List<Entry> Entries = new();
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(level, formatter(state, ex)));
    }

    /// <summary>
    /// The provider only needs a data source to be CONSTRUCTED; the timing helper touches neither it
    /// nor the database, which is the point of extracting it — the log decision is unit-testable
    /// without Postgres.
    /// </summary>
    private static (PostgreSqlCrossSchemaQueryProvider Provider, CapturingLogger Log) Subject(int schemas)
    {
        var logger = new CapturingLogger();
        var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        var provider = new PostgreSqlCrossSchemaQueryProvider(dataSource, logger)
        {
            _cachedSchemaCount = schemas,
        };
        return (provider, logger);
    }

    [Fact]
    public void SlowFanOut_WarnsAtDefaultLevel()
    {
        var (provider, log) = Subject(schemas: 312);

        provider.LogFanOutTiming("search_across_schemas",
            totalMs: PostgreSqlCrossSchemaQueryProvider.SlowFanOutMs, firstRowMs: 900, rows: 15, limit: 15);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("SLOW", entry.Message);
    }

    [Fact]
    public void SlowFanOut_ReportsTheFanOutWidth()
    {
        // The width is the whole diagnosis: "1200ms" alone reads as a database problem, "1200ms
        // across 312 schemas" tells the operator it is an unanchored query.
        var (provider, log) = Subject(schemas: 312);

        provider.LogFanOutTiming("search_across_schemas", totalMs: 4200, firstRowMs: 4100, rows: 3, limit: 50);

        var message = Assert.Single(log.Entries).Message;
        Assert.Contains("312", message);
        Assert.Contains("4200", message);
        Assert.Contains("3/50", message);
    }

    [Fact]
    public void SlowFanOut_NamesTheFix()
    {
        // A warning that does not say what to do gets muted rather than fixed.
        var (provider, log) = Subject(schemas: 200);

        provider.LogFanOutTiming("search_across_schemas", totalMs: 9000, firstRowMs: 8000, rows: 1, limit: 10);

        var message = Assert.Single(log.Entries).Message;
        Assert.Contains("path:", message);
        Assert.Contains("namespace:", message);
    }

    [Fact]
    public void FastFanOut_StaysAtDebug()
    {
        // Every page render fans out; warning on the normal case would drown the slow one.
        var (provider, log) = Subject(schemas: 12);

        provider.LogFanOutTiming("search_across_schemas", totalMs: 40, firstRowMs: 30, rows: 8, limit: 50);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.DoesNotContain("SLOW", entry.Message);
    }

    [Fact]
    public void TheThresholdIsInclusive_SoTheBoundaryCannotFallThroughSilently()
    {
        var (slowProvider, slowLog) = Subject(schemas: 5);
        slowProvider.LogFanOutTiming("q", PostgreSqlCrossSchemaQueryProvider.SlowFanOutMs, 1, 1, 1);
        Assert.Equal(LogLevel.Warning, Assert.Single(slowLog.Entries).Level);

        var (fastProvider, fastLog) = Subject(schemas: 5);
        fastProvider.LogFanOutTiming("q", PostgreSqlCrossSchemaQueryProvider.SlowFanOutMs - 1, 1, 1, 1);
        Assert.Equal(LogLevel.Debug, Assert.Single(fastLog.Entries).Level);
    }

    [Fact]
    public void TimeToFirstRow_IsReported_SoScanTimeIsSeparableFromStreamingTime()
    {
        // Total alone cannot distinguish "the scan was slow" from "the caller consumed slowly".
        var (provider, log) = Subject(schemas: 40);

        provider.LogFanOutTiming("search_across_schemas", totalMs: 5000, firstRowMs: 120, rows: 200, limit: 200);

        var message = Assert.Single(log.Entries).Message;
        Assert.Contains("120", message);
        Assert.Contains("5000", message);
    }
}
