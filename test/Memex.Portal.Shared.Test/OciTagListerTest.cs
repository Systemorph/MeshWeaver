#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The self-updater listing tags on the read-through MIRROR instead of ACR (#3353): the bearer
/// handshake the mirror speaks, walked end to end against an in-process registry; the
/// credential being this installation's own plugin-registry instance key and nothing new; every
/// page of a paginated listing being followed; and a refused or misconfigured credential being an
/// ERROR — never an empty list, which the poller would read as "nothing to roll to".
///
/// <para>The fake substitutes the NETWORK, not a MeshWeaver interface: it holds the protocol's
/// invariants (a 401 without a bearer, a 401 on a wrong key, a relative <c>Link</c> continuation)
/// rather than replaying recorded answers. The hub, the token resolver and the poller's decision
/// path are real.</para>
/// </summary>
public class OciTagListerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string MirrorHost = "mirror.example.test";
    private const string MirrorUrl = "https://" + MirrorHost;
    private const string InstanceKey = "mwi_this-installations-own-plugin-registry-key";
    private const string Repository = "memex-portal-ai";

    /// <summary>Strictly newer than anything this test host can run as, so the roll it drives is
    /// the ordinary forward roll (the same shape ComboGateRollTest uses) — and the highest CD build
    /// number in the fixture, because lineage orders by <c>ci.N</c>, not by the SemVer prefix.</summary>
    private const string NewestTag = "9999.0.0-ci.3";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    private readonly FakeMirror mirror = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddUpdatePolicyType()
            .ConfigureServices(s => s
                .AddSingleton<IHttpClientFactory>(new FakeMirrorClientFactory(mirror))
                // The resolver the Store uses — real, so "the same credential" is measured on the
                // same code path rather than assumed.
                .AddSingleton<RegistryTokenResolver>()
                // The plugin registry this installation installs modules from IS the mirror host,
                // and the key it holds for it is the credential the lister must present.
                .AddSingleton(new PluginCatalogOptions
                {
                    Registries =
                    [
                        new PluginRegistryReference { Name = "Plugins", Url = MirrorUrl, Token = InstanceKey },
                    ],
                }));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private OciTagLister Lister(string registry = MirrorHost) => new(
        Mesh,
        new SelfUpdateOptions { Registry = registry },
        Mesh.ServiceProvider.GetService<ILogger<OciTagLister>>());

    // ══════════════════════════════════════════════════════════════════════════
    //  The handshake, the credential, the pages
    // ══════════════════════════════════════════════════════════════════════════

    [Fact(Timeout = 120_000)]
    public async Task ListsEveryPage_ThroughTheBearerHandshake_WithThePluginRegistryKey()
    {
        var ct = TestContext.Current.CancellationToken;

        var tags = await Lister().ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct);

        tags.Should().Equal(FakeMirror.Tags,
            "the listing is paginated and every page must be followed — ACR pages at 100 in lexical "
            + "order, so a lister that stops after the first page sees the OLDEST builds only");
        mirror.PresentedSecret.Should().Be(InstanceKey,
            "the credential is the plugin-registry instance key this installation already holds, "
            + "presented as Basic user:key at the realm the challenge names — no second secret");
        mirror.TokenRequests.Should().Be(1, "one exchange serves every page of one listing");
        mirror.PagesServed.Should().BeGreaterThan(1, "the fixture pages so that pagination is observable");
    }

    [Fact(Timeout = 120_000)]
    public async Task ARefusedKey_FaultsTheListing_NeverAnEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        mirror.RefuseKey = true;

        var fault = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lister().ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        fault.Message.Should().Contain("refused",
            "an empty listing would read as 'nothing to roll to' — the one silent state #2553 forbids");
        fault.Message.Should().Contain(MirrorHost);
    }

    [Fact(Timeout = 120_000)]
    public async Task NoPluginRegistryOnThatHost_FaultsNamingTheHost()
    {
        var ct = TestContext.Current.CancellationToken;

        var fault = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lister("other.example.test").ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        fault.Message.Should().Contain("other.example.test");
        fault.Message.Should().Contain("PluginCatalog",
            "the fix is a configuration change, and the message has to say which one");
        mirror.Requests.Should().Be(0, "a listing with no credential could only ever be a 401");
    }

    [Fact]
    public void AnAzureContainerRegistryHost_KeepsTheAcrLister()
    {
        new SelfUpdateOptions().RegistryIsAzureContainerRegistry.Should().BeTrue("the default is ACR");
        new SelfUpdateOptions { Registry = "MESHWEAVER.AZURECR.IO" }.RegistryIsAzureContainerRegistry
            .Should().BeTrue("host names are case-insensitive");
        new SelfUpdateOptions { Registry = "memex.meshweaver.cloud" }.RegistryIsAzureContainerRegistry
            .Should().BeFalse("the mirror is an OCI Distribution registry, listed through /v2");
        new SelfUpdateOptions { Registry = "memex.meshweaver.cloud/" }.RegistryIsAzureContainerRegistry
            .Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The poller's selection: a mirror install never talks to ACR
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 The integration claim. With <c>SelfUpdate:Registry</c> naming the mirror, ONE check
    /// lists through the mirror, finds the newer release and rolls to it — and the ACR seam is
    /// never touched (it throws if it is). The roll names the image on the mirror host, which is
    /// what makes the pull secret on the pod spec matter.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AMirrorInstall_RollsFromTheMirrorListing_AndNeverCallsAcr()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed();
        var updater = new RecordingUpdater();
        var options = new SelfUpdateOptions
        {
            Registry = MirrorHost,
            RetryInterval = TimeSpan.FromMilliseconds(500),
            EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
            DefaultPolicy = UpdatePolicyKind.Continuous,
            DefaultPattern = "*-ci*",
        };
        var service = new SeamedSelfUpdateService(
            Mesh, new AcrMustNotBeCalled(), updater, options,
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(),
            new AlwaysAvailable(Mesh, new ConfigurationBuilder().Build()),
            new OciTagLister(Mesh, options, Mesh.ServiceProvider.GetService<ILogger<OciTagLister>>()));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.Evaluations.FirstAsync().Timeout(Budget).Await(ct);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        updater.Tags.Should().Contain(NewestTag,
            "the newest release on the mirror is what a mirror-consuming install rolls to");
        updater.Images.Should().Contain($"{MirrorHost}/{Repository}:{NewestTag}",
            "the image the updater rolls to is named on the mirror host — that is the pull the pod's pull secret exists for");
        mirror.PresentedSecret.Should().Be(InstanceKey);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The fake mirror
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The mirror's pull surface for <c>tags/list</c>, as its own tests pin it: a bare 401 with a
    /// Bearer challenge until a bearer is presented, <c>/v2/token</c> answering the caller's OWN key
    /// back for <c>Basic user:key</c>, and a listing paginated with a relative <c>Link</c>.
    /// </summary>
    private sealed class FakeMirror : HttpMessageHandler
    {
        public static readonly string[] Tags = ["3.0.0-ci.1", "3.0.0-ci.2", NewestTag, "latest"];

        /// <summary>Two per page, whatever <c>?n=</c> asks, so pagination is always exercised.</summary>
        private const int PageSize = 2;

        public bool RefuseKey;
        public int Requests;
        public int TokenRequests;
        public int PagesServed;
        public string? PresentedSecret;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            var uri = request.RequestUri!;
            if (!string.Equals(uri.Host, MirrorHost, StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            if (uri.AbsolutePath == "/v2/token")
            {
                Interlocked.Increment(ref TokenRequests);
                var header = request.Headers.Authorization;
                string? secret = null;
                if (header is { Scheme: "Basic", Parameter: { } basic })
                {
                    var pair = Encoding.UTF8.GetString(Convert.FromBase64String(basic));
                    secret = pair[(pair.IndexOf(':') + 1)..];
                }
                PresentedSecret = secret;
                if (RefuseKey || secret != InstanceKey)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                // The mirror mints nothing: the bearer IS the caller's key.
                return Task.FromResult(Json($$"""{"token":"{{InstanceKey}}"}"""));
            }

            if (request.Headers.Authorization is not { Scheme: "Bearer" } auth || auth.Parameter != InstanceKey)
                return Task.FromResult(Challenge());

            if (uri.AbsolutePath == $"/v2/{Repository}/tags/list")
            {
                Interlocked.Increment(ref PagesServed);
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var last = query["last"];
                var remaining = last is null ? Tags : Tags.SkipWhile(t => t != last).Skip(1).ToArray();
                var page = remaining.Take(PageSize).ToArray();
                var response = Json(
                    $$"""{"name":"{{Repository}}","tags":[{{string.Join(",", page.Select(t => $"\"{t}\""))}}]}""");
                if (page.Length < remaining.Length)
                    response.Headers.TryAddWithoutValidation(
                        "Link", $"</v2/{Repository}/tags/list?n={PageSize}&last={page[^1]}>; rel=\"next\"");
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Challenge()
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer",
                $"realm=\"{MirrorUrl}/v2/token\",service=\"{MirrorHost}\""));
            return response;
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeMirrorClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The poller harness (the seams SelfUpdateStrandRecoveryTest documents)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The ACR seam of a mirror install: reaching it IS the failure.</summary>
    private sealed class AcrMustNotBeCalled : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            throw new InvalidOperationException(
                "the ACR lister was called on an install whose SelfUpdate:Registry is the mirror");
    }

    private sealed class RecordingUpdater : IDeploymentUpdater
    {
        private ImmutableList<string> tags = ImmutableList<string>.Empty;
        private ImmutableList<string> images = ImmutableList<string>.Empty;

        public ImmutableList<string> Tags => tags;
        public ImmutableList<string> Images => images;
        public bool CanPatch => true;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct)
        {
            ImmutableInterlocked.Update(ref tags, current => current.Add(versionTag));
            ImmutableInterlocked.Update(ref images, current => current.Add(
                new SelfUpdateOptions { Registry = MirrorHost }.PortalImage(versionTag)));
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysAvailable(IMessageHub hub, IConfiguration configuration)
        : ReleaseAvailabilityService(hub, configuration)
    {
        public override IObservable<UpdatabilityVerdict> IsUpdatable(string? targetVersion) =>
            Observable.Return(UpdatabilityVerdict.NotEnforced("this test host consumes no CI bakes"));
    }

    private sealed class SeamedSelfUpdateService(
        IMessageHub hub,
        IAcrTagLister acr,
        IDeploymentUpdater updater,
        SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger,
        ReleaseAvailabilityService gate,
        OciTagLister oci)
        : SelfUpdateHostedService(hub, acr, updater, options, logger, oci: oci)
    {
        public IObservable<Unit> Evaluations => ChecksReported;

        protected override ReleaseAvailabilityService? ResolveAvailabilityGate() => gate;

        protected override ComboVerificationGate? ResolveComboGate() => null;
    }

    private Task Seed()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            // 2026-09-08: the newest tag is a ci build, eligible only under a pattern that admits it.
            Content = new UpdatePolicyContent { Policy = UpdatePolicyKind.Continuous, Pattern = "*-ci*" },
        };
        return Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return (IDisposable)meshService.CreateNode(node).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
    }
}
