using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence.Parsers;
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
/// 🚨 <b>THE SWEEP'S VERDICT NEEDS A CONSUMER THAT ACTS</b> — MeshWeaver#4812.
///
/// <para>#3485 put the completeness sweep into the boot repair pass (the one complete inventory of
/// what is installed) and the heal into <c>CatalogLayoutAreas.InstallOrUpdate</c>'s up-to-date
/// exit — and nothing connected the two. The funnel runs only for a package some lane VISITS:
/// the platform baseline and the environment's flags on every boot, the operator's seed once, the
/// update reconciler only when the module hash MOVED (an equal hash returns before the funnel),
/// and a human's click. A package installed by hand from a source that has not moved since is
/// visited by nothing, so the sweep's own line — "Reinstalling it now repairs it" — described a
/// click nobody made, on every boot, for weeks.</para>
///
/// <para>This fixture is that shape: a package installed through the funnel (not the boot lane, so
/// no lane re-asserts it), one declared node lost, the source unchanged. The REPAIR PASS — the
/// production <see cref="InstalledPackageRepairService.RunRepair"/> — must hand the shortfall to
/// the install lane and the node must come back. On <c>main</c> the pass reports at Error and the
/// node stays gone.</para>
/// </summary>
public class IncompleteInstallIsReassertedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                // The source the install came from, REGISTERED so the repair's re-assert can list
                // it at its ref (the boot lane names a DI source `registered-0`). No baseline, no
                // pattern: the boot pass itself selects nothing, which is exactly the point — the
                // package below is visited by no lane but the repair.
                .AddSingleton<IPackageSource>(_ => new ReassertFixture.Source(ReassertFixture.Files()))
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false }));

    private InstanceAutoRegistrationService Installer =>
        Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

    private InstalledPackageRepairService Repair =>
        Mesh.ServiceProvider.GetRequiredService<InstalledPackageRepairService>();

    [Fact(Timeout = 300_000)]
    public async Task ALostNodeOfAHandInstalledPackage_ComesBackOnTheNextRepairPass()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<IncompleteInstallIsReassertedTest>();

        // The boot pass has nothing to install here and must have SAID so before the repair can
        // sequence after it — the heal waits on Completed by design.
        await Installer.Completed.FirstAsync().Timeout(180.Seconds()).Await(ct);

        // A hand install through the funnel — the catalog click's path — at the source's ref.
        await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, new ReassertFixture.Source(ReassertFixture.Files()), "HEAD",
                ReassertFixture.Candidate(), logger)
            .Should().Within(180.Seconds())
            .Emit("the install must land before a loss can be measured", cancellationToken: ct);
        (await ReassertFixture.WaitForNode(Mesh, ReassertFixture.GuidePath, present: true)).Should().BeTrue(
            "the Guide node is the subject — if the install never wrote it, the assertion below "
            + "would pass for the wrong reason");

        var before = await ReassertFixture.ReadRecord(Mesh);
        before.Should().NotBeNull("the installer stamps an install record");
        before!.ModuleVersion.Should().Be(ReassertFixture.ModuleHash,
            "the source serves the same module version the record carries — the premise: nothing "
            + "about the SOURCE changed, so no update lane will ever visit this package");

        // The loss.
        await meshService.DeleteNode(ReassertFixture.GuidePath)
            .Should().Within(60.Seconds()).Emit("the deletion is the precondition", cancellationToken: ct);
        (await ReassertFixture.WaitForNode(Mesh, ReassertFixture.GuidePath, present: false)).Should().BeTrue(
            "the node must actually be gone before the repair runs");

        // The boot repair pass, exactly as the next process start runs it.
        await Repair.RunRepair().Timeout(240.Seconds()).Await(ct);

        (await ReassertFixture.WaitForNode(Mesh, ReassertFixture.GuidePath, present: true)).Should().BeTrue(
            "THE assertion: the repair pass hands an incomplete install to the install lane, which "
            + "re-observes and heals it. On main the pass only REPORTS — the sweep named "
            + "ClaimsDeepfield's shortfall at Error on every boot for three weeks while nothing "
            + "ran the lane its own line pointed at (MeshWeaver#4812)");
        (await ReassertFixture.WaitForNode(Mesh, ReassertFixture.OtherPath, present: true)).Should().BeTrue(
            "the control: the node that was never lost is still there");

        var after = await ReassertFixture.ReadRecord(Mesh);
        after.Should().NotBeNull();
        after!.ModuleVersion.Should().Be(ReassertFixture.ModuleHash,
            "a re-assert is a HEAL at the recorded module version, never an update");
    }
}

/// <summary>
/// 🚨 <b>THE CONTROL: a partition DELETED after its install is never reinstalled unattended</b> —
/// the other half of MeshWeaver#4812, and the shape the issue was actually opened on.
///
/// <para><c>Plugins/ClaimsDeepfield</c> on memex.meshweaver.cloud was installed 2026-08-27; the
/// module was retired from its repository on 09-04 and the Space deleted on 09-05T09:03Z, before
/// the record-follows-partition handler (#3451) existed — so the record survived. Thirteen minutes
/// later the boot pass re-asserted the record's declared access, which is create-only and CREATED
/// the root and its <c>_Policy</c> (<c>createdBy: system-security</c>, 09:16:50Z). From then on
/// the partition read as "present" and the sweep counted <i>82 of 83 declared node(s) are
/// ABSENT — reinstalling it now repairs it</i>, at Error, on every boot: a recommendation to
/// resurrect a retired module.</para>
///
/// <para>With the heal above in place, that record would be exactly what gets re-asserted — unless
/// the sweep can tell a DELETED partition from a DAMAGED one. It can: the root is YOUNGER than the
/// install that declared it, and nothing else the install wrote is there
/// (<see cref="InstallCompletenessKind.TornDown"/>). This fixture re-enacts the incident — the
/// residue record, the create-only re-assert that resurrects the root — and asserts the repair pass
/// leaves it alone and says why.</para>
/// </summary>
public class ATornDownPartitionIsNeverReinstalledUnattendedTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton<IPackageSource>(_ => new ReassertFixture.Source(ReassertFixture.Files()))
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false }));

    private InstanceAutoRegistrationService Installer =>
        Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

    private InstalledPackageRepairService Repair =>
        Mesh.ServiceProvider.GetRequiredService<InstalledPackageRepairService>();

    [Fact(Timeout = 300_000)]
    public async Task ARecordThatOutlivedItsPartition_IsNamedTornDown_AndNotReinstalled()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<ATornDownPartitionIsNeverReinstalledUnattendedTest>();

        await Installer.Completed.FirstAsync().Timeout(180.Seconds()).Await(ct);

        await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, new ReassertFixture.Source(ReassertFixture.Files()), "HEAD",
                ReassertFixture.Candidate(), logger)
            .Should().Within(180.Seconds())
            .Emit("the install must land before a deletion can be measured", cancellationToken: ct);
        (await ReassertFixture.WaitForNode(Mesh, ReassertFixture.GuidePath, present: true)).Should().BeTrue();
        var record = await ReassertFixture.ReadRecord(Mesh);
        record.Should().NotBeNull();
        record!.InstalledAtUtc.Should().NotBeNull("the install stamp is half of the discriminator");

        // ── The operator retires the space: the partition root goes, recursively. The
        // record-follows-partition handler (#3451) removes the record with it — which is the
        // FORWARD fix; the incident predates it.
        await meshService.DeleteNode(ReassertFixture.Package)
            .Should().Within(60.Seconds()).Emit("the partition delete is the precondition", cancellationToken: ct);
        (await ReassertFixture.WaitForNode(Mesh, ReassertFixture.Package, present: false)).Should().BeTrue(
            "the root must be gone");
        (await ReassertFixture.WaitForNode(Mesh, ReassertFixture.RecordPath, present: false)).Should().BeTrue(
            "#3451 removes the record with its partition — the residue below is re-created "
            + "deliberately, as a record written before that handler existed");

        // ── The residue: the record as it was, outliving its partition (pre-#3451 state).
        var residue = MeshNode.FromPath(ReassertFixture.RecordPath) with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = ReassertFixture.Package,
            State = MeshNodeState.Active,
            Content = record,
        };
        await access.RunAsSystem(() => meshService.CreateNode(residue))
            .Should().Within(60.Seconds()).Emit("the residue record is the precondition", cancellationToken: ct);
        (await ReassertFixture.WaitForNode(Mesh, ReassertFixture.RecordPath, present: true)).Should().BeTrue();

        // ── The resurrection. In production the NEXT boot's access re-assert on the surviving
        // record created the root — a fresh pod has no memory of the delete. In ONE process the
        // #3451 recently-deleted registry refuses to heal a root it just saw go ("NOT healing
        // partition … it was just deleted"), which is the forward fix doing its job. So the end
        // state the incident left — a content-free root YOUNGER than the install, with the record
        // beside it — is written here as the deliberate create that clears that registry, and the
        // access re-assert then lands the policy exactly as it did at 09:16:50Z.
        await access.RunAsSystem(() => meshService.CreateNode(new MeshNode(ReassertFixture.Package)
            {
                NodeType = "Space",
                Name = ReassertFixture.Package,
                State = MeshNodeState.Active,
            }))
            .Should().Within(60.Seconds()).Emit("the re-created root is the precondition", cancellationToken: ct);
        await access.RunAsSystem(() => PackageInstaller.EnsureDeclaredAccess(
                Mesh, record, ReassertFixture.Package, logger))
            .Should().Within(60.Seconds()).Emit("the access re-assert is the precondition", cancellationToken: ct);
        (await ReassertFixture.WaitForNode(Mesh, ReassertFixture.Package, present: true)).Should().BeTrue(
            "the root is back — the shape the incident measured");
        var root = await ReassertFixture.Read(Mesh, ReassertFixture.Package);
        root!.CreatedDate.Should().BeAfter(record.InstalledAtUtc!.Value,
            "the resurrected root is YOUNGER than the install that declared it — the discriminator");

        // ── The boot repair pass. It must name the shape and leave it alone.
        await Repair.RunRepair().Timeout(240.Seconds()).Await(ct);

        (await ReassertFixture.Read(Mesh, ReassertFixture.GuidePath)).Should().BeNull(
            "THE assertion: a partition deleted after its install is NOT reinstalled by a boot pass "
            + "— that would resurrect a module an operator retired (MeshWeaver#4812)");
        (await ReassertFixture.Read(Mesh, ReassertFixture.RecordPath)).Should().NotBeNull(
            "and the pass does not delete the record on a heuristic either — the admin orphan "
            + "list is the remedy, and the line says so");

        // The verdict itself, through the same observation the sweep uses.
        var verdict = await InstallCompleteness.Observe(
                Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>(), Mesh.JsonSerializerOptions,
                ReassertFixture.Package, ReassertFixture.Package, record,
                new FileFormatParserRegistry(Mesh.JsonSerializerOptions,
                    Mesh.ServiceProvider.GetServices<IFileFormatParser>()),
                recordIdentity: null)
            .Timeout(60.Seconds()).Await(ct);
        verdict.Kind.Should().Be(InstallCompletenessKind.TornDown,
            "the root is younger than the install and nothing else the install wrote is present");
        verdict.Because.Should().Contain("orphaned install records",
            "the line names the remedy, not a reinstall");
    }
}

/// <summary>The pure discriminator — every arm pinnable without a mesh.</summary>
public class TornDownVerdictTest
{
    private const string Package = "TornPkg";
    private static readonly DateTimeOffset Installed = new(2026, 8, 27, 19, 48, 39, TimeSpan.Zero);

    private static PackageManifest Record() => new()
    {
        Id = Package,
        Kind = PackageKind.NodeRepo,
        TargetPartition = Package,
        ModuleVersion = "d9718de1c34ee4e5",
        InstalledAtUtc = Installed,
        InstalledFiles = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, new[]
        {
            KeyValuePair.Create($"{Package}/index.json", "a"),
            KeyValuePair.Create($"{Package}/Agent.md", "b"),
            KeyValuePair.Create($"{Package}/Cedent.json", "c"),
        }),
    };

    private static IReadOnlySet<string> Present(params string[] paths) =>
        paths.ToImmutableHashSet(StringComparer.Ordinal);

    [Fact]
    public void ARootYoungerThanTheInstall_WithNothingElsePresent_IsTornDown()
    {
        var verdict = InstallCompleteness.Compare(
            Package, Package, Record(), Present(Package), PackageInstaller.BuiltInParsers,
            rootCreated: Installed.AddDays(9));
        verdict.Kind.Should().Be(InstallCompletenessKind.TornDown);
        verdict.Declared.Should().Be(3);
        verdict.Present.Should().Be(1, "the resurrected root is the one declared node present");
        verdict.Missing.Should().Equal($"{Package}/Agent", $"{Package}/Cedent");
        verdict.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void ARootFromTheInstall_WithNodesLostBesideIt_IsIncomplete()
    {
        // The root dates from the install (or before it): a genuine loss, to be healed.
        InstallCompleteness.Compare(
                Package, Package, Record(), Present(Package), PackageInstaller.BuiltInParsers,
                rootCreated: Installed.AddSeconds(-1))
            .Kind.Should().Be(InstallCompletenessKind.Incomplete);
    }

    [Fact]
    public void AYoungRootWithSurvivingContent_IsIncomplete_NotTornDown()
    {
        // The #638 shape: a root row lost and re-created while the partition's content lived on.
        // Content beside the root means the install left a trace — a loss, never a deletion.
        InstallCompleteness.Compare(
                Package, Package, Record(), Present(Package, $"{Package}/Agent"),
                PackageInstaller.BuiltInParsers, rootCreated: Installed.AddDays(9))
            .Kind.Should().Be(InstallCompletenessKind.Incomplete);
    }

    [Fact]
    public void AnUnknownStampOnEitherSide_NeverInventsADeletion()
    {
        InstallCompleteness.Compare(
                Package, Package, Record(), Present(Package), PackageInstaller.BuiltInParsers,
                rootCreated: null)
            .Kind.Should().Be(InstallCompletenessKind.Incomplete, "no root stamp");
        InstallCompleteness.Compare(
                Package, Package, Record() with { InstalledAtUtc = null }, Present(Package),
                PackageInstaller.BuiltInParsers, rootCreated: Installed.AddDays(9))
            .Kind.Should().Be(InstallCompletenessKind.Incomplete, "no install stamp");
    }

    /// <summary>The enum is public: a member inserted mid-list renumbers every later one for a
    /// precompiled dependent or an integer-serialised verdict, so the existing values are pinned and
    /// the new member is last (review on #4985).</summary>
    [Fact]
    public void TheNewKindIsAppended_SoExistingValuesKeepTheirNumbers()
    {
        ((int)InstallCompletenessKind.Complete).Should().Be(0);
        ((int)InstallCompletenessKind.Incomplete).Should().Be(1);
        ((int)InstallCompletenessKind.Undeclared).Should().Be(2);
        ((int)InstallCompletenessKind.NotObserved).Should().Be(3);
        ((int)InstallCompletenessKind.RootWithoutRecord).Should().Be(4);
        ((int)InstallCompletenessKind.TornDown).Should().Be(5);
    }

    [Fact]
    public void TheSummaryCountsTornDownOnItsOwn_AndTheLandingIsAnError()
    {
        var torn = InstallCompleteness.Compare(
            Package, Package, Record(), Present(Package), PackageInstaller.BuiltInParsers,
            rootCreated: Installed.AddDays(9));
        var summary = InstallCompleteness.Summarize([torn]);
        summary.TornDown.Should().Be(1);
        summary.Incomplete.Should().Be(0);
        summary.Total.Should().Be(1);
        summary.ToString().Should().Contain("1 torn down");
        InstallCompleteness.DescribeLanding(torn, "d9718de1c34ee4e5").Level.Should().Be(LogLevel.Error,
            "an install that just WROTE and still reads torn down is a landing failure");
    }
}

/// <summary>One package, three declared nodes, served by a fixed source — shared by both fixtures.</summary>
internal static class ReassertFixture
{
    public const string Package = "ReassertPkg";
    public const string GuidePath = $"{Package}/Guide";
    public const string OtherPath = $"{Package}/Other";
    public const string ModuleHash = "4812481248124812";
    public static readonly string RecordPath = $"{PackageInstaller.InstalledPartition}/{Package}";

    private const string ManifestLock = $$"""
        {
          "module": "{{Package}}",
          "moduleVersion": "{{ModuleHash}}",
          "version": "1.0.0",
          "files": {
            "{{Package}}/index.json": "aaa",
            "{{Package}}/Guide.md": "bbb",
            "{{Package}}/Other.md": "ccc"
          }
        }
        """;

    public static PackageManifest Candidate() => new()
    {
        Id = Package,
        Name = Package,
        Kind = PackageKind.NodeRepo,
        TargetPartition = Package,
        SourceFolder = Package,
        Version = "1.0.0",
        ModuleVersion = ModuleHash,
    };

    public static IReadOnlyList<PackageFile> Files() =>
    [
        new PackageFile($"{Package}/{ModuleManifest.FileName}", ManifestLock),
        new PackageFile($"{Package}/index.json", $$"""
            {
              "id": "{{Package}}",
              "path": "{{Package}}",
              "nodeType": "Space",
              "name": "Re-assert package",
              "state": "Active"
            }
            """),
        new PackageFile($"{Package}/Guide.md", "# Guide\n\nThe node the tests lose."),
        new PackageFile($"{Package}/Other.md", "# Other\n\nThe node that stays, as the control."),
    ];

    public sealed class Source(IReadOnlyList<PackageFile> files) : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return<IReadOnlyList<PackageManifest>>([Candidate()]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef) => Observable.Return(files);
    }

    /// <summary>Authoritative single read off storage (never the lagging index); null when absent.</summary>
    public static Task<MeshNode?> Read(IMessageHub mesh, string path) =>
        mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, mesh.JsonSerializerOptions)
            .Take(1)
            .DefaultIfEmpty(null)
            .Timeout(60.Seconds())
            .Await(TestContext.Current.CancellationToken);

    public static async Task<PackageManifest?> ReadRecord(IMessageHub mesh) =>
        (await Read(mesh, RecordPath))?.ContentAs<PackageManifest>(mesh.JsonSerializerOptions);

    public static Task<bool> WaitForNode(IMessageHub mesh, string path, bool present) =>
        Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
                .Read(path, mesh.JsonSerializerOptions)
                .Take(1)
                .DefaultIfEmpty(null)
                .Select(n => n is not null)
                .Catch<bool, Exception>(ex => Observable.Throw<bool>(new InvalidOperationException(
                    $"reading '{path}' from storage FAILED, so neither its presence nor its absence "
                    + "was observed.", ex))))
            .Where(found => found == present)
            .Select(_ => true)
            .FirstAsync()
            .Timeout(120.Seconds())
            .Await(TestContext.Current.CancellationToken);
}
