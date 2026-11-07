using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The install record decides who may write it</b> (MeshWeaver.Plugins#5055,
/// <see cref="InstallRecordGate"/>, <c>Doc/Architecture/ConditionalWritesAcrossHubs</c>). Writers state
/// facts under their own keys; the record's OWN hub folds them against current state — so a retirement
/// in flight refuses an unattended re-stamp at the owner, a re-stamp never re-creates a removed record,
/// a refusal reaches its writer, and a person's install clears a retirement. Real mesh, real record
/// hubs, the real uninstall executor.
/// </summary>
public class TheInstallRecordDecidesWhoMayWriteItTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder).AddPluginCatalog();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private static string RecordPath(string id) => $"{PackageInstaller.InstalledPartition}/{id}";

    private static MeshNode Record(string id, string version = "1.0.0") =>
        MeshNode.FromPath(RecordPath(id)) with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = id,
            State = MeshNodeState.Active,
            Content = new PackageManifest { Id = id, Name = id, Source = "Plugins", Version = version, TargetPartition = id },
        };

    private Task<MeshNode> Write(string id, string intent, CancellationToken ct, string version = "1.0.0") =>
        InstallRecordGate.Write(Mesh, Record(id, version), intent, NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>The write's terminal: the record written, or the refusal exception — never a hang.</summary>
    private Task<Exception?> Refusal(string id, string intent, CancellationToken ct, string version = "1.0.0") =>
        InstallRecordGate.Write(Mesh, Record(id, version), intent, NullLogger.Instance)
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<PackageManifest?> Stored(string id, CancellationToken ct) =>
        Storage.Read(RecordPath(id), Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .Select(n => n?.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions))
            .Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<PackageManifest> Live(string id, Func<PackageManifest, bool> until, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetMeshNodeStream(RecordPath(id)))
            .Select(n => n.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions))
            .OfType<PackageManifest>()
            .Where(until)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<string?> Begin(string id, string request, CancellationToken ct) =>
        InstallRecordGate.BeginRetirement(Mesh, id, request, "test", NullLogger.Instance).Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>
    /// (1) A stale RE-STAMP after the record was removed does not re-create it. Negative control
    /// (measured): with the old create-or-update write restored for a re-stamp, the record is back.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AReStampAfterTheRecordWasRemoved_DoesNotRecreateIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await Write("GateGone", PackageRecordWriteIntent.CreateAllowed, ct);
        Assert.NotNull(await Stored("GateGone", ct));
        Assert.True(await PackageInstaller.RemoveInstalledRecord(Mesh, "GateGone", NullLogger.Instance).Take(1)
            .Timeout(TestTimeouts.Convergence).Await(ct));

        var refused = Assert.IsType<PackageRecordWriteRefusedException>(await Refusal("GateGone", PackageRecordWriteIntent.Restamp, ct));
        Assert.Contains("never re-creates", refused.Reason);
        Assert.Null(await Stored("GateGone", ct));
    }

    /// <summary>
    /// (2) A retirement that starts BETWEEN two update candidates: A re-stamps, then B's retirement is
    /// granted, then B's re-stamp is refused AT THE OWNER, by name. Negative control (measured): with
    /// the owner's retirement check removed, B's stamp is applied.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARetirementBetweenTwoUpdateCandidates_RefusesTheSecondAtTheOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await Write("GateA", PackageRecordWriteIntent.CreateAllowed, ct);
        await Write("GateB", PackageRecordWriteIntent.CreateAllowed, ct);

        await Write("GateA", PackageRecordWriteIntent.Restamp, ct, version: "1.1.0");
        Assert.Null(await Begin("GateB", "Admin/_PackageUninstall/req-b", ct));
        var refused = Assert.IsType<PackageRecordWriteRefusedException>(
            await Refusal("GateB", PackageRecordWriteIntent.Restamp, ct, version: "1.1.0"));

        Assert.Contains("being retired by Admin/_PackageUninstall/req-b", refused.Reason);
        Assert.Equal("1.1.0", (await Live("GateA", r => r.Version == "1.1.0", ct)).Version);
        Assert.Equal("1.0.0", (await Live("GateB", r => r.RetiringBy is not null, ct)).Version);
    }

    /// <summary>
    /// (3) A retirement that is refused or fails RELEASES the record, which is repairable again. Negative
    /// control (measured): with the release ignored by the owner, the repair's re-stamp stays refused.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AReleasedRetirement_LeavesTheRecordRepairable()
    {
        var ct = TestContext.Current.CancellationToken;
        await Write("GateRelease", PackageRecordWriteIntent.CreateAllowed, ct);
        Assert.Null(await Begin("GateRelease", "Admin/_PackageUninstall/req-r", ct));
        Assert.IsType<PackageRecordWriteRefusedException>(await Refusal("GateRelease", PackageRecordWriteIntent.Restamp, ct, "1.1.0"));

        Assert.Null(await InstallRecordGate.EndRetirement(Mesh, "GateRelease", "Admin/_PackageUninstall/req-r", NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct));

        Assert.Null(await Refusal("GateRelease", PackageRecordWriteIntent.Restamp, ct, "1.2.0"));
        var stored = await Live("GateRelease", r => r.Version == "1.2.0", ct);
        Assert.Null(stored.RetiringBy);
    }

    /// <summary>
    /// (4) A person's catalog install CLEARS a retirement that has not started removing the record, and
    /// is applied. Negative control (measured): treated as an unattended stamp, it is refused.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task APersonsInstall_ClearsTheRetirement()
    {
        var ct = TestContext.Current.CancellationToken;
        await Write("GatePerson", PackageRecordWriteIntent.CreateAllowed, ct);
        Assert.Null(await Begin("GatePerson", "Admin/_PackageUninstall/req-p", ct));

        Assert.Null(await Refusal("GatePerson", PackageRecordWriteIntent.Person, ct, "2.0.0"));

        var stored = await Live("GatePerson", r => r.Version == "2.0.0", ct);
        Assert.Null(stored.RetiringBy);
        // …and the displaced uninstall is told, by name, when it tries to remove the record.
        var displaced = await InstallRecordGate.MarkRemoving(Mesh, "GatePerson", "Admin/_PackageUninstall/req-p", NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.Contains("no longer holds", displaced);
    }

    /// <summary>
    /// (5) A stamp the owner refuses is REPORTED to its writer — a typed refusal with the owner's words,
    /// never a write that "succeeds" while nothing was written. Negative control (measured): with the
    /// owner consuming a refused stamp without answering, the writer gets no refusal and this is red.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARefusedStamp_IsReportedToItsWriter_NeverASilentSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await Write("GateReport", PackageRecordWriteIntent.CreateAllowed, ct);
        Assert.Null(await Begin("GateReport", "Admin/_PackageUninstall/req-x", ct));

        var refusal = await Refusal("GateReport", PackageRecordWriteIntent.CreateAllowed, ct, "9.9.9");

        var refused = Assert.IsType<PackageRecordWriteRefusedException>(refusal);
        Assert.Equal(RecordPath("GateReport"), refused.Path);
        Assert.Contains("req-x", refused.Reason);
        Assert.Equal("1.0.0", (await Stored("GateReport", ct))!.Version);
    }

    /// <summary>
    /// A write through the gate is DURABLE when it completes: storage — what every later reader of the
    /// record uses (the next install's carry-forward, the update lanes) — already holds the stamp. Found
    /// by MeshWeaver.Plugins' install suites (5 red on the first cut, where the owner's fold was not
    /// awaited to storage). Negative control (measured): with the writer's answer removal no longer
    /// awaited, storage still holds the old version here.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AStamp_IsInStorageWhenTheWriteCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        await Write("GateDurable", PackageRecordWriteIntent.CreateAllowed, ct);
        await Write("GateDurable", PackageRecordWriteIntent.Restamp, ct, version: "3.0.0");
        Assert.Equal("3.0.0", (await Stored("GateDurable", ct))!.Version);
    }

    /// <summary>
    /// The existing person-requested uninstall holds the retirement through phase 1: it is granted,
    /// the record is removed, and a stale re-stamp afterwards is refused. Negative control: test (1).
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task APersonRequestedUninstall_HoldsTheRetirement_AndAStaleReStampCannotUndoIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await Write("GateUninstall", PackageRecordWriteIntent.CreateAllowed, ct);
        foreach (var node in new[]
                 {
                     MeshNode.FromPath("GateUninstall") with { NodeType = "Markdown", Name = "GateUninstall", State = MeshNodeState.Active },
                 })
            await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(node)).Timeout(TestTimeouts.Convergence).Await(ct);

        var ticket = await PackageUninstall.Request(Mesh, new PackageUninstallRequest
            {
                Package = "GateUninstall", Reason = "test", RequestedBy = "someone",
            })
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var request = await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(ticket.Path!))
            .Select(n => n.ContentAs<PackageUninstallRequest>(Mesh.JsonSerializerOptions))
            .OfType<PackageUninstallRequest>()
            .Where(r => r.Status == PackageUninstallStatus.AwaitingConfirmation || PackageUninstallStatus.IsTerminal(r.Status))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        Assert.True(request.Status == PackageUninstallStatus.AwaitingConfirmation, request.Failure ?? request.Status);
        Assert.Null(await Stored("GateUninstall", ct));
        Assert.IsType<PackageRecordWriteRefusedException>(await Refusal("GateUninstall", PackageRecordWriteIntent.Restamp, ct, "1.1.0"));
        Assert.Null(await Stored("GateUninstall", ct));
    }
}
