using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Retention of <c>Admin/_Recycle</c> (#6813): a NEW request prunes the requests older than the
/// declared window together with their per-process reports, and leaves the ones inside it — audit
/// and all — readable.
/// </summary>
public class ActivationRecycleRetentionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypeR = "RetentionTypeR";

    /// <summary>
    /// The mesh's registered policy: a window of one millisecond, constructed directly (the
    /// configuration clamp does not apply), so a request whose report has landed is already
    /// expired by the time the next request is written — no clock to fake, no sleep.
    /// </summary>
    private static readonly ActivationRecycleRetention Tight = new() { MaxAge = TimeSpan.FromMilliseconds(1) };

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode(TypeR) { Name = "Retention Type R" },
                new MeshNode("RetentionR1", TestPartition) { Name = "R one", NodeType = TypeR })
            .ConfigureServices(services => services.AddSingleton(Tight));

    private IObservable<ImmutableList<string>> RequestPaths() =>
        MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{ActivationRecycleRequest.Namespace} scope:children nodeType:{ActivationRecycleRequest.NodeType}").AsSystem())
            .Take(1)
            .Select(c => c.Items.Select(n => n.Path).ToImmutableList());

    private Task<ActivationRecycleTicket> Issue(string reason) =>
        ActivationRecycle.Request(RequestHub, new ActivationRecycleRequest
        {
            NodeTypes = ImmutableList.Create(TypeR),
            Reason = reason,
            RequestedBy = nameof(ActivationRecycleRetentionTest),
        }).FirstAsync().Await(TestContext.Current.CancellationToken);

    /// <summary>The request's report (this monolith's one process) has been written.</summary>
    private Task ReportWritten(string requestPath) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => ActivationRecycle.Reports(RequestHub, requestPath))
            .Where(reports => !reports.IsEmpty)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"the process reports on {requestPath}", cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>
    /// A request older than the window is pruned with its report when the next request is written;
    /// the new request itself is kept. Negative control: with the prune disabled the old request
    /// and its report stay, and this test fails on the first assertion below.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ANewRequest_PrunesAnExpiredRequestWithItsReports_AndKeepsItself()
    {
        var ct = TestContext.Current.CancellationToken;
        Mesh.ServiceProvider.GetRequiredService<ActivationRecycleRetention>().Should().BeSameAs(Tight,
            "the precondition: the test's declared policy is the one Request applies");

        var old = await Issue("test: the old request");
        old.Accepted.Should().BeTrue();
        await ReportWritten(old.Path!);

        var fresh = await Issue("test: the new request");
        fresh.Accepted.Should().BeTrue();

        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => RequestPaths())
            .Where(paths => !paths.Contains(old.Path!, StringComparer.OrdinalIgnoreCase))
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the old request is past the window and the new request's prune removes it", cancellationToken: ct);
        (await ActivationRecycle.Reports(RequestHub, old.Path!).FirstAsync().Await(ct))
            .Should().BeEmpty("the per-process reports leave with their request");
        (await RequestPaths().FirstAsync().Await(ct))
            .Should().Contain(fresh.Path!, "the request that triggered the prune is never pruned by it");
    }

    /// <summary>A request inside the window survives a prune, its report still readable.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ARequestInsideTheWindow_SurvivesThePrune_WithItsReport()
    {
        var ct = TestContext.Current.CancellationToken;
        var ticket = await Issue("test: a recent request");
        await ReportWritten(ticket.Path!);

        var removed = await ActivationRecycle.Prune(RequestHub, ActivationRecycleRetention.Default, DateTimeOffset.UtcNow)
            .FirstAsync().Await(ct);

        removed.Should().Be(0, "nothing here is thirty days old");
        (await RequestPaths().FirstAsync().Await(ct)).Should().Contain(ticket.Path!);
        (await ActivationRecycle.Reports(RequestHub, ticket.Path!).FirstAsync().Await(ct))
            .Should().ContainSingle("the audit stays readable inside the window");
    }

    /// <summary>The decision is pure, oldest first, capped, and fails closed on anything it cannot age.</summary>
    [Fact]
    public void SelectForPruning_TakesExpiredRequestsOldestFirst_AndFailsClosed()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var policy = new ActivationRecycleRetention { MaxAge = TimeSpan.FromDays(30), MaxDeletionsPerRun = 2 };
        MeshNode Row(string id, int daysOld, string nodeType = ActivationRecycleRequest.NodeType,
            string ns = ActivationRecycleRequest.Namespace) =>
            new(id, ns) { NodeType = nodeType, LastModified = daysOld < 0 ? default : now.AddDays(-daysOld) };

        var rows = new[]
        {
            Row("fresh", 29),
            Row("old40", 40),
            Row("old60", 60),
            Row("old50", 50),
            Row("undated", -1),
            Row("report", 90, ActivationRecycleReport.NodeType),
            Row("elsewhere", 90, ns: "Admin/_Other"),
        };

        policy.SelectForPruning(rows, now).Should().Equal(
            $"{ActivationRecycleRequest.Namespace}/old60", $"{ActivationRecycleRequest.Namespace}/old50");
        policy.SelectForPruning(rows, now, $"{ActivationRecycleRequest.Namespace}/old60").Should().Equal(
            $"{ActivationRecycleRequest.Namespace}/old50", $"{ActivationRecycleRequest.Namespace}/old40");
        (policy with { Enabled = false }).SelectForPruning(rows, now).Should().BeEmpty();
    }

    /// <summary>The configured window is clamped to the floor; a malformed value keeps the default.</summary>
    [Fact]
    public void FromConfiguration_ClampsTheWindow_AndIgnoresMalformedValues()
    {
        IConfiguration Config(string maxAge) => new ConfigurationBuilder()
            .AddInMemoryCollection([new(ActivationRecycleRetention.MaxAgeConfigKey, maxAge)]).Build();

        ActivationRecycleRetention.FromConfiguration(Config("00:00:01")).MaxAge
            .Should().Be(ActivationRecycleRetention.MinimumMaxAge);
        ActivationRecycleRetention.FromConfiguration(Config("00:00:00")).MaxAge
            .Should().Be(ActivationRecycleRetention.MinimumMaxAge, "a parsed zero is below the floor, not malformed");
        ActivationRecycleRetention.FromConfiguration(Config("-1.00:00:00")).MaxAge
            .Should().Be(ActivationRecycleRetention.MinimumMaxAge, "a parsed negative span is below the floor, not malformed");
        ActivationRecycleRetention.FromConfiguration(Config("not-a-span")).MaxAge
            .Should().Be(ActivationRecycleRetention.Default.MaxAge);
        ActivationRecycleRetention.FromConfiguration(Config("7.00:00:00")).MaxAge
            .Should().Be(TimeSpan.FromDays(7));
    }
}
