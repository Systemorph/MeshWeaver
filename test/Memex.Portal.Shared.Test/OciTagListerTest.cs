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

    /// <summary>
    /// The fleet's OTHER shape (#4093): a container registry on its own host, which is NOT a plugin
    /// registry and never will be — <c>cr.meshweaver.cloud</c> decides a pull by forwarding the
    /// caller's key to <c>memex.meshweaver.cloud</c>, so the two hosts differ by design.
    /// </summary>
    private const string RegistryHost = "cr.example.test";

    /// <summary>The validator declaration that pairs <see cref="RegistryHost"/> with the plugin
    /// registry this installation actually holds a key for — a registry record's
    /// <c>validationUrl</c>, copied verbatim.</summary>
    private const string DeclaredValidator = MirrorUrl + "/api/instances/token";

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

    private OciTagLister Lister(string registry = MirrorHost, string validator = "") => new(
        Mesh,
        new SelfUpdateOptions { Registry = registry, RegistryValidationUrl = validator },
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
        mirror.CredentialsSeen.Should().BeEmpty(
            "the guard is a disclosure control: an arbitrary host named in SelfUpdate:Registry must "
            + "never be handed this installation's instance key");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  #4093 — the registry whose validator is ANOTHER portal
    //
    //  The fleet's registry decides a pull by forwarding the caller's key to a portal
    //  (cr.meshweaver.cloud → memex.meshweaver.cloud/api/instances/token), so the image registry
    //  and the plugin registry are DIFFERENT hosts by design. These three pin the whole rule: a
    //  DECLARED pairing resolves the key, and the two ways of not declaring one still refuse.
    //  Delete the host check in ResolveCredential and the two negatives below go red.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 THE FIX. The container registry is on its own host and is no plugin registry; the
    /// installation DECLARES the portal that validates its key there, and the key it already holds
    /// for that portal is what the listing presents.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ADeclaredValidator_PresentsTheKeyHeldForThatPortal_AndListsTheRegistry()
    {
        var ct = TestContext.Current.CancellationToken;

        var tags = await Lister(RegistryHost, DeclaredValidator)
            .ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct);

        tags.Should().Equal(FakeMirror.Tags,
            "an instance provisioned on the fleet registry must be able to see what it can roll to");
        mirror.PresentedSecret.Should().Be(InstanceKey,
            "the credential is the plugin-registry instance key this installation already holds for "
            + "the DECLARED VALIDATOR — the registry forwards it there to decide the pull, which is "
            + "exactly why the two hosts differ");
        mirror.ServedHosts.Should().OnlyContain(h => h == RegistryHost,
            "the listing goes to the container registry; the validator is named in configuration "
            + "and never contacted by the lister");
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL, and the one that proves the guard still guards: the SAME registry host
    /// as the test above, differing only in that nothing declares the pairing. It must refuse, and
    /// the key must not leave. A rule that merely dropped or loosened the host comparison — "the
    /// sole mount carries a key", "same registrable domain" — turns this green.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheSameRegistry_WithNoDeclaredValidator_IsStillRefused()
    {
        var ct = TestContext.Current.CancellationToken;

        var fault = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lister(RegistryHost).ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        fault.Message.Should().Contain(RegistryHost);
        fault.Message.Should().Contain("SelfUpdate:RegistryValidationUrl",
            "an absent declaration is refused AND the message says what would declare it");
        mirror.Requests.Should().Be(0);
        mirror.CredentialsSeen.Should().BeEmpty(
            "an undeclared pairing is not permission — the key never goes out");
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL: a declaration ALONE grants nothing. The validator named is a host this
    /// installation holds no plugin-registry key for, so there is still nothing to present — the
    /// pairing needs BOTH statements, and the refusal names the host that is missing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ADeclaredValidatorThisInstallationHoldsNoKeyFor_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;

        var fault = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lister(RegistryHost, "https://someone-elses-portal.example.test/api/instances/token")
                .ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        fault.Message.Should().Contain("someone-elses-portal.example.test");
        fault.Message.Should().Contain("PluginCatalog:Registries");
        mirror.Requests.Should().Be(0);
        mirror.CredentialsSeen.Should().BeEmpty(
            "the key is only ever presented to a host this installation was issued one for");
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL for the TARGET, not the declaration (Copilot review on #4094). A
    /// `SelfUpdate:Registry` carrying userinfo is a valid URI whose host is the LAST one —
    /// `OciRegistryClient` would build `https://instance:mwi_…@evil…/` and hand it the Basic
    /// credential — and the raw value is interpolated into that client's errors and this class's
    /// audit line. Host equality could never match such a value, so it is the declared-validator
    /// branch that would make it reachable; this refusal is what stops that branch opening a
    /// disclosure path. The refusal must ALSO not echo the value, which is where the key would be.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARegistryTargetCarryingUserinfo_IsRefused_AndNeverEchoed()
    {
        var ct = TestContext.Current.CancellationToken;
        const string smuggled = "instance:mwi_smuggled-in-the-registry-value";

        var fault = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lister($"{smuggled}@evil.example.test", DeclaredValidator)
                .ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        fault.Message.Should().NotContain(smuggled,
            "the refusal must not echo a value of this shape — the userinfo is where a key would be");
        fault.Message.Should().NotContain("mwi_");
        fault.Message.Should().Contain("SelfUpdate:Registry");
        mirror.Requests.Should().Be(0);
        mirror.CredentialsSeen.Should().BeEmpty(
            "a declared validator must never make an unparseable target reachable — and this "
            + "assertion is recorded before the fake's host check, so it can see a credential sent "
            + "to evil.example.test, which PresentedSecret by construction could not");
    }

    /// <summary>
    /// The same normalization closes an ASYMMETRY: the target was compared raw against plugin
    /// hosts parsed by <c>HostOf</c>, so a URL form missed a registry that IS the same host. It is
    /// refused rather than silently accepted, because the identical value is interpolated into the
    /// portal and migration image references, which a URL form makes malformed.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AUrlFormRegistryTarget_IsRefused_NamingTheBareHostToUse()
    {
        var ct = TestContext.Current.CancellationToken;

        var fault = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lister($"https://{MirrorHost}").ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        fault.Message.Should().Contain(MirrorHost, "the message names the value to set instead");
        fault.Message.Should().Contain("bare registry host");
        mirror.Requests.Should().Be(0);
        mirror.CredentialsSeen.Should().BeEmpty();
    }

    /// <summary>The declaration is read WHOLE-HOST or not at all — never a suffix, a registrable
    /// domain or anything else that could make a coincidence of naming look like a grant.</summary>
    [Fact]
    public void TheDeclaredValidatorHost_IsReadWholeOrNotAtAll()
    {
        new SelfUpdateOptions().RegistryValidatorHost.Should().BeNull(
            "nothing declared is never permission");
        new SelfUpdateOptions { RegistryValidationUrl = "   " }.RegistryValidatorHost.Should().BeNull();
        new SelfUpdateOptions { RegistryValidationUrl = "https://memex.meshweaver.cloud/api/instances/token" }
            .RegistryValidatorHost.Should().Be("memex.meshweaver.cloud", "only the host is read");
        new SelfUpdateOptions { RegistryValidationUrl = "MEMEX.meshweaver.cloud" }
            .RegistryValidatorHost.Should().Be("memex.meshweaver.cloud",
                "a bare host means the same portal, and host names fold case");
        new SelfUpdateOptions { RegistryValidationUrl = "https://memex.meshweaver.cloud:8443/x" }
            .RegistryValidatorHost.Should().Be("memex.meshweaver.cloud:8443",
                "a non-default port is part of the host");
        new SelfUpdateOptions { RegistryValidationUrl = "https://memex.meshweaver.cloud@evil.example.test/x" }
            .RegistryValidatorHost.Should().BeNull(
                "the host there is evil.example.test and it reads to a human as the opposite — a "
                + "value that can be misread that way declares no pairing at all");
        new SelfUpdateOptions { RegistryValidationUrl = "mailto:ops@example.test" }
            .RegistryValidatorHost.Should().BeNull("a value that names no http host declares nothing");
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

        private ImmutableList<string> servedHosts = ImmutableList<string>.Empty;
        private ImmutableList<string> credentialsSeen = ImmutableList<string>.Empty;

        /// <summary>Every host a request actually reached — the listing must go to the CONTAINER
        /// registry, never to the portal whose key authenticates it.</summary>
        public ImmutableList<string> ServedHosts => servedHosts;

        /// <summary>
        /// 🚨 Every host that was sent ANY credential, recorded BEFORE the host check — which is
        /// what makes the disclosure assertions non-vacuous. <see cref="PresentedSecret"/> is only
        /// assigned inside the <c>/v2/token</c> branch, reached solely for the two hosts this fake
        /// serves, so on its own it would read null even if the key HAD been sent to
        /// <c>evil.example.test</c> — an assertion that cannot fail for exactly the host the
        /// negatives exist to catch. This one can.
        /// </summary>
        public ImmutableList<string> CredentialsSeen => credentialsSeen;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            var uri = request.RequestUri!;
            if (request.Headers.Authorization is { } offered)
                ImmutableInterlocked.Update(ref credentialsSeen,
                    current => current.Add($"{offered.Scheme} to {uri.Host}"));

            // 🚨 AN UNKNOWN HOST BEHAVES LIKE A HOSTILE REGISTRY, NOT LIKE A 404.
            //
            // This is what makes the disclosure assertions real. The client presents Basic only
            // AFTER a 401 Bearer challenge, so a fake that answered 404 here could never observe a
            // leaked credential — and a negative asserting "no credential was seen" would pass by
            // construction whatever the code did. (Measured: with the target guard deleted, that
            // shape of fixture reported a clean PASS.) A real attacker-controlled host issues the
            // challenge, so this one does too: it challenges, it serves /v2/token, and it records
            // whatever secret is handed over. Only the tags listing stays restricted to the two
            // hosts this fixture legitimately serves.
            var known = uri.Host is MirrorHost or RegistryHost;
            if (known)
                ImmutableInterlocked.Update(ref servedHosts, current => current.Add(uri.Host));

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
                return Task.FromResult(Challenge(uri.Host));

            if (known && uri.AbsolutePath == $"/v2/{Repository}/tags/list")
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

        private static HttpResponseMessage Challenge(string host)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer",
                $"realm=\"https://{host}/v2/token\",service=\"{host}\""));
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
