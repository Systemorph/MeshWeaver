using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// The claim this whole subsystem rests on, exercised against a real mesh: <b>a burst of identical
/// errors produces exactly ONE incident node</b>, and every later burst folds into it rather than
/// creating another. If this were wrong, a production incident would open a ticket per log line.
/// </summary>
public class LogIncidentIngestTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Fingerprint = "deadbeefcafe0001";
    private static readonly DateTimeOffset T0 = new(2026, 8, 7, 10, 0, 0, TimeSpan.Zero);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddLogWatch();

    private LogIncidentIngestService Ingest() =>
        new(Mesh,
            Mesh.ServiceProvider.GetRequiredService<IOptions<LogWatchOptions>>(),
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<LogIncidentIngestService>>());

    private static LogIncidentReport Report(long occurrences, DateTimeOffset at, string pod = "portal-a") => new()
    {
        Fingerprint = Fingerprint,
        Category = "MeshWeaver.Data.MeshDataSource",
        Severity = LogSeverity.Error,
        ExceptionType = "System.InvalidOperationException",
        NormalizedMessage = "Update failed for node {path}",
        TopFrame = "MeshWeaver.Data.MeshDataSource.Apply(MeshNode node)",
        Namespace = "memex",
        Pods = ImmutableList.Create(pod),
        Occurrences = occurrences,
        FirstSeen = at,
        LastSeen = at,
        Samples = ImmutableList.Create(new LogSample(at, pod, "fail: boom")),
    };

    /// <summary>Reads the incident's live content off its authoritative node stream.</summary>
    private Task<LogIncident?> ReadIncident() =>
        Mesh.GetWorkspace()
            .GetMeshNodeStream(LogIncidentNodeType.IncidentPath(Fingerprint))
            .Where(node => node is not null)
            .Select(node => node.ContentAs<LogIncident>(Mesh.JsonSerializerOptions))
            .Where(incident => incident is not null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(20))
            .ToTask();

    [Fact(Timeout = 60000)]
    public async Task FirstSighting_OpensOneIncidentRequestingTriage()
    {
        var result = await Ingest().Report(Report(3, T0)).FirstAsync().ToTask();

        result.Created.Should().BeTrue();
        result.Path.Should().Be(LogIncidentNodeType.IncidentPath(Fingerprint));

        var incident = await ReadIncident();
        incident.Should().NotBeNull();
        incident!.Occurrences.Should().Be(3);
        incident.Category.Should().Be("MeshWeaver.Data.MeshDataSource");
        incident.Status.Should().Be(LogIncidentStatus.New);
        // The control-plane request, not a direct status write — the watcher reacts to this.
        incident.RequestedStatus.Should().Be(LogIncidentRequest.Triage);
    }

    [Fact(Timeout = 60000)]
    public async Task RepeatBursts_FoldIntoTheSameIncident()
    {
        var ingest = Ingest();
        await ingest.Report(Report(3, T0)).FirstAsync().ToTask();

        var second = await ingest.Report(Report(7, T0.AddMinutes(2), "portal-b")).FirstAsync().ToTask();
        second.Created.Should().BeFalse();

        var incident = await Mesh.GetWorkspace()
            .GetMeshNodeStream(LogIncidentNodeType.IncidentPath(Fingerprint))
            .Select(node => node.ContentAs<LogIncident>(Mesh.JsonSerializerOptions))
            .Where(i => i is { Occurrences: 10 })
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(20))
            .ToTask();

        incident!.Occurrences.Should().Be(10);
        incident.LastSeen.Should().Be(T0.AddMinutes(2));
        incident.FirstSeen.Should().Be(T0);
        // Same defect on a second pod stays ONE incident; the pod rides along as evidence.
        incident.Pods.Should().BeEquivalentTo(
            new[] { "portal-a", "portal-b" }, System.Text.Json.JsonSerializerOptions.Default);
    }

    [Fact(Timeout = 60000)]
    public async Task RepeatBursts_NeverCreateASecondNode()
    {
        var ingest = Ingest();
        foreach (var i in Enumerable.Range(0, 5))
            await ingest.Report(Report(1, T0.AddSeconds(i))).FirstAsync().ToTask();

        var children = await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{LogIncidentNodeType.IncidentNamespace} scope:children "
                + $"nodeType:{LogIncidentNodeType.NodeType}"))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(20))
            .ToTask();

        // Five bursts of one fault ⇒ one node ⇒ (later) one GitHub issue.
        children.Items.Should().ContainSingle();
    }
}
