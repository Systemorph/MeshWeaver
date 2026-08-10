using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Pins #1120: when wiring a <c>_GitSync</c> makes a space system-owned, the
/// <see cref="SystemOwnedAccessRetractionHandler"/> sweep must treat the last-admin invariant
/// (<c>SpaceAdminInvariantValidator</c>: "a space must always have at least one admin") as an
/// EXPECTED boundary, not a fault. Before the fix the handler attempted the delete anyway, the
/// delete pipeline correctly refused it, and the guaranteed rejection surfaced as a fail-level
/// "[SystemOwned] FAILED to retract" + <see cref="UnauthorizedAccessException"/> — the log
/// incident that opened the issue.
///
/// <para>The pinned semantics: when no grant outside the doomed set still confers Admin, the
/// sweep SPARES exactly one doomed admin grant (deterministically the earliest-created — the
/// space's original owner), says so at Warning naming the spared path and the reason, attempts
/// no delete the guard is guaranteed to refuse, logs nothing at Error, and reaches its normal
/// terminal state. Every other doomed grant — including a NON-last admin — is still retracted,
/// and a surviving System-identity admin assignment releases the whole doomed set.</para>
/// </summary>
public class SystemOwnedRetractionLastAdminTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    private const string LastAdminSpace = "LastAdminSpace";
    private const string CoAdminSpace = "CoAdminSpace";
    private const string SystemAdminSpace = "SystemAdminSpace";

    /// <summary>Second administrator on <see cref="CoAdminSpace"/> — granted AFTER the space
    /// create, so the creator's grant stays the earliest-created admin the sweep
    /// deterministically spares.</summary>
    private const string CoAdmin = "co-admin";

    private const string RepoUrl = "https://github.com/test/system-owned-last-admin";

    private readonly RetractionLogCapture capture = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(s =>
            {
                s.AddSingleton<ILoggerProvider>(capture);
                return s;
            });

    /// <summary>Creates the space (the DevLogin creator receives the Admin grant via the Space
    /// post-creation handler), PERSISTS any extra grants (the sweep lists the storage adapter, so
    /// a builder-seeded config node would be invisible to it — grants must be real nodes, as in
    /// production), and wires the GitHub sync, which fires the system-owned sweep.</summary>
    private async Task ProvisionSyncedSpace(string space, params MeshNode[] extraGrants)
    {
        await Connect();
        await CreateSpace(space);
        foreach (var grant in extraGrants)
            await NodeFactory.CreateNode(grant).Timeout(30.Seconds()).ToTask();
        await Sync.SaveConfig(space, RepoUrl, "main", null, true, true)
            .Timeout(30.Seconds()).ToTask();
    }

    [Fact(Timeout = 120000)]
    public async Task LastAdmin_IsSparedWithWarning_NotAnError()
    {
        await ProvisionSyncedSpace(LastAdminSpace);

        // The sweep reaches its graceful terminal state: a Warning names the spared grant and the
        // reason. Before the fix no such record exists — the handler attempted the delete, the
        // last-admin guard refused it, and the outcome was a fail-level "FAILED to retract".
        await WaitForRetractionRecord(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("last administrator")
            && e.Message.Contains($"{LastAdminSpace}/_Access/{UserId}_Access"));

        // The grant is NOT removed — the invariant outranks the sweep; the space keeps its admin.
        var kept = await ReadNode($"{LastAdminSpace}/_Access/{UserId}_Access")
            .Timeout(10.Seconds()).ToTask();
        Assert.NotNull(kept);

        // An expected condition, never a fault: nothing from the handler at Error or above.
        Assert.DoesNotContain(capture.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact(Timeout = 120000)]
    public async Task NonLastAdmin_IsStillRetracted_EarliestAdminSpared()
    {
        // Two admins: the space creator (earliest-created) and a later co-admin. The sweep
        // retracts the co-admin's grant — a NON-last admin — and spares the earliest one
        // (the space's original owner).
        await ProvisionSyncedSpace(CoAdminSpace,
            AssignmentNodeFactory.UserRole(CoAdmin, "Admin", CoAdminSpace));

        await WaitForAbsent($"{CoAdminSpace}/_Access/{CoAdmin}_Access");

        var kept = await ReadNode($"{CoAdminSpace}/_Access/{UserId}_Access")
            .Timeout(10.Seconds()).ToTask();
        Assert.NotNull(kept);

        Assert.Contains(capture.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("last administrator")
            && e.Message.Contains($"{CoAdminSpace}/_Access/{UserId}_Access"));
        Assert.DoesNotContain(capture.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact(Timeout = 120000)]
    public async Task SystemAdminAssignment_ReleasesTheWholeDoomedSet()
    {
        // A System-identity admin assignment survives the sweep (the importer's own identity) and
        // satisfies the last-admin invariant, so every human write grant is retracted — nothing
        // is spared and no last-admin warning is emitted for this space.
        await ProvisionSyncedSpace(SystemAdminSpace,
            AssignmentNodeFactory.UserRole(WellKnownUsers.System, "Admin", SystemAdminSpace));

        await WaitForAbsent($"{SystemAdminSpace}/_Access/{UserId}_Access");

        var systemGrant = await ReadNode($"{SystemAdminSpace}/_Access/{WellKnownUsers.System}_Access")
            .Timeout(10.Seconds()).ToTask();
        Assert.NotNull(systemGrant);

        Assert.DoesNotContain(capture.Entries, e =>
            e.Message.Contains("last administrator") && e.Message.Contains(SystemAdminSpace));
        Assert.DoesNotContain(capture.Entries, e => e.Level >= LogLevel.Error);
    }

    /// <summary>Polls until the node at <paramref name="path"/> is gone — the positive signal that
    /// the sweep retracted the grant.</summary>
    private async Task WaitForAbsent(string path) =>
        await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => ReadNode(path))
            .Where(n => n is null)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask();

    /// <summary>Polls the captured handler log until a record matches — the log capture is not an
    /// observable source, so the sanctioned interval re-query stands in for a stream wait.</summary>
    private async Task WaitForRetractionRecord(Func<RetractionLogCapture.Entry, bool> predicate) =>
        await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .Where(_ => capture.Entries.Any(predicate))
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask();

    /// <summary>Captures every record the <see cref="SystemOwnedAccessRetractionHandler"/> logs
    /// (instance-scoped — one per test, dies with the mesh).</summary>
    private sealed class RetractionLogCapture : ILoggerProvider
    {
        public sealed record Entry(LogLevel Level, string Message);

        private readonly ConcurrentQueue<Entry> entries = new();

        public IReadOnlyCollection<Entry> Entries => entries;

        public ILogger CreateLogger(string categoryName)
            => categoryName == typeof(SystemOwnedAccessRetractionHandler).FullName
                ? new Sink(entries)
                : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<Entry> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => sink.Enqueue(new Entry(logLevel, formatter(state, exception)));
        }
    }
}
