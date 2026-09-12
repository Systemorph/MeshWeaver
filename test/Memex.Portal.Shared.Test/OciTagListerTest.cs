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
using MeshWeaver.Graph;
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

    /// <summary>The key an AUTO-REGISTERED installation holds: issued at first boot against
    /// <c>PluginCatalog:BootstrapKey</c>, stored at <c>Admin/PluginRegistryCredential/…</c>, and
    /// configured NOWHERE — the zero-touch shape <c>InstanceProvisioningPlan</c> documents. Distinct
    /// from <see cref="InstanceKey"/> so a presented secret proves WHERE it came from.</summary>
    private const string StoredKey = "mwi_issued-at-first-boot-and-stored-in-the-admin-partition";

    /// <summary>What the plugin registry's exchange route mints for a stored key — the short-lived
    /// token <c>RegistryTokenResolver.ResolveToken</c> would present, which the fleet registry's
    /// validator refuses BY DESIGN (a token may never mint its successor).</summary>
    private const string ExchangedToken = "mwa_short-lived-token-the-validator-refuses";

    /// <summary>A SECOND plugin registry this installation holds a key for, so that "the key held for
    /// the DECLARED validator" and "some other registry's key" are different values.</summary>
    private const string OtherRegistryHost = "a.example.test";
    private const string OtherRegistryUrl = "https://" + OtherRegistryHost;
    private const string OtherRegistryKey = "mwi_issued-by-a-example-test-and-not-the-validator";

    /// <summary>Strictly newer than anything this test host can run as, so the roll it drives is
    /// the ordinary forward roll (the same shape ComboGateRollTest uses) — and the highest CD build
    /// number in the fixture, because lineage orders by <c>ci.N</c>, not by the SemVer prefix.</summary>
    private const string NewestTag = "9999.0.0-ci.3";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    private readonly FakeMirror mirror = new();

    /// <summary>
    /// This installation's plugin-catalog configuration — the singleton the lister resolves. The
    /// default is the fleet's common shape (ONE registry, the mirror host, its key configured); a
    /// test that needs another shape assigns <c>Registries</c> before it lists, and the lister reads
    /// the instance at listing time, so no test depends on when the container first resolved it.
    /// </summary>
    private readonly PluginCatalogOptions catalog = new()
    {
        Registries =
        [
            new PluginRegistryReference { Name = "Plugins", Url = MirrorUrl, Token = InstanceKey },
        ],
    };

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddUpdatePolicyType()
            // The credential STORE's node type, and only that — the same definition
            // AddPluginCatalog registers, without the catalog's hosted services (the boot reconcile
            // and the auto-registration pass would reach the fake at startup and count as requests
            // the negatives assert are zero). The resolver reads it; the auto-registered test writes it.
            .AddMeshNodes(new MeshNode(PluginRegistryCredentials.NodeType)
            {
                Name = "Plugin Registry Credential",
                IsSatelliteType = false,
                HubConfiguration = config => config
                    .AddMeshDataSource(s => s.WithContentType<PluginRegistryCredential>()),
            })
            .ConfigureServices(s => s
                .AddSingleton<IHttpClientFactory>(new FakeMirrorClientFactory(mirror))
                // The resolver the Store uses — real, so "the same credential" is measured on the
                // same code path rather than assumed.
                .AddSingleton<RegistryTokenResolver>()
                // The plugin registry this installation installs modules from IS the mirror host,
                // and the key it holds for it is the credential the lister must present.
                .AddSingleton(catalog));

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

        var fault = await Record.ExceptionAsync(() =>
            Lister("other.example.test").ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        // 🚨 Disclosure FIRST, so a falsification reports WHAT LEAKED rather than "no exception".
        mirror.CredentialsSeen.Should().BeEmpty(
            "the guard is a disclosure control: an arbitrary host named in SelfUpdate:Registry must "
            + "never be handed this installation's instance key");
        mirror.Requests.Should().Be(0, "a listing with no credential could only ever be a 401");
        var message = fault.Should().BeOfType<InvalidOperationException>().Which.Message;
        message.Should().Contain("other.example.test");
        message.Should().Contain("PluginCatalog",
            "the fix is a configuration change, and the message has to say which one");
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
        mirror.ReachedHosts.Should().OnlyContain(h => h == RegistryHost,
            "the listing goes to the container registry; the validator is named in configuration "
            + "and never contacted by the lister — and this is EVERY host reached, recorded before "
            + "the fake decides what it serves, so a request to a third host cannot hide");
    }

    /// <summary>
    /// 🚨 THE FIX, for the installation the fix was FOR (#4094 review). A zero-touch consumer —
    /// <c>PluginCatalog:BootstrapKey</c>, no configured Token — holds its <c>mwi_</c> key only in the
    /// store, and the Store's resolver EXCHANGES that key for a short-lived <c>mwa_</c> token before
    /// presenting it. The fleet registry decides a pull by forwarding the presented secret to the
    /// declared validator, which IS the exchange endpoint and refuses a token by design — so the
    /// lister must present the durable key itself, unexchanged. A lister that resolved through
    /// <c>ResolveToken</c> here presents <see cref="ExchangedToken"/>, is refused on every check, and
    /// fixes only the installations with a raw Token configured.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnAutoRegisteredInstallation_PresentsItsStoredDurableKey_NeverAnExchangedToken()
    {
        var ct = TestContext.Current.CancellationToken;
        // The zero-touch shape: the registry is configured, its key is NOT — it lives in the store.
        catalog.Registries = [new PluginRegistryReference { Name = "Plugins", Url = MirrorUrl }];
        await StoreCredential(MirrorUrl, StoredKey);
        // The registry's validator knows the key it issued, and nothing else — in particular not a
        // token minted FROM it, which `InstanceTokenEndpoints` refuses by shape.
        mirror.AcceptedSecrets = [StoredKey];

        IReadOnlyList<string>? tags = null;
        var fault = await Record.ExceptionAsync(async () =>
            tags = await Lister(RegistryHost, DeclaredValidator)
                .ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        // 🚨 The MECHANISM first, so a regression reports what was presented rather than only the
        // 401 it earns: a lister resolving through ResolveToken exchanges the stored key (one
        // exchange request) and presents the mwa_ token, which the registry's validator refuses.
        mirror.ExchangeRequests.Should().Be(0,
            "the declared validator IS the key-to-token exchange; exchanging first would hand the "
            + "registry a token its validator refuses by design, on every check");
        mirror.PresentedSecret.Should().Be(StoredKey,
            "the credential is the DURABLE mwi_ key from the store — configured nowhere, so it can "
            + "only have come from there — and never the mwa_ token the Store would present");
        mirror.PresentedSecret.Should().StartWith("mwi_");
        fault.Should().BeNull("the listing must succeed for an auto-registered instance");
        tags.Should().Equal(FakeMirror.Tags,
            "an auto-registered instance on the fleet registry must be able to see what it can roll to");
        mirror.ReachedHosts.Should().OnlyContain(h => h == RegistryHost,
            "the key goes to the container registry only; the store is read in-process");
    }

    /// <summary>
    /// 🚨 SELECTION, not merely resolution (#4094 review). With ONE registry configured nothing
    /// distinguishes "the key held for the DECLARED validator" from "some registry's key", so a
    /// lister that took the first configured registry would pass the positive above. Two
    /// registries, two keys, the validator declared as the SECOND: the key presented must be B's.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ADeclaredValidator_SelectsTheKeyHeldForThatPortal_NotAnotherRegistrys()
    {
        var ct = TestContext.Current.CancellationToken;
        catalog.Registries =
        [
            new PluginRegistryReference { Name = "A", Url = OtherRegistryUrl, Token = OtherRegistryKey },
            new PluginRegistryReference { Name = "B", Url = MirrorUrl, Token = InstanceKey },
        ];
        // The fake accepts BOTH keys on purpose: a wrong selection must report as the wrong SECRET,
        // not as a refusal the reader could attribute to the fixture.
        mirror.AcceptedSecrets = [OtherRegistryKey, InstanceKey];

        var tags = await Lister(RegistryHost, DeclaredValidator)
            .ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct);

        tags.Should().Equal(FakeMirror.Tags);
        mirror.PresentedSecret.Should().Be(InstanceKey,
            "the key presented is the one held for the DECLARED validator (B), selected by host — "
            + "never the first configured registry's, never 'a key this installation happens to hold'");
        mirror.PresentedSecret.Should().NotBe(OtherRegistryKey);
        mirror.ReachedHosts.Should().OnlyContain(h => h == RegistryHost);
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

        var fault = await Record.ExceptionAsync(() =>
            Lister(RegistryHost).ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        mirror.CredentialsSeen.Should().BeEmpty(
            "an undeclared pairing is not permission — the key never goes out");
        mirror.Requests.Should().Be(0);
        var message = fault.Should().BeOfType<InvalidOperationException>().Which.Message;
        message.Should().Contain(RegistryHost);
        message.Should().Contain("SelfUpdate:RegistryValidationUrl",
            "an absent declaration is refused AND the message says what would declare it");
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

        var fault = await Record.ExceptionAsync(() =>
            Lister(RegistryHost, "https://someone-elses-portal.example.test/api/instances/token")
                .ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        mirror.CredentialsSeen.Should().BeEmpty(
            "the key is only ever presented to a host this installation was issued one for");
        mirror.Requests.Should().Be(0);
        var message = fault.Should().BeOfType<InvalidOperationException>().Which.Message;
        message.Should().Contain("someone-elses-portal.example.test");
        message.Should().Contain("PluginCatalog:Registries");
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL for the DIAGNOSIS (#4094 review). A declaration that is SET but names no
    /// http(s) host — a userinfo-bearing value, a typo'd scheme — used to read as ABSENT: the
    /// refusal told the operator to declare the key they had already set. It is refused as
    /// MALFORMED, the message names the key and the shape, and the value is never echoed, because
    /// the userinfo is where a key would be.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData("https://memex.example.test@evil.example.test/api/instances/token")]
    [InlineData("mailto:ops@example.test")]
    [InlineData("htps://memex.example.test/api/instances/token")]
    public async Task ADeclaredButUnreadableValidator_IsRefusedAsMalformed_NeverAsAbsent(string declared)
    {
        var ct = TestContext.Current.CancellationToken;

        var fault = await Record.ExceptionAsync(() =>
            Lister(RegistryHost, declared).ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        mirror.CredentialsSeen.Should().BeEmpty();
        mirror.Requests.Should().Be(0);
        var message = fault.Should().BeOfType<InvalidOperationException>().Which.Message;
        message.Should().Contain("SelfUpdate:RegistryValidationUrl is set but does not name an http(s) host",
            "a set-but-unreadable declaration is a misconfiguration to NAME, never 'nothing declared'");
        message.Should().NotContain("no validator is declared",
            "telling the operator to set a key they already set is a fail-closed fallback forging a "
            + "correct-looking bug");
        message.Should().NotContain("evil.example.test", "the value is not echoed");
        message.Should().NotContain("ops@example.test");
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL for the OTHER diagnosis (#4094 review): a plugin registry that EXISTS on
    /// the qualifying host but whose URL carries credentials (<c>https://instance:mwi_…@host</c>).
    /// <c>HostOf</c> refuses userinfo, so such a registry stops matching on BOTH branches — the
    /// pre-#4094 reader tolerated it — and without this control the refusal would claim "no plugin
    /// registry is configured on that host" about a registry the catalog is talking to. The cause
    /// is named, the fix is named, and the URL is not echoed: the credential is in it.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(MirrorHost, "")]                       // the registry's OWN host
    [InlineData(RegistryHost, DeclaredValidator)]      // the declared validator — the fleet's shape
    public async Task APluginRegistryWhoseUrlCarriesCredentials_IsRefusedNamingTheCause_NeverAsAbsent(
        string registry, string validator)
    {
        var ct = TestContext.Current.CancellationToken;
        catalog.Registries =
        [
            new PluginRegistryReference
            {
                Name = "Plugins", Url = $"https://instance:{InstanceKey}@{MirrorHost}", Token = InstanceKey,
            },
        ];

        var fault = await Record.ExceptionAsync(() =>
            Lister(registry, validator).ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        mirror.CredentialsSeen.Should().BeEmpty(
            "a registry whose URL is not read as naming a host is not a credential source either");
        mirror.Requests.Should().Be(0);
        var message = fault.Should().BeOfType<InvalidOperationException>().Which.Message;
        message.Should().Contain("carries credentials",
            "the registry exists; what is wrong is where its credential sits, and the message says so");
        message.Should().Contain("PluginCatalog:Registries:N:Token", "the fix is named");
        message.Should().NotContain("no plugin registry",
            "'no registry configured' must never be the diagnosis for a registry that exists");
        message.Should().NotContain(InstanceKey);
        message.Should().NotContain("mwi_");
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

        var fault = await Record.ExceptionAsync(() =>
            Lister($"{smuggled}@evil.example.test", DeclaredValidator)
                .ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        mirror.CredentialsSeen.Should().BeEmpty(
            "a declared validator must never make an unparseable target reachable — and this "
            + "assertion is recorded before the fake's host check, so it can see a credential sent "
            + "to evil.example.test, which PresentedSecret by construction could not");
        mirror.Requests.Should().Be(0);
        var message = fault.Should().BeOfType<InvalidOperationException>().Which.Message;
        message.Should().NotContain(smuggled,
            "the refusal must not echo a value of this shape — the userinfo is where a key would be");
        message.Should().NotContain("mwi_");
        message.Should().Contain("SelfUpdate:Registry");
    }

    /// <summary>
    /// The same normalization closes an ASYMMETRY: the target was compared raw against plugin
    /// hosts parsed by <c>HostOf</c>, so a URL form missed a registry that IS the same host. It is
    /// refused rather than silently accepted, because the identical value is interpolated into the
    /// portal and migration image references, which a URL form makes malformed.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData("https://" + MirrorHost)]
    [InlineData(MirrorHost + "/v2")]
    [InlineData("https://" + MirrorHost + ":443/v2/")]
    public async Task AUrlFormRegistryTarget_IsRefused_NamingTheBareHostToUse(string target)
    {
        var ct = TestContext.Current.CancellationToken;

        var fault = await Record.ExceptionAsync(() =>
            Lister(target).ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        mirror.Requests.Should().Be(0, "the refusal is decided before a byte is sent");
        mirror.CredentialsSeen.Should().BeEmpty();
        var message = fault.Should().BeOfType<InvalidOperationException>().Which.Message;
        message.Should().Contain(MirrorHost, "the message names the value to set instead");
        message.Should().Contain("bare registry host");
        message.Should().Contain("carries a scheme or a path", "and the message must be TRUE of the value");
    }

    /// <summary>
    /// The documentation promises <c>host</c> or <c>host:port</c>, and 443 is a port (#4094 review).
    /// <c>HostOf</c> drops a scheme-default port, so a bare-host check written as equality with the
    /// parsed host refused <c>host:443</c> — with a message claiming it "carries a scheme or a
    /// path", which it does not. The check is textual on the configured value; the client and the
    /// credential match then use the normalized host, which is what the plugin registry is
    /// configured under.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ABareHostNamingTheDefaultPort_IsAccepted_AndListed()
    {
        var ct = TestContext.Current.CancellationToken;

        var tags = await Lister($"{MirrorHost}:443").ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct);

        tags.Should().Equal(FakeMirror.Tags, "'host:port' is the documented shape, whatever the port");
        mirror.PresentedSecret.Should().Be(InstanceKey,
            "the plugin registry configured as https://host is the same host as host:443");
        mirror.ReachedHosts.Should().OnlyContain(h => h == MirrorHost);
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

    /// <summary>"Declared" and "readable" are two questions (#4094 review): a set-but-unreadable
    /// value is DECLARED and yields no host, and a caller refusing on the null host must be able to
    /// say which of the two it is refusing on.</summary>
    [Fact]
    public void ADeclaration_IsToldApartFromAReadableOne()
    {
        new SelfUpdateOptions().RegistryValidatorDeclared.Should().BeFalse();
        new SelfUpdateOptions { RegistryValidationUrl = "   " }.RegistryValidatorDeclared.Should().BeFalse(
            "whitespace is not a declaration");
        var malformed = new SelfUpdateOptions { RegistryValidationUrl = "https://memex.example.test@evil.example.test/x" };
        malformed.RegistryValidatorDeclared.Should().BeTrue("the operator SET it");
        malformed.RegistryValidatorHost.Should().BeNull("and it names no host — malformed, not absent");
        var declared = new SelfUpdateOptions { RegistryValidationUrl = DeclaredValidator };
        declared.RegistryValidatorDeclared.Should().BeTrue();
        declared.RegistryValidatorHost.Should().Be(MirrorHost);
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

    /// <summary>
    /// 🚨 NEGATIVE CONTROL for the BOOT LINE (#4094 review, the serious one). The poller's startup
    /// line is <c>Information</c> — it leaves the pod for Loki on every start, BEFORE the lister's
    /// refusal (which deliberately does not echo the value) has ever run. Logging
    /// <c>SelfUpdate:Registry</c> verbatim there shipped a value of the shape
    /// <c>instance:mwi_…@evil.example</c>, key included, to Loki at boot. The line names the HOST
    /// read from the value, or says it is unreadable — and it names the THIRD validator state (set
    /// but unreadable) as such, never as "NO validator declared".
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheBootLine_NamesTheRegistryHost_NeverTheConfiguredValue()
    {
        const string smuggled = "instance:mwi_smuggled-in-the-registry-value";
        var log = new CapturingLogger<SelfUpdateHostedService>();
        var options = new SelfUpdateOptions
        {
            Registry = $"{smuggled}@evil.example.test",
            RegistryValidationUrl = "https://memex.example.test@evil.example.test/api/instances/token",
            DefaultPolicy = UpdatePolicyKind.Continuous,
            DefaultPattern = "*-ci*",
        };
        var service = new SeamedSelfUpdateService(
            Mesh, new AcrMustNotBeCalled(), new RecordingUpdater(), options, log,
            new AlwaysAvailable(Mesh, new ConfigurationBuilder().Build()),
            new OciTagLister(Mesh, options, Mesh.ServiceProvider.GetService<ILogger<OciTagLister>>()));

        await service.StartAsync(CancellationToken.None);
        try
        {
            var boot = log.Lines.Should().ContainSingle(l => l.Contains("[SelfUpdate] starting"),
                "the startup line is written synchronously in StartAsync").Which;
            boot.Should().NotContain(smuggled,
                "the configured value is never logged — this line ships to Loki at boot, before any refusal");
            boot.Should().NotContain("mwi_");
            boot.Should().Contain("(unreadable", "an unreadable value is named as such, not printed");
            boot.Should().Contain("is SET but does not name an http(s) host",
                "a declaration that is set but unreadable is the THIRD state, named as a misconfiguration");
            boot.Should().NotContain("NO validator declared",
                "which is what it used to say, sending the operator to set a key they had already set");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        log.Lines.Should().NotContain(l => l.Contains("mwi_"),
            "no line this service wrote while it ran carries anything shaped like the key");
        mirror.CredentialsSeen.Should().BeEmpty();
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
        public int ExchangeRequests;
        public int PagesServed;
        public string? PresentedSecret;

        /// <summary>
        /// The secrets the registry's token realm accepts — what its validator would answer 200 for.
        /// The fleet registry forwards the presented secret to <c>/api/instances/token</c>, which
        /// accepts a durable <c>mwi_</c> key it knows and REFUSES a token by shape, so a minted
        /// <see cref="ExchangedToken"/> is never in this set however it got there.
        /// </summary>
        public ImmutableHashSet<string> AcceptedSecrets = [InstanceKey];

        private ImmutableList<string> reachedHosts = ImmutableList<string>.Empty;
        private ImmutableList<string> credentialsSeen = ImmutableList<string>.Empty;

        /// <summary>
        /// 🚨 EVERY host a request reached, recorded UNCONDITIONALLY before the fake decides what it
        /// serves. It used to be appended only for the hosts the fake knows, so a positive asserting
        /// "only the container registry was contacted" could not see an un-credentialed request to a
        /// third host — the same vacuity class as the credential one below, on the other assertion
        /// (#4094 review). The listing must go to the CONTAINER registry, never to the portal whose
        /// key authenticates it.
        /// </summary>
        public ImmutableList<string> ReachedHosts => reachedHosts;

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
            ImmutableInterlocked.Update(ref reachedHosts, current => current.Add(uri.Host));
            if (request.Headers.Authorization is { } offered)
                ImmutableInterlocked.Update(ref credentialsSeen,
                    current => current.Add($"{offered.Scheme} to {uri.Host}"));

            // The plugin registry's key→token exchange (SyncTokenPayloads.Route): a durable key is
            // exchanged for a short-lived token, exactly as RegistryTokenResolver.ResolveToken does
            // for a STORED key. Served so that a lister which exchanges before presenting is SEEN to
            // — it then presents the token, which the registry's realm below refuses, as the fleet's
            // validator would. A lister that must not exchange leaves ExchangeRequests at zero.
            if (uri.AbsolutePath == SyncTokenPayloads.Route && request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref ExchangeRequests);
                return Task.FromResult(request.Headers.Authorization is { Scheme: "Bearer", Parameter: { } key }
                    && key.StartsWith("mwi_", StringComparison.Ordinal)
                    ? Json($$"""{"accessToken":"{{ExchangedToken}}","tokenType":"Bearer","expiresIn":300,"scope":[]}""")
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

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
                if (RefuseKey || secret is null || !AcceptedSecrets.Contains(secret))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                // The mirror mints nothing: the bearer IS the caller's key.
                return Task.FromResult(Json($$"""{"token":"{{secret}}"}"""));
            }

            if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { } bearer }
                || !AcceptedSecrets.Contains(bearer))
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

    /// <summary>Records every formatted line — instance state, owned by the test.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private ImmutableList<string> lines = ImmutableList<string>.Empty;

        public ImmutableList<string> Lines => lines;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => ImmutableInterlocked.Update(ref lines, current => current.Add(formatter(state, exception)));
    }

    /// <summary>
    /// What auto-registration leaves behind: the issued key at
    /// <c>Admin/PluginRegistryCredential/{host-slug}</c>, which <c>RegistryTokenResolver</c> reads as
    /// System. Stored in the clear here — the protector passes a value without the <c>enc:</c> tag
    /// through unchanged, and what this test measures is which SHAPE is presented, not the envelope.
    /// </summary>
    private Task StoreCredential(string registryUrl, string key)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var path = PluginRegistryCredentials.Path(registryUrl);
        var node = new MeshNode(path.Split('/').Last(), PluginRegistryCredentials.Namespace)
        {
            NodeType = PluginRegistryCredentials.NodeType,
            Name = "Registry credential (auto-registered)",
            State = MeshNodeState.Active,
            Content = new PluginRegistryCredential
            {
                RegistryUrl = registryUrl,
                InstanceId = "this-installation",
                ProtectedKey = key,
                RegisteredAt = DateTimeOffset.UtcNow,
            },
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
