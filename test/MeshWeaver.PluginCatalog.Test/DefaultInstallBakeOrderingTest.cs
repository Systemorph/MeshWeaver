#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// 🚨 The #1114 ordering pin: on a host that BAKES its dynamic NodeTypes at boot (the
/// <c>DynamicTypePreWarmer</c> sweep), the default install must sequence AFTER the bake — never
/// race it.
///
/// <para>The production shape this pins: every self-update roll changes the framework hash, so
/// every dynamic NodeType is ABI-stale and the sweep rebuilds all ~240 sequentially (~10 min).
/// The boot install races that queue: each package's partition-ROOT upsert routes through the
/// owning per-node hub, whose activation parks on the root type's framework-stale rebuild —
/// queued behind the sweep — and the cross-hub <c>stream.Update</c> aborts at its 30 s
/// initial-state bound: <c>"no initial state arrived for '{package}' within 30s"</c>, for EVERY
/// default package (Agent, DataModelling, Edu, Essentials, Publish, Collaboration), on EVERY pod
/// boot. The failed packages keep their stale install records, so the next boot re-runs the
/// identical race — a portal permanently missing its platform plugins.</para>
///
/// <para>The contract: <see cref="PreWarmCompletion"/> (registered by
/// <c>AddDynamicTypePreWarming</c>, settled by the sweep's every terminal) is the barrier, and
/// <see cref="InstanceAutoRegistrationService"/> starts its boot pass only after it emits. A host
/// without the pre-warm registers no barrier and proceeds exactly as before — which every other
/// default-install test in this project exercises.</para>
/// </summary>
public class DefaultInstallBakeOrderingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly PreWarmCompletion preWarm = new();
    private readonly ProbeSource probe = new(Source());

    /// <summary>
    /// Records whether the boot pass has LISTED packages yet — listing is the install phase's very
    /// first touch of a source, so it flipping before the bake settles is the race, caught at its
    /// first observable step rather than at the eventual timeout.
    /// </summary>
    private sealed class ProbeSource(IPackageSource inner) : IPackageSource
    {
        public volatile bool Listed;

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef)
        {
            Listed = true;
            return inner.ListPackages(gitRef);
        }

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef) =>
            inner.FetchPackageFiles(package, gitRef);
    }

    // One pre-installed package — the platform-baseline shape the boot pass reconciles on every
    // start (same repo idiom as PreInstalledPackageInstallTest, pared to the ordering concern).
    private static readonly IReadOnlyList<RepoFile> Repo =
    [
        new("Seed/index.json",
            """
            {"$type":"MeshNode","id":"Seed","namespace":"","path":"Seed","mainNode":"Seed",
             "name":"Seed","nodeType":"Space","state":"Active",
             "content":{"$type":"PluginManifest","description":"Baseline package.",
                        "preInstalled":true}}
            """),
        new("Seed/Doc.json",
            """
            {"$type":"MeshNode","id":"Doc","namespace":"Seed","path":"Seed/Doc",
             "mainNode":"Seed/Doc","name":"Doc","nodeType":"Markdown","state":"Active",
             "content":"# Seed"}
            """),
    ];

    private static NodeRepoPackageSource Source()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-bake-ordering", Repo));
        return new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins");
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddPluginCatalog()
            .ConfigureServices(services => services
                // The host pre-warms: the bake barrier is registered UNSETTLED, exactly as
                // AddDynamicTypePreWarming registers it before the sweep has run.
                .AddSingleton(preWarm)
                .AddSingleton<IPackageSource>(probe));

    [Fact(Timeout = 180_000)]
    public async Task BootInstall_WaitsForTheBake_ThenInstalls()
    {
        var installer = Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

        // The boot pass WAS started (the test base starts every IHostedService with the mesh) —
        // but the bake has not settled, so it must not advance past the barrier: no source
        // listing, no completion. This is the sanctioned "wait to confirm nothing happened"
        // negative (WritingTests.md): there is no positive signal to filter for, because on the
        // gated ordering the chain is parked on the barrier's AsyncSubject and CANNOT produce
        // one — the window can never red a correct build. On the pre-#1114 (ungated) code the
        // chain reaches ListPackages within ~1 s of hosted-service start, so the poll fails FAST
        // the moment the race is observed rather than sitting out the window.
        var raced = await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .Select(_ => probe.Listed)
            .FirstAsync(listed => listed)
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<bool, TimeoutException>(_ => Observable.Return(false))
            .ToTask();
        raced.Should().BeFalse(
            "the default install must not even LIST packages before the bake settles — every "
            + "root it would go on to write parks on a type rebuild queued behind the sweep");
        // AsyncSubject replays synchronously on Subscribe, so this is a no-wait probe, not a sleep.
        var completedEarly = false;
        using (installer.Completed.Subscribe(_ => completedEarly = true))
            completedEarly.Should().BeFalse(
                "the default install must not finish before the bake settles");

        // The bake reaches a terminal — the barrier opens and the already-subscribed boot pass
        // proceeds on its own: no re-kick, no poll. The terminal now CARRIES its outcome; the
        // installer deliberately does not branch on it (see InstanceAutoRegistrationService — a
        // faulted bake must still install, because installs repair content), so the ordering
        // contract this test pins is the same for every settlement.
        preWarm.MarkSettled(PreWarmSettlement.Completed);

        var summary = await installer.Completed
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();

        summary.Packages.Should().Contain("Seed");
        summary.Failed.Should().Be(0);
        probe.Listed.Should().BeTrue("once the bake settled, the pass must actually run");

        // …and the gated pass still delivers: the install record and the content landed.
        var record = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read($"{PackageInstaller.InstalledPartition}/Seed", Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask();
        record.Should().NotBeNull(
            "waiting for the bake must delay the baseline install, never lose it");
    }

    /// <summary>
    /// 🚨 A FAULTED BAKE STILL INSTALLS — the deliberate null result of giving the barrier an
    /// outcome.
    ///
    /// <para><see cref="PreWarmCompletion"/> now carries a <see cref="PreWarmSettlement"/> so a
    /// consumer CAN branch on it. This one must not, and that is worth pinning rather than leaving
    /// to be "tidied up" later into a plausible-looking guard:</para>
    /// <list type="bullet">
    ///   <item>The ordering exists because the SWEEP saturates the compile queue and parks per-node
    ///     hub activations behind it. A sweep that faulted never built that queue, so there is
    ///     nothing left to wait for.</item>
    ///   <item>Withholding the install would be backwards. Installs REPAIR content, and the boot
    ///     where the bake could not run is exactly the boot where an instance is most likely to be
    ///     missing its defaults — skipping it would turn one bad boot into a portal with no
    ///     plugins, which is the very outcome #1114 fixed.</item>
    ///   <item>Readiness is the bake gate's call (<c>NodeTypeBakeGateState</c>), and it already
    ///     refuses. The installer must not become a second, quieter gate — the same reasoning the
    ///     pre-warmer records for a Regressed+armed pod.</item>
    /// </list>
    /// <para>So the settlement changes what is LOGGED, never whether the pass runs.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task BootInstall_StillRuns_WhenTheBakeFaulted()
    {
        var installer = Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

        // The sweep errored — it verified nothing. The barrier opens all the same.
        preWarm.MarkSettled(PreWarmSettlement.Faulted);

        var summary = await installer.Completed
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();

        summary.Packages.Should().Contain("Seed",
            "an unproven bake must not withhold the default install — installs repair content, and "
            + "refusing to run one is how a single bad boot becomes a portal with no plugins");
        summary.Failed.Should().Be(0);
        probe.Listed.Should().BeTrue();

        var record = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read($"{PackageInstaller.InstalledPartition}/Seed", Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask();
        record.Should().NotBeNull(
            "the install must actually land, not merely be attempted");
    }
}
