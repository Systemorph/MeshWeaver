using MeshWeaver.Observability;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// Pins the identity function against the bursts that actually defeated the four earlier attempts on
/// memex.systemorph.com (2026-08-08/09). Every case here is a real production line, not a
/// hand-invented one — the previous rules all passed hand-invented input and failed these.
/// </summary>
public class StructuralIdentityTest
{
    private static LogBurst Burst(string category, int eventId, string message,
        string? exception = null, string? frame = null) =>
        new(category, eventId, LogSeverity.Error, message,
            LogLineParser.Normalize(message), exception, frame);

    /// <summary>
    /// Round 3's failure: one ROUTER_TRAFFIC defect, reported once per target hub, produced ~50
    /// incidents. The subject sits AFTER a label.
    /// </summary>
    [Theory]
    [InlineData("Claims")]
    [InlineData("Edu")]
    [InlineData("X")]
    [InlineData("ReinsuranceDemo")]
    public void OneDefect_ManyTargets_IsOneIncident(string target)
    {
        string Msg(string t) =>
            $"ROUTER_TRAFFIC: RawJson has the mesh hub as sender (sender: mesh/N-u6rl0oAUuc, target: {t}). "
            + "The mesh hub is the ROUTER and must not execute work.";

        var baseline = StructuralLogIncidentIdentity.Compute(
            Burst("MeshWeaver.Messaging.MessageHub", 0, Msg("Claims")));

        StructuralLogIncidentIdentity.Compute(Burst("MeshWeaver.Messaging.MessageHub", 0, Msg(target)))
            .Should().Be(baseline);
    }

    /// <summary>
    /// Round 4's failure — the one I stopped before patching: ~22 incidents for one reconcile defect,
    /// with the subject BEFORE the colon, where a `label: value` rule cannot see it.
    /// </summary>
    [Theory]
    [InlineData("Chess")]
    [InlineData("Edu")]
    [InlineData("Underwriting")]
    [InlineData("LinkedIn")]
    public void OneDefect_ManyPlugins_IsOneIncident(string plugin)
    {
        string Msg(string p) =>
            $"[PluginGating] {p}: reconcile is NOT CONVERGING — rewrote {p}/_Access/Public_Access, "
            + $"{p}/_Access/Anonymous_Access, which this hub already wrote.";

        var baseline = StructuralLogIncidentIdentity.Compute(Burst("PluginGating", 0, Msg("Chess")));

        StructuralLogIncidentIdentity.Compute(Burst("PluginGating", 0, Msg(plugin)))
            .Should().Be(baseline);
    }

    /// <summary>
    /// Round 1's failure, in the other direction: collapsing must NOT go so far that genuinely
    /// different faults share an incident. Different exceptions at the same site stay apart.
    /// </summary>
    [Fact]
    public void DifferentExceptions_AtTheSameSite_StayApart()
    {
        var a = StructuralLogIncidentIdentity.Compute(
            Burst("MeshWeaver.Hosting.Orleans.MessageHubGrain", 0, "call failed",
                exception: "System.TimeoutException"));
        var b = StructuralLogIncidentIdentity.Compute(
            Burst("MeshWeaver.Hosting.Orleans.MessageHubGrain", 0, "call failed",
                exception: "Orleans.Runtime.SiloUnavailableException"));

        b.Should().NotBe(a);
    }

    /// <summary>
    /// 2026-08-10, issues #1170 and #1171: ONE <c>ObjectDisposedException</c>, raised at
    /// <c>SynchronizationStream&lt;T&gt;.OnCompleted()</c> during ONE hub teardown, on ONE pod, at ONE
    /// instant — and two tickets, because the unwind was logged twice on its way out: once by
    /// <c>MessageHub</c> ("Error during shutdown of hub …") and once by <c>HostedHubsCollection</c>
    /// ("Hub … disposal faulted"). These are the verbatim bursts. They must fold into one incident.
    ///
    /// <para>Note the two report shapes: <c>MessageHub</c> formats the exception INTO its message,
    /// <c>HostedHubsCollection</c> lets the logger print it on its own line. Both must yield the same
    /// exception type, or the identity forks on the caller's formatting rather than on the fault.</para>
    /// </summary>
    [Fact]
    public void OneFault_LoggedByTwoCatchSitesOnTheWayOut_IsOneIncident()
    {
        string[] viaMessageHub =
        [
            "fail: MeshWeaver.Messaging.MessageHub[0]",
            "      Error during shutdown of hub sync/KVk1hB2Tw02ESgrSpL3Cgg after 0ms (total disposal "
            + "time: 0ms): System.ObjectDisposedException: Cannot access a disposed object.",
            "         at System.Reactive.Subjects.ReplaySubject`1.ReplayBase.CheckDisposed()",
            "         at System.Reactive.Subjects.ReplaySubject`1.OnCompleted()",
            "         at MeshWeaver.Data.Serialization.SynchronizationStream`1.OnCompleted() in "
            + "/home/runner/work/MeshWeaver/MeshWeaver/src/MeshWeaver.Data/Serialization/SynchronizationStream.cs:line 680",
            "         at System.Reactive.SafeObserver`1.WrappingSafeObserver.OnCompleted()",
        ];
        string[] viaHostedHubsCollection =
        [
            "fail: MeshWeaver.Messaging.HostedHubsCollection[0]",
            "      Hub sync/KVk1hB2Tw02ESgrSpL3Cgg disposal faulted",
            "      System.ObjectDisposedException: Cannot access a disposed object.",
            "         at System.Reactive.Subjects.ReplaySubject`1.ReplayBase.CheckDisposed()",
            "         at System.Reactive.Subjects.ReplaySubject`1.OnCompleted()",
            "         at MeshWeaver.Data.Serialization.SynchronizationStream`1.OnCompleted() in "
            + "/home/runner/work/MeshWeaver/MeshWeaver/src/MeshWeaver.Data/Serialization/SynchronizationStream.cs:line 680",
            "         at System.Reactive.SafeObserver`1.WrappingSafeObserver.OnCompleted()",
        ];

        var a = LogLineParser.Parse(viaMessageHub)!;
        var b = LogLineParser.Parse(viaHostedHubsCollection)!;

        // One fingerprint ⇒ one incident node ⇒ one ticket. This is the whole claim.
        LogLineParser.Fingerprint(b).Should().Be(LogLineParser.Fingerprint(a));

        // …and why: the fault is the same one in both — the reporters differ, and only the reporters.
        a.Category.Should().NotBe(b.Category);
        a.TopFrame.Should().Be(b.TopFrame);
        a.ExceptionType.Should().Be(b.ExceptionType);
    }

    /// <summary>
    /// The negative control for the case above, and also real: issues #1183 and #1184 — the same
    /// category (<c>DefaultHealthCheckService</c>), the same event id (103), the same exception
    /// (<c>OperationCanceledException</c>), the same pod, the same second. Only the health check that
    /// raised it differs. Two different code sites are two defects, and folding the reporter into the
    /// identity must not be "fixed" by folding the fault site out of it as well.
    /// </summary>
    [Fact]
    public void TwoCodeSites_ReportedByOneLogger_StayTwoIncidents()
    {
        string[] dbVersionCheck =
        [
            "fail: Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService[103]",
            "      Health check db_version with status Unhealthy completed after 3462.0492ms with "
            + "message 'db_version check threw'",
            "      System.OperationCanceledException: The operation was canceled.",
            "         at System.Threading.CancellationToken.ThrowIfCancellationRequested()",
            "         at Npgsql.PoolingDataSource.<Get>g__RentAsync|33_0(NpgsqlConnection conn, "
            + "NpgsqlTimeout timeout, Boolean async, CancellationToken cancellationToken)",
            "         at Memex.Portal.Distributed.DbVersionHealthCheck.CheckHealthAsync("
            + "HealthCheckContext context, CancellationToken cancellationToken) in "
            + "/home/runner/work/MeshWeaver/MeshWeaver/memex/aspire/Memex.Portal.Distributed/DbVersionGate.cs:line 115",
        ];
        string[] npgsqlCheck =
        [
            "fail: Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService[103]",
            "      Health check PostgreSql with status Unhealthy completed after 3459.7535ms with "
            + "message 'The operation was canceled.'",
            "      System.OperationCanceledException: The operation was canceled.",
            "         at System.Threading.CancellationToken.ThrowIfCancellationRequested()",
            "         at Npgsql.PoolingDataSource.<Get>g__RentAsync|33_0(NpgsqlConnection conn, "
            + "NpgsqlTimeout timeout, Boolean async, CancellationToken cancellationToken)",
            "         at HealthChecks.NpgSql.NpgSqlHealthCheck.CheckHealthAsync(HealthCheckContext "
            + "context, CancellationToken cancellationToken) in "
            + "/home/runner/work/AspNetCore.Diagnostics.HealthChecks/src/HealthChecks.NpgSql/NpgSqlHealthCheck.cs:line 31",
        ];

        var a = LogLineParser.Parse(dbVersionCheck)!;
        var b = LogLineParser.Parse(npgsqlCheck)!;

        a.Category.Should().Be(b.Category);
        a.ExceptionType.Should().Be(b.ExceptionType);
        a.TopFrame.Should().NotBe(b.TopFrame);

        LogLineParser.Fingerprint(b).Should().NotBe(LogLineParser.Fingerprint(a));
    }

    /// <summary>
    /// The frame localises the fault, but WHAT went wrong there still discriminates: the same method
    /// throwing two different exception types is two defects. Without this the "drop the reporter"
    /// rule would have nothing left to split on inside a frame.
    /// </summary>
    [Fact]
    public void DifferentExceptions_AtTheSameFrame_StayApart()
    {
        var a = StructuralLogIncidentIdentity.Compute(
            Burst("MeshWeaver.Data.MeshDataSource", 0, "boom",
                exception: "System.InvalidOperationException", frame: "MeshDataSource.Apply(MeshNode)"));
        var b = StructuralLogIncidentIdentity.Compute(
            Burst("MeshWeaver.Data.MeshDataSource", 0, "boom",
                exception: "System.NullReferenceException", frame: "MeshDataSource.Apply(MeshNode)"));

        b.Should().NotBe(a);
    }

    /// <summary>
    /// Two spellings of one type — <c>ex.ToString()</c> prints the namespace, a message that
    /// interpolates <c>ex.GetType().Name</c> does not — are the same fault, not two.
    /// </summary>
    [Fact]
    public void QualifiedAndSimpleExceptionNames_AreTheSameFault()
    {
        var qualified = StructuralLogIncidentIdentity.Compute(
            Burst("MeshWeaver.Messaging.MessageHub", 0, "boom",
                exception: "System.ObjectDisposedException", frame: "SynchronizationStream`1.OnCompleted()"));
        var simple = StructuralLogIncidentIdentity.Compute(
            Burst("MeshWeaver.Messaging.MessageHub", 0, "boom",
                exception: "ObjectDisposedException", frame: "SynchronizationStream`1.OnCompleted()"));

        simple.Should().Be(qualified);
    }

    /// <summary>
    /// Dropping the reporting category applies ONLY where a fault site is known. A bare diagnostic has
    /// no frame, so the log site is all the structure there is — and two different sites stay apart.
    /// </summary>
    [Fact]
    public void WithoutAFaultSite_TheLogSiteStillSplits()
    {
        StructuralLogIncidentIdentity.Compute(Burst("MeshWeaver.Graph.MeshCatalog", 0, "boom"))
            .Should().NotBe(
                StructuralLogIncidentIdentity.Compute(Burst("MeshWeaver.Data.MeshDataSource", 0, "boom")));
    }

    [Fact]
    public void DifferentTopFrames_ForTheSameException_StayApart()
    {
        var a = StructuralLogIncidentIdentity.Compute(
            Burst("MeshWeaver.Data.MeshDataSource", 0, "boom",
                exception: "System.InvalidOperationException", frame: "MeshDataSource.Apply(MeshNode)"));
        var b = StructuralLogIncidentIdentity.Compute(
            Burst("MeshWeaver.Data.MeshDataSource", 0, "boom",
                exception: "System.InvalidOperationException", frame: "MeshDataSource.Flush()"));

        b.Should().NotBe(a);
    }

    [Fact]
    public void DifferentEventIds_InTheSameCategory_StayApart()
    {
        // The event id is what the code assigns to a log SITE — two sites in one class are two faults.
        StructuralLogIncidentIdentity.Compute(Burst("PluginGating", 1, "reconcile is NOT CONVERGING"))
            .Should().NotBe(
                StructuralLogIncidentIdentity.Compute(Burst("PluginGating", 0, "reconcile is NOT CONVERGING")));
    }

    /// <summary>
    /// A burst with neither exception nor frame is identified by its log SITE alone — the message
    /// contributes nothing, so no wording change can fork it.
    /// </summary>
    [Fact]
    public void BareDiagnostics_AreIdentifiedByTheirLogSite()
    {
        var a = StructuralLogIncidentIdentity.Compute(
            Burst("DynamicTypePreWarmer", 0, "REFUSING READINESS — 3 NodeType(s) regressed on this image"));
        var b = StructuralLogIncidentIdentity.Compute(
            Burst("DynamicTypePreWarmer", 0, "REFUSING READINESS — 17 NodeType(s) regressed on this image"));

        b.Should().Be(a);

        // …and the same holds for a wholly different wording at the same site: prose is not part of
        // the key at all, which is the property four earlier rules lacked.
        StructuralLogIncidentIdentity.Compute(
                Burst("DynamicTypePreWarmer", 0, "something else entirely"))
            .Should().Be(a);
    }

    [Fact]
    public void Identity_IsShortAndStable()
    {
        var id = StructuralLogIncidentIdentity.Compute(Burst("Some.Category", 0, "a message"));

        id.Should().HaveLength(16);
        StructuralLogIncidentIdentity.Compute(Burst("Some.Category", 0, "a message")).Should().Be(id);
    }
}
