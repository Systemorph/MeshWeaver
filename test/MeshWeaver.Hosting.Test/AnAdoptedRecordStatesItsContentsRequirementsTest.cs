using System;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>An install record states the requirements of the content it is at.</b> Measured on the plugin
/// registry during the 3 → 4 roll: the Governance record had been adopted from its sync source to
/// 4.0.3 (floor 4.0.13516) while it still listed the September release's <c>AI@^1.0.0</c>, because
/// <see cref="PackageUpdateReconciler.AdoptedFromSync"/> moved the version, the hash and the floor and
/// kept the requirements. The dependency-floor check judges a record of floor 4.x on line 4, so that
/// one stale line refused every line-4 module-set proposal against the landed AI 4.0.3 — the first
/// line-4 replica stayed on an empty module set and its readiness gates refused it. Pure.
/// </summary>
public class AnAdoptedRecordStatesItsContentsRequirementsTest
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 1, 0, 0, TimeSpan.Zero);
    private const string CandidateHash = "531fdb35d7d08c01";

    private static PackageManifest SeptemberRecord(string moduleVersion) => new()
    {
        Id = "Governance",
        Source = "Plugins",
        Version = "0.2.10",
        ModuleVersion = moduleVersion,
        MinMeshVersion = "3.0.0-ci.7845",
        Requires = ["Store@^1.0.0", "AI@^1.0.0", "Signature@^1.2.0"],
        AuthorizedBy = "system-security",
        InstalledAtUtc = new DateTimeOffset(2026, 9, 23, 16, 23, 10, TimeSpan.Zero),
    };

    private static PackageManifest Candidate() => new()
    {
        Id = "Governance",
        Source = "Plugins",
        Version = "4.0.3",
        ModuleVersion = CandidateHash,
        MinMeshVersion = "4.0.13516",
        Requires = ["Store@^4.0.0", "AI@^4.0.0", "Signature@^4.0.0", "Essentials@^4.0.0"],
    };

    /// <summary>The incident's write: adopting a candidate takes its requirements with its floor.</summary>
    [Fact]
    public void AdoptingACandidate_TakesItsRequirements()
    {
        var adopted = PackageUpdateReconciler.AdoptedFromSync(SeptemberRecord("old"), Candidate(), Now);

        Assert.Equal(Candidate().Requires, adopted.Requires);
        Assert.Equal("4.0.13516", adopted.MinMeshVersion);
        Assert.Equal("system-security", adopted.AuthorizedBy);
    }

    /// <summary>The record the old adoption left behind — at the candidate's content, with the old
    /// requirements — is recognised as stating other declarations…</summary>
    [Fact]
    public void ARecordAtTheCandidatesContent_WithOldRequirements_DoesNotDeclareAsTheCandidate()
    {
        var stale = SeptemberRecord(CandidateHash) with { MinMeshVersion = "4.0.13516", Version = "4.0.3" };

        Assert.False(PackageUpdateReconciler.DeclaresAsTheCandidate(stale, Candidate()));
        Assert.True(PackageUpdateReconciler.DeclaresAsTheCandidate(
            PackageUpdateReconciler.WithCandidateDeclarations(stale, Candidate()), Candidate()));
    }

    /// <summary>…and corrected to the content's own requirements, keeping every install-time field.</summary>
    [Fact]
    public void HealingTheDeclarations_KeepsEverythingInstallTime()
    {
        var stale = SeptemberRecord(CandidateHash) with { MinMeshVersion = "4.0.13516", Version = "4.0.3" };

        var healed = PackageUpdateReconciler.WithCandidateDeclarations(stale, Candidate());

        Assert.Equal(Candidate().Requires, healed.Requires);
        Assert.Equal(stale.ModuleVersion, healed.ModuleVersion);
        Assert.Equal(stale.AuthorizedBy, healed.AuthorizedBy);
        Assert.NotNull(stale.InstalledAtUtc);
        Assert.Equal(stale.InstalledAtUtc, healed.InstalledAtUtc);
    }

    /// <summary>A record already stating the candidate's declarations needs no write.</summary>
    [Fact]
    public void ARecordStatingTheCandidatesDeclarations_NeedsNoHeal()
        => Assert.True(PackageUpdateReconciler.DeclaresAsTheCandidate(Candidate(), Candidate()));
}

/// <summary>
/// The heal's WRITE path, through the real reconciler on a real mesh: a stored record at the listed
/// content hash that states the September requirements is corrected to the content's own by
/// <see cref="PackageUpdateReconciler.ReconcileInstalled"/>. The source fetches nothing — it throws if
/// asked — because an equal-hash pass must not read any content.
/// </summary>
public class AnEqualHashPassHealsTheRecordsRequirementsTest(ITestOutputHelper output)
    : MeshWeaver.Hosting.Monolith.TestBase.MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder).AddPluginCatalog();

    private static string RecordPath(string id) => $"{PackageInstaller.InstalledPartition}/{id}";

    private static PackageManifest Manifest(string id, string hash, params string[] requires) => new()
    {
        Id = id, Name = id, Source = "Plugins", Version = "4.0.3", TargetPartition = id,
        Kind = PackageKind.NodeRepo, ModuleVersion = hash, MinMeshVersion = "4.0.13516",
        Requires = [.. requires], UpdatePolicy = PackageUpdatePolicy.Auto,
    };

    private System.Threading.Tasks.Task Store(PackageManifest record, System.Threading.CancellationToken ct) =>
        InstallRecordGate.Write(Mesh, MeshNode.FromPath(RecordPath(record.Id)) with
            {
                NodeType = PackageInstaller.PackageNodeType, Name = record.Id, State = MeshNodeState.Active, Content = record,
            }, PackageRecordWriteIntent.CreateAllowed, NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct);

    private System.Threading.Tasks.Task<PackageManifest> Live(string id, Func<PackageManifest, bool> until,
        System.Threading.CancellationToken ct) =>
        Mesh.GetMeshNodeStream(RecordPath(id))
            .Select(n => n?.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions))
            .OfType<PackageManifest>()
            .Where(until)
            .Take(1)
            .Timeout(TestTimeouts.Convergence).Await(ct);

    private sealed class SilentSource : IPackageSource
    {
        public IObservable<System.Collections.Generic.IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return((System.Collections.Generic.IReadOnlyList<PackageManifest>)[]);

        public IObservable<System.Collections.Generic.IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            Observable.Throw<System.Collections.Generic.IReadOnlyList<PackageFile>>(
                new InvalidOperationException("an equal-hash pass must fetch nothing"));
    }

    [Fact(Timeout = 120_000)]
    public async System.Threading.Tasks.Task AStaleRecordAtTheListedContent_TakesTheContentsRequirements()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store(Manifest("HealGov", "h1", "Store@^1.0.0", "AI@^1.0.0"), ct);
        var candidate = Manifest("HealGov", "h1", "Store@^4.0.0", "AI@^4.0.0");

        await Mesh.ServiceProvider.GetRequiredService<AccessService>().RunAsSystem(() =>
                PackageUpdateReconciler.ReconcileInstalled(Mesh, new SilentSource(), "HEAD", [candidate], "test",
                    NullLogger.Instance))
            .Timeout(TestTimeouts.Convergence).Await(ct);

        var healed = await Live("HealGov", m => m.Requires.Contains("AI@^4.0.0"), ct);
        Assert.Equal(candidate.Requires, healed.Requires);
        Assert.Equal("h1", healed.ModuleVersion);
    }
}
