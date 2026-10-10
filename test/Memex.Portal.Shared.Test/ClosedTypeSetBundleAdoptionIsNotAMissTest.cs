#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A closed type set has nothing to adopt, and that is not a miss.</b> Measured on the
/// control instance (control.systemorph.com <c>/health</c>, 2026-10-09):
/// <c>bundle_adoption: Degraded — 27 adoption attempt(s), 0 assembly/assemblies adopted …, 14
/// MISS(es) — … Edu: adopted only 0/12 — the rest compile here; Essentials: adopted only 0/5 …</c>.
/// Every one of the 0/N lines was the seeder answering <c>NotSeeded</c> on purpose — the pod's log
/// carries <c>Prebuilt assembly for Edu/Quiz NOT SEEDED: Mesh:ClosedTypeSet=true</c> for each of
/// them — and nothing was compiled there in their place: a closed type set serves only the image's
/// types (<c>Doc/Architecture/ClosedTypeSet</c>). So the instrument read Degraded for ever on a
/// portal with nothing wrong with it.
///
/// <para>Pinned: on a closed type set <see cref="PluginBundleClient.Adopt"/> asks the registry
/// NOTHING (the bytes could only be declined), returns 0, and records
/// <see cref="BundleAdoptionKind.NotApplicable"/> — so <c>bundle_adoption</c> reads "no misses".
/// The control (<see cref="OpenTypeSetBundleAdoptionControlTest"/>) is the same client on an open
/// mesh against the same registry: it DOES fetch the index and DOES record the miss, so the closed
/// arm's silence is a verdict and not a client that asks nothing anywhere.</para>
/// </summary>
public class ClosedTypeSetBundleAdoptionIsNotAMissTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    internal const string RegistryHost = "registry.closedset.test";
    internal const string RegistryUrl = "https://" + RegistryHost;
    internal const string Plugin = "Edu";

    private readonly CountingRegistry registry = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .WithClosedTypeSet()
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton<IHttpClientFactory>(new HostRoutingClientFactory(registry)));

    [Fact(Timeout = 60_000)]
    public async Task OnAClosedTypeSet_NothingIsFetched_AndTheAttemptIsNotAMiss()
    {
        var ct = TestContext.Current.CancellationToken;

        var adopted = await new PluginBundleClient(Mesh, RegistryUrl, "mwi_closed").Adopt(Plugin)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        adopted.Should().Be(0);
        registry.Requests.Should().BeEmpty(
            "a closed type set adopts no database NodeType, so the bundle could only be declined");
        var ledger = Mesh.ServiceProvider.GetRequiredService<BundleAdoptionLedger>();
        var outcome = ledger.Outcomes.Should().ContainSingle(o => o.PluginId == Plugin).Subject;
        outcome.Kind.Should().Be(BundleAdoptionKind.NotApplicable,
            "nothing was fetched, so nothing may be claimed about what the package carries");
        outcome.Reason.Should().Contain(ClosedTypeSet.ConfigKey);
        ledger.Misses.Should().BeEmpty("nothing is compiled here in place of the bundle");
        ledger.Describe().Should().Contain("no misses").And.Contain("1 not attempted")
            .And.NotContain("carried no NodeTypes", "the package was never read");
    }
}

/// <summary>The open-mesh control for <see cref="ClosedTypeSetBundleAdoptionIsNotAMissTest"/>.</summary>
public class OpenTypeSetBundleAdoptionControlTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly CountingRegistry registry = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton<IHttpClientFactory>(new HostRoutingClientFactory(registry)));

    [Fact(Timeout = 60_000)]
    public async Task OnAnOpenMesh_TheSameAttemptFetchesTheIndex_AndRecordsTheMiss()
    {
        var ct = TestContext.Current.CancellationToken;

        var adopted = await new PluginBundleClient(
                Mesh, ClosedTypeSetBundleAdoptionIsNotAMissTest.RegistryUrl, "mwi_open")
            .Adopt(ClosedTypeSetBundleAdoptionIsNotAMissTest.Plugin)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        adopted.Should().Be(0);
        registry.Requests.Should().NotBeEmpty("an open mesh asks the registry for its index");
        var ledger = Mesh.ServiceProvider.GetRequiredService<BundleAdoptionLedger>();
        ledger.Misses.Should().ContainSingle(o => o.PluginId == ClosedTypeSetBundleAdoptionIsNotAMissTest.Plugin)
            .Which.Kind.Should().Be(BundleAdoptionKind.FrameworkDeclined,
                "the index is baked for a framework this process does not run");
    }
}

/// <summary>A registry whose index is baked for another framework, recording every request.</summary>
internal sealed class CountingRegistry : HttpMessageHandler, IHostHandler
{
    private ImmutableList<string> requests = ImmutableList<string>.Empty;

    public ImmutableList<string> Requests => requests;

    public bool Serves(string host) =>
        string.Equals(host, ClosedTypeSetBundleAdoptionIsNotAMissTest.RegistryHost, StringComparison.OrdinalIgnoreCase);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ImmutableInterlocked.Update(ref requests, r => r.Add(request.RequestUri!.AbsolutePath));
        var body = JsonSerializer.Serialize(new
        {
            frameworkMvid = "s00000000000000000000000000000000",
            bundles = Array.Empty<object>(),
        });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
