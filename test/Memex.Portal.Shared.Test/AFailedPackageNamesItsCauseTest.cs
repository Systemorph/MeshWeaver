using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 A default package the boot could not deliver is NAMED WITH ITS CAUSE — on the pass's summary
/// line and on <c>Plugins/_DefaultInstallLedger</c> — and the cause leaves the ledger the moment a
/// later pass delivers the package (MeshWeaver#5826).
///
/// <para>Measured on memex.systemorph.com, 2026-09-27/28: the boot summary said
/// <c>FAILED: [Anthropic]</c>, then <c>FAILED: [AppleIntelligence]</c>, and the ledger said
/// <c>failed: ["Anthropic"]</c>. The cause — a registry file fetch whose three 30 s attempts all
/// expired waiting for response headers — lived only in a separate per-package log line, so the
/// triage of the summary could not say why, and the ticket read as "persistently fails" when each
/// boot had failed a different package for a reason it could have named.</para>
///
/// <para>The failure is injected the way production saw it: a Polly-style timeout WRAPPING the
/// socket cancellation, so the assertion also pins that the innermost cause is carried.</para>
/// </summary>
public class AFailedPackageNamesItsCauseTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SourceName = "registered-0";
    private const string LedgerPath = "Plugins/_DefaultInstallLedger";

    /// <summary>Whether <c>Flaky</c>'s fetch fails. Flipped by the test between passes; volatile
    /// because the install reads it on a pool thread.</summary>
    private volatile bool flakyFails = true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton<IPackageSource>(_ => new FlakyCatalog(
                    new NodeRepoPackageSource(
                        (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-cause", Repo)),
                        "https://github.com/acme/plugins"),
                    () => flakyFails))
                .AddSingleton(new PluginCatalogOptions
                {
                    InstallByDefault = [$"{SourceName}/*"],
                    InstallPreInstalledPackages = false,
                }));

    private static readonly IReadOnlyList<RepoFile> Repo =
    [
        new("Good/index.json",
            """{"$type":"MeshNode","id":"Good","namespace":"","path":"Good","mainNode":"Good","name":"Good","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Installs fine.","minMeshVersion":"1.0.0"}}"""),
        new("Good/Page.json",
            """{"$type":"MeshNode","id":"Page","namespace":"Good","path":"Good/Page","mainNode":"Good/Page","name":"Page","nodeType":"Markdown","state":"Active","content":"# Good"}"""),
        new("Flaky/index.json",
            """{"$type":"MeshNode","id":"Flaky","namespace":"","path":"Flaky","mainNode":"Flaky","name":"Flaky","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Lands on the second pass.","minMeshVersion":"1.0.0"}}"""),
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

    /// <summary>Lists both packages; fails <c>Flaky</c>'s file fetch while <paramref name="fails"/> says so.</summary>
    private sealed class FlakyCatalog(NodeRepoPackageSource inner, Func<bool> fails) : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef)
            => inner.ListPackages(gitRef);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef)
            => package.Id == "Flaky" && fails()
                ? Observable.Throw<IReadOnlyList<PackageFile>>(RegistryStall())
                : inner.FetchPackageFiles(package, gitRef);
    }

    [Fact(Timeout = 180_000)]
    public async Task AFailedPackage_IsNamedWithItsCause_AndTheCauseLeavesWhenItLands()
    {
        var installer = Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

        // PASS 1 — Flaky's fetch stalls.
        var first = await installer.Completed
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"pass 1: {first}");

        first.Failures.Should().Equal("Flaky");
        var cause = first.FailureCauses.Should().ContainSingle(
            "every failed package carries exactly one cause").Which;
        cause.Package.Should().Be("Flaky");
        cause.Cause.Should().Contain("TimeoutException")
            .And.Contain("00:01:30", "the outer cause names the budget that expired")
            .And.Contain("SocketException", "the innermost cause names what actually stalled");
        first.ToString().Should().Contain("CAUSES: [Flaky: TimeoutException",
            "the summary line a reader triages from must say WHY, not only WHICH");

        var ledger = await Ledger(l => l.Failed.Contains("Flaky"));
        ledger.FailureCauses.Should().ContainSingle()
            .Which.Should().Be(cause, "the ledger records the cause the pass saw, verbatim");

        // PASS 2 — the registry answers now. The failed package is retried (it is known to the
        // ledger, so the governed-provision hold does not take it) and lands; its cause goes.
        flakyFails = false;
        var second = await installer.RunDefaultInstall()
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"pass 2: {second}");

        second.Delivered.Should().Contain("Flaky", "the next pass is the retry, and this time it delivers");
        second.FailureCauses.Should().BeEmpty();
        var healed = await Ledger(l => !l.Failed.Contains("Flaky"));
        healed.FailureCauses.Should().BeEmpty(
            "a cause is a snapshot of what is missing NOW — a delivered package must not stay advertised as failing");
        healed.Seeded.Should().Contain("Flaky");
    }

    /// <summary>The cause is bounded and keeps the innermost exception. Pure.</summary>
    [Fact]
    public void ACause_KeepsTheInnermostException_AndIsBounded()
    {
        var cause = DefaultInstallFailure.Of("Anthropic", RegistryStall());
        cause.Cause.Should().StartWith("TimeoutException: The operation didn't complete");
        cause.Cause.Should().Contain("← SocketException");

        var huge = DefaultInstallFailure.Of("Big", new InvalidOperationException(new string('x', 5_000)));
        huge.Cause.Length.Should().BeLessThanOrEqualTo(DefaultInstallFailure.MaxCauseLength + 1);
        huge.Cause.Should().EndWith("…");

        var flat = DefaultInstallFailure.Of("Flat", new InvalidOperationException("no inner"));
        flat.Cause.Should().Be("InvalidOperationException: no inner", "no inner exception, no arrow");
    }

    /// <summary>
    /// The ledger as stored, once it satisfies <paramref name="condition"/> — read off storage (never
    /// the lagging index) and re-read until the condition holds, since the write may still be in
    /// flight when the pass's summary is emitted.
    /// </summary>
    private Task<DefaultInstallLedger> Ledger(Func<DefaultInstallLedger, bool> condition)
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        return Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(LedgerPath, Mesh.JsonSerializerOptions).Take(1))
            .Select(node => node?.ContentAs<DefaultInstallLedger>(Mesh.JsonSerializerOptions))
            .Where(ledger => ledger is not null && condition(ledger))
            .Select(ledger => ledger!)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
    }
}
