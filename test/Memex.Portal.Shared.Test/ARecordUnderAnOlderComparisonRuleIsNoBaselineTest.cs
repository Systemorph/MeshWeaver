using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A HASH OF THE SOURCE IS NO EVIDENCE OF WHAT THE MESH STORES</b> — MeshWeaver.Plugins#2786.
///
/// <para>Measured on a deployed portal: <c>Governance/NameCheck</c> declares
/// <c>"keepsHistory": false</c> in its file and the stored definition read <c>true</c>. MeshWeaver#6479
/// widened the per-node comparison so it sees that member, and the package was then REINSTALLED on a
/// platform carrying #6479 — and the stored node did not move. The comparison was never reached: the
/// install funnel decides from the install record first, and both of its shortcuts read hashes of the
/// SOURCE (the module hash for the up-to-date exit, the per-file map for the incremental diff). The
/// file's hash had not moved since the install whose comparison could not see the difference — that
/// install skipped the write and stamped the hash anyway — so every later install trusted the hash and
/// never looked at the node again.</para>
///
/// <para>The fixture re-enacts that state with the real funnel: install the package, make one stored
/// node differ from what the package ships while its file hash stays the same, and stamp the record
/// the way every installer before the comparison rule was versioned did (no
/// <see cref="PackageManifest.ComparisonEpoch"/>). The reinstall must rewrite the node. The control
/// is the identical sequence under a record stamped by the CURRENT rule: there the up-to-date exit is
/// trusted and nothing is written — which is what proves the rule on the record, and nothing else in
/// the sequence, is what makes the reinstall compare.</para>
/// </summary>
public class ARecordUnderAnOlderComparisonRuleIsNoBaselineTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Packaged = "# Guide\n\nThe node the tests lose.";
    private const string Drifted = "# Guide\n\nWhat an installer that could not see the difference left behind.";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton<IPackageSource>(_ => new ReassertFixture.Source(ReassertFixture.Files()))
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false }));

    [Fact(Timeout = 300_000)]
    public async Task AStoredNodeThatDiffersUnderAnUnchangedHash_IsRewritten_WhenTheRecordPredatesTheRule()
    {
        var result = await ReinstallOverADriftedNode(restampEpoch: true, recordedEpoch: null,
            TestContext.Current.CancellationToken);

        result.WrittenPaths.Should().Contain(ReassertFixture.GuidePath,
            "THE assertion: the record was stamped before the comparison rule was versioned, so its "
            + "equal module hash is no evidence the stored node matches the package — the reinstall "
            + "must compare it and write it. On main the up-to-date exit returns before any node is "
            + "compared, which is how Governance/NameCheck kept keepsHistory:true (Plugins#2786)");
        (await WaitForGuideBody(Packaged)).Should().BeTrue(
            "the stored node must carry what the package ships once the reinstall has run");

        var after = await ReassertFixture.ReadRecord(Mesh);
        after!.ComparisonEpoch.Should().Be(PackageInstaller.InstallComparisonEpoch,
            "the full install compared every node, so it may stamp the current rule — otherwise every "
            + "later install would repeat the full compare forever");
        after.ModuleVersion.Should().Be(ReassertFixture.ModuleHash,
            "a re-compare at the recorded version is never an update");
    }

    /// <summary>
    /// The mixed-image roll: a pod on a LATER image stamped a NEWER rule onto the shared record. That
    /// record is no baseline for this installer either — under an "at least" test the up-to-date exit
    /// would trust it, and an incremental update would compare under this installer's older rule
    /// while carrying the newer value forward, leaving a record that falsely claims the newer
    /// comparison ran. The installer must compare in full and stamp its OWN rule.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARecordStampedByANewerRule_IsNoBaselineEither_AndTheReinstallComparesInFull()
    {
        var result = await ReinstallOverADriftedNode(restampEpoch: true,
            recordedEpoch: PackageInstaller.InstallComparisonEpoch + 1, TestContext.Current.CancellationToken);

        result.WrittenPaths.Should().Contain(ReassertFixture.GuidePath,
            "a record stamped by a newer rule than this installer applies says nothing about what THIS "
            + "installer's comparison would decide, so the reinstall must compare the drifted node and write it");
        (await WaitForGuideBody(Packaged)).Should().BeTrue(
            "the stored node must carry what the package ships once the reinstall has run");
        var after = await ReassertFixture.ReadRecord(Mesh);
        after!.ComparisonEpoch.Should().Be(PackageInstaller.InstallComparisonEpoch,
            "the full install stamps THIS installer's rule, never carries the newer one forward");
    }

    /// <summary>The predicate itself: only an exact match is a baseline.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(PackageInstaller.InstallComparisonEpoch - 1, true)]
    [InlineData(PackageInstaller.InstallComparisonEpoch, false)]
    [InlineData(PackageInstaller.InstallComparisonEpoch + 1, true)]
    public void OnlyARecordStampedByExactlyThisRule_IsABaseline(int? recordedEpoch, bool expectedNoBaseline)
        => PackageInstaller.RecordIsNotABaselineUnderThisRule(
                new PackageManifest { Id = "p", ComparisonEpoch = recordedEpoch })
            .Should().Be(expectedNoBaseline,
                "a record is a baseline only under exactly the rule this installer applies — "
                + "older AND newer rules both force a full compare");

    [Fact(Timeout = 300_000)]
    public async Task Control_UnderARecordStampedByTheCurrentRule_TheUpToDateExitIsTrusted()
    {
        var result = await ReinstallOverADriftedNode(restampEpoch: false, recordedEpoch: null,
            TestContext.Current.CancellationToken);

        result.Written.Should().Be(0,
            "the control: under a record stamped by the current rule the equal module hash is trusted "
            + "and nothing is compared or written — so the rewrite in the subject test comes from the "
            + "rule on the record, not from anything else in the sequence");
        var guide = await ReassertFixture.Read(Mesh, ReassertFixture.GuidePath);
        guide!.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)!.Content.Should().Be(Drifted,
            "nothing rewrote the drifted node");
    }

    /// <summary>
    /// Install, drift one node under an unchanged file hash, optionally re-stamp the record's
    /// comparison rule to <paramref name="recordedEpoch"/>, reinstall.
    /// </summary>
    private async Task<InstallResult> ReinstallOverADriftedNode(
        bool restampEpoch, int? recordedEpoch, System.Threading.CancellationToken ct)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<ARecordUnderAnOlderComparisonRuleIsNoBaselineTest>();

        await Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>()
            .Completed.FirstAsync().Timeout(180.Seconds()).Await(ct);

        await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, new ReassertFixture.Source(ReassertFixture.Files()), "HEAD",
                ReassertFixture.Candidate(), logger)
            .Should().Within(180.Seconds())
            .Emit("the first install must land before a drift can be planted", cancellationToken: ct);
        (await WaitForGuideBody(Packaged)).Should().BeTrue("the first install wrote the packaged node");

        var record = await ReassertFixture.ReadRecord(Mesh);
        record!.ComparisonEpoch.Should().Be(PackageInstaller.InstallComparisonEpoch,
            "a full install stamps the rule it compared under");

        // ── The drift: the stored node differs from the package, the package's file hash does not move.
        // Through the live stream surface, transforming the CURRENT node, never a whole earlier snapshot.
        var options = Mesh.JsonSerializerOptions;
        await access.RunAsSystem(() => Mesh.GetMeshNodeStream(ReassertFixture.GuidePath).Update(current => current with
            {
                Content = current.ContentAs<MarkdownContent>(options)! with { Content = Drifted },
            }))
            .Should().Within(60.Seconds()).Emit("the drift is the precondition", cancellationToken: ct);
        (await WaitForGuideBody(Drifted)).Should().BeTrue("the drift must be stored before the reinstall");

        if (restampEpoch)
        {
            // ── The record as another installer stamped it: before the rule was versioned (null), or
            // a pod on a later image mid-roll (current + 1). Computed from the LIVE manifest.
            await access.RunAsSystem(() => Mesh.GetMeshNodeStream(ReassertFixture.RecordPath).Update(current => current with
                {
                    Content = current.ContentAs<PackageManifest>(options)! with { ComparisonEpoch = recordedEpoch },
                }))
                .Should().Within(60.Seconds()).Emit("the re-stamped record is the precondition", cancellationToken: ct);
            (await WaitFor(ReassertFixture.RecordPath, n =>
                    n.ContentAs<PackageManifest>(options)?.ComparisonEpoch == recordedEpoch))
                .Should().BeTrue("the re-stamped record must be stored before the reinstall");
        }

        // ── The reinstall, at the SAME module hash — the 19:07Z shape.
        return await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, new ReassertFixture.Source(ReassertFixture.Files()), "HEAD",
                ReassertFixture.Candidate(), logger)
            .Should().Within(180.Seconds())
            .Emit("the reinstall must complete", cancellationToken: ct);
    }

    private Task<bool> WaitForGuideBody(string body) =>
        WaitFor(ReassertFixture.GuidePath,
            n => n.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content == body);

    /// <summary>Polls the authoritative store until <paramref name="predicate"/> holds; false on timeout.</summary>
    private Task<bool> WaitFor(string path, Func<MeshNode, bool> predicate) =>
        Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
                .Read(path, Mesh.JsonSerializerOptions)
                .Take(1)
                .DefaultIfEmpty(null))
            .Where(n => n is not null && predicate(n))
            .Select(_ => true)
            .FirstAsync()
            .Timeout(60.Seconds())
            .Catch<bool, TimeoutException>(_ => Observable.Return(false))
            .Await(TestContext.Current.CancellationToken);
}
