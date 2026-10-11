#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>MeshWeaver#5825 — a default package the boot could not install is installed when a registry
/// next answers, not at the next boot.</b>
///
/// <para>Measured on the control instance, 2026-09-27: the boot's fetch of <c>Anthropic</c> stalled
/// on an HTTPS read, the package was recorded as FAILED, and the only repair was "the next boot
/// re-attempts it". On an instance that restarts when its image rolls, that is however long the
/// next roll takes.</para>
///
/// <para>The fixture is the real path end to end: the boot default-install pass with one package
/// whose file fetch fails the way production saw it, a registry this installation is a consumer of
/// (the fake HTTP registry of <see cref="ModuleReloadScenario"/>), and
/// <see cref="RegistryUpdateReconciler.ReconcileNow"/> — the same pass a safety-net tick runs — as
/// the feed read that prompts the repair. Nothing in the test calls the installer.</para>
/// </summary>
public class AFailedDefaultInstallIsRepairedWhenTheFeedAnswersTest(ITestOutputHelper output)
    : ModuleReloadScenario(output)
{
    private const string SourceName = "registered-0";
    private const string LedgerPath = "Plugins/_DefaultInstallLedger";

    /// <summary>Whether <c>Flaky</c>'s fetch fails. Flipped by the test; read on a pool thread.</summary>
    private volatile bool flakyFails = true;

    private int listings;

    /// <summary>How often the default-install source was LISTED — one per pass.</summary>
    private int Listings => Volatile.Read(ref listings);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .AddSingleton<IPackageSource>(_ => new FlakyCatalog(
                    new NodeRepoPackageSource(
                        (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-repair", Repo)),
                        "https://github.com/acme/plugins"),
                    () => flakyFails,
                    () => Interlocked.Increment(ref listings)))
                // Registered AFTER the scenario's own options, so this is the instance resolved: the
                // same registry, plus a default-install pattern over the flaky source.
                .AddSingleton(new PluginCatalogOptions
                {
                    Registries = [new PluginRegistryReference { Name = "test", Url = RegistryUrl, Token = Token }],
                    InstallByDefault = [$"{SourceName}/*"],
                    InstallPreInstalledPackages = false,
                    // The safety net's own timer is out of the test: ReconcileNow IS its pass.
                    ReconcileSafetyNetInterval = TimeSpan.Zero,
                }));

    private static readonly ImmutableArray<RepoFile> Repo =
    [
        new("Good/index.json",
            """{"$type":"MeshNode","id":"Good","namespace":"","path":"Good","mainNode":"Good","name":"Good","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Installs fine.","minMeshVersion":"1.0.0"}}"""),
        new("Good/Page.json",
            """{"$type":"MeshNode","id":"Page","namespace":"Good","path":"Good/Page","mainNode":"Good/Page","name":"Page","nodeType":"Markdown","state":"Active","content":"# Good"}"""),
        new("Flaky/index.json",
            """{"$type":"MeshNode","id":"Flaky","namespace":"","path":"Flaky","mainNode":"Flaky","name":"Flaky","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Lands on the repair.","minMeshVersion":"1.0.0"}}"""),
        new("Flaky/Page.json",
            """{"$type":"MeshNode","id":"Page","namespace":"Flaky","path":"Flaky/Page","mainNode":"Flaky/Page","name":"Page","nodeType":"Markdown","state":"Active","content":"# Flaky"}"""),
    ];

    /// <summary>The production shape of the failure: a pipeline timeout wrapping the stalled socket read.</summary>
    private static Exception RegistryStall() =>
        new TimeoutException(
            "The operation didn't complete within the allowed timeout of '00:01:30'.",
            new System.IO.IOException(
                "Unable to read data from the transport connection: Operation canceled.",
                new SocketException((int)SocketError.OperationAborted)));

    private sealed class FlakyCatalog(NodeRepoPackageSource inner, Func<bool> fails, Action listed) : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef)
            => Observable.Defer(() =>
            {
                listed();
                return inner.ListPackages(gitRef);
            });

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef)
            => package.Id == "Flaky" && fails()
                ? Observable.Throw<IReadOnlyList<PackageFile>>(RegistryStall())
                : inner.FetchPackageFiles(package, gitRef);
    }

    private InstanceAutoRegistrationService Installer =>
        Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

    private RegistryUpdateReconciler Reconciler =>
        Mesh.ServiceProvider.GetRequiredService<RegistryUpdateReconciler>();

    /// <summary>The boot pass has failed <c>Flaky</c> and the boot reconcile is over.</summary>
    private async Task<DefaultInstallSummary> BootedWithFlakyFailed(CancellationToken ct)
    {
        var boot = await Installer.Completed.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"boot: {boot}");
        boot.Failures.Should().Equal(["Flaky"], "the premise: the boot could not install it");
        boot.Delivered.Should().Contain("Good");
        (await Ledger(l => l.Failed.Contains("Flaky"), ct)).Seeded.Should().NotContain("Flaky");
        await Reconciler.BootReconciled.DefaultIfEmpty().Timeout(TestTimeouts.Convergence).Await(ct);
        // 🚨 The boot reconcile itself must NOT repair: it runs straight after the pass that failed,
        // against the same registry state. Read here, before any baseline is taken — a repair pass
        // inside the boot would otherwise be absorbed into "afterBoot" and every later +1 would hold.
        Listings.Should().Be(1, "the source is listed by the boot pass alone; the boot reconcile runs no repair pass");
        return boot;
    }

    [Fact(Timeout = 240_000)]
    public async Task TheNextReconcileAgainstAnAnsweringRegistry_InstallsWhatTheBootCouldNot()
    {
        var ct = TestContext.Current.CancellationToken;
        await BootedWithFlakyFailed(ct);
        var afterBoot = Listings;

        // ── the registry answers, the package source still stalls: re-attempted, still recorded ──
        await Reconciler.ReconcileNow().Timeout(TestTimeouts.Convergence).Await(ct);
        Listings.Should().Be(afterBoot + 1, "a reconcile after a failed pass runs the default install again, once");
        (await Ledger(l => l.Failed.Contains("Flaky"), ct)).Seeded.Should().NotContain("Flaky",
            "a package that fails again stays recorded as failed, never as delivered");

        // ── the source answers too: the same reconcile installs it, with no boot in between ──────
        flakyFails = false;
        await Reconciler.ReconcileNow().Timeout(TestTimeouts.Convergence).Await(ct);
        var healed = await Ledger(l => l.Seeded.Contains("Flaky"), ct);
        healed.Failed.Should().NotContain("Flaky");
        healed.FailureCauses.Should().BeEmpty();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        (await storage.Read("Flaky/Page", Mesh.JsonSerializerOptions).Take(1)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().NotBeNull("the package's content is in the mesh");
        var afterRepair = Listings;
        afterRepair.Should().Be(afterBoot + 2);

        // ── nothing left undone: a further reconcile does not run the default install at all ─────
        await Reconciler.ReconcileNow().Timeout(TestTimeouts.Convergence).Await(ct);
        Listings.Should().Be(afterRepair, "a pass that left nothing undone is not repeated");
    }

    /// <summary>The negative control of the repair: a boot that installed everything is followed by
    /// no second pass, however often the registry is read.</summary>
    [Fact(Timeout = 240_000)]
    public async Task ACleanBoot_IsNeverFollowedByARepairPass()
    {
        var ct = TestContext.Current.CancellationToken;
        flakyFails = false;
        var boot = await Installer.Completed.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        boot.Failures.Should().BeEmpty();
        await Reconciler.BootReconciled.DefaultIfEmpty().Timeout(TestTimeouts.Convergence).Await(ct);
        var afterBoot = Listings;

        await Reconciler.ReconcileNow().Timeout(TestTimeouts.Convergence).Await(ct);
        await Reconciler.ReconcileNow().Timeout(TestTimeouts.Convergence).Await(ct);

        Listings.Should().Be(afterBoot);
    }

    /// <summary>What counts as "left undone". Pure.</summary>
    [Fact]
    public void LeftUndone_NamesFailuresAndAnUnlistedSource_AndIsNullForACleanPass()
    {
        InstanceAutoRegistrationService.LeftUndone(DefaultInstallSummary.Empty).Should().BeNull();
        InstanceAutoRegistrationService.LeftUndone(new DefaultInstallSummary(3, 1, 0, ["A"])).Should().BeNull();
        InstanceAutoRegistrationService.LeftUndone(
                new DefaultInstallSummary(0, 0, 1, ["A"]) { Failures = ["Anthropic"] })
            .Should().Contain("1 package(s) failed").And.Contain("Anthropic");
        InstanceAutoRegistrationService.LeftUndone(DefaultInstallSummary.Empty with { ListingIncomplete = true })
            .Should().Be("a source could not be listed",
                "a package a failed listing never named is neither failed nor installed, and is owed all the same");
    }

    private Task<DefaultInstallLedger> Ledger(Func<DefaultInstallLedger, bool> condition, CancellationToken ct)
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        return Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(LedgerPath, Mesh.JsonSerializerOptions).Take(1))
            .Select(node => node?.ContentAs<DefaultInstallLedger>(Mesh.JsonSerializerOptions))
            .OfType<DefaultInstallLedger>()
            .Where(condition)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
    }
}
