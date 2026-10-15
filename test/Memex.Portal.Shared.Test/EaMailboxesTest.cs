using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.AI;                       // ProviderKeyProtector
using MeshWeaver.Data;                     // GetMeshNodeStream
using MeshWeaver.Fixture;                  // TestTimeouts.Convergence
using MeshWeaver.Graph.Configuration;      // AddEaCredentialType
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;            // IMasterKeyProvider
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Several Microsoft 365 mailboxes per person (2026-10-13): the account-id rules, the OAuth state
/// that carries the account across the consent round trip, the address read off the id token, and
/// — on a real monolith mesh — that an added mailbox is stored beside the primary, listed with it,
/// de-duplicated by address, and removed without touching the other.
/// </summary>
public class EaMailboxesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // ── Pure rules ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NewAccountIds_AreWellFormed_AndNeverRepeat()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => EaMailboxes.NewAccountId()).ToList();
        ids.Should().OnlyContain(id => EaMailboxes.IsAddedAccountId(id));
        ids.Distinct().Should().HaveCount(ids.Count);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("primary", true)]
    [InlineData("new", true)]
    [InlineData("ea-credential--0123456789", true)]
    [InlineData("ea-credential--0123456789/../x", false)]
    [InlineData("ea-credential--ABCDEF0123", false)]
    [InlineData("ea-credential--012345678", false)]
    [InlineData("someone-else", false)]
    public void OnlyThePrimary_New_OrAWellFormedId_IsAValidRequest(string? account, bool valid) =>
        EaMailboxes.IsValidRequest(account).Should().Be(valid);

    private static EaMailbox Box(string id, string? address, EaConnection state) =>
        new(id, address, new EaGraphAccess(state));

    [Fact]
    public void TheDefaultMailbox_IsTheConnectedPrimary_ElseTheFirstConnected()
    {
        var work = Box(EaMailboxes.Primary, "max@fabrikam.example", EaConnection.Connected);
        var home = Box("ea-credential--aaaaaaaaaa", "max@acme.example", EaConnection.Connected);
        EaMailboxes.Select([work, home], null).Should().Be(work);

        var refusedPrimary = work with { Access = EaGraphAccess.NotConnected("refused") };
        EaMailboxes.Select([refusedPrimary, home], null).Should().Be(home,
            "a primary that cannot serve must not hide a mailbox that can");
        EaMailboxes.Select([refusedPrimary], null).Should().Be(refusedPrimary,
            "with nothing connected the primary is still returned, so the caller can say why");
        EaMailboxes.Select(ImmutableList<EaMailbox>.Empty, null).Should().BeNull();
    }

    [Fact]
    public void AMailboxIsAddressed_ByAddress_CaseInsensitively_OrByAccountId()
    {
        var work = Box(EaMailboxes.Primary, "max@fabrikam.example", EaConnection.Connected);
        var home = Box("ea-credential--aaaaaaaaaa", "max@acme.example", EaConnection.Connected);
        EaMailboxes.Select([work, home], "MAX@Acme.example").Should().Be(home);
        EaMailboxes.Select([work, home], "ea-credential--aaaaaaaaaa").Should().Be(home);
        EaMailboxes.Select([work, home], "primary").Should().Be(work);
        EaMailboxes.Select([work, home], "jane@initech.example").Should().BeNull();
    }

    [Fact]
    public void ANewConsent_ReusesTheMailboxWithTheSameAddress_ElseFillsAnEmptyPrimary_ElseMintsAnId()
    {
        var work = Box(EaMailboxes.Primary, "max@fabrikam.example", EaConnection.Connected);
        EaGraphAuth.ChooseNewTarget([], "max@fabrikam.example").Should().Be(EaMailboxes.Primary);
        EaGraphAuth.ChooseNewTarget([work], "MAX@fabrikam.example").Should().Be(EaMailboxes.Primary);
        EaGraphAuth.ChooseNewTarget([work], "max@acme.example").Should().StartWith(EaMailboxes.AddedPrefix);
        EaGraphAuth.ChooseNewTarget([work], null).Should().StartWith(EaMailboxes.AddedPrefix,
            "without an address there is nothing to de-duplicate on, and the primary is taken");
    }

    [Fact]
    public void TheState_CarriesTheAccount_AndAnOldStateStillMeansThePrimary()
    {
        var state = EaConsentController.StateFor(EaMailboxes.New, "/rbuergi/Settings?tab=x");
        EaConsentController.ParseState(state).Should().Be((EaMailboxes.New, "/rbuergi/Settings?tab=x"));

        var legacy = EaConsentController.StateFor(EaMailboxes.Primary, "/inbox");
        legacy.Should().NotStartWith(EaConsentController.AccountStatePrefix, "the primary keeps the pre-existing shape");
        EaConsentController.ParseState(legacy).Should().Be((EaMailboxes.Primary, "/inbox"));

        EaConsentController.ParseState("acct:../../Admin:%2Finbox").Account.Should().Be(EaMailboxes.Primary,
            "a state that left our process never names a path");
    }

    private static string IdToken(string claimsJson)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("""{"alg":"none"}""")}.{B64(claimsJson)}.sig";
    }

    [Fact]
    public void TheSignedInAddress_IsReadOffTheIdToken()
    {
        EaGraphAuth.SignedInAddress($$"""{"refresh_token":"r","id_token":"{{IdToken("""{"preferred_username":"max@fabrikam.example","name":"Max"}""")}}"}""")
            .Should().Be("max@fabrikam.example");
        EaGraphAuth.SignedInAddress($$"""{"id_token":"{{IdToken("""{"preferred_username":"Max","email":"max@acme.example"}""")}}"}""")
            .Should().Be("max@acme.example", "a username that is not an address falls through to email");
        EaGraphAuth.SignedInAddress("""{"refresh_token":"r"}""").Should().BeNull();
        EaGraphAuth.SignedInAddress("""{"id_token":"not-a-jwt"}""").Should().BeNull();
    }

    [Fact]
    public void AddingAMailbox_ShowsTheAccountPicker_AndAReconnectHintsTheAddress()
    {
        using var http = new HttpClient();
        var ea = NewAuth(http);
        var add = ea.BuildConsentUrl("s", "https://memex.example/auth/ea/callback", loginHint: null, selectAccount: true);
        add.Should().Contain("prompt=select_account").And.NotContain("prompt=consent").And.NotContain("login_hint");
        add.Should().Contain(Uri.EscapeDataString("openid profile email"));

        var reconnect = ea.BuildConsentUrl("s", "https://memex.example/auth/ea/callback", "max@acme.example", selectAccount: false);
        reconnect.Should().Contain("login_hint=max%40acme.example").And.NotContain("prompt=");
    }

    // ── On a real mesh ───────────────────────────────────────────────────────────────────────────

    private sealed class FixedMasterKey : IMasterKeyProvider
    {
        private readonly byte[] key = new byte[32];
        public byte[]? GetMasterKey() => key;
    }

    /// <summary>Answers every code exchange with a grant for the NEXT scripted address.</summary>
    private sealed class AddressTokenEndpoint(params string[] addresses) : HttpMessageHandler
    {
        private int next;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var address = addresses[Math.Min(Interlocked.Increment(ref next) - 1, addresses.Length - 1)];
            var body = $$"""{"access_token":"at","refresh_token":"rt-{{address}}","id_token":"{{IdToken($$"""{"preferred_username":"{{address}}"}""")}}"}""";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddEaCredentialType();

    private EaGraphAuth NewAuth(HttpClient http) => new(
        Mesh.ServiceProvider,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Microsoft:ClientId"] = "test-client",
            ["Authentication:Microsoft:ClientSecret"] = "test-secret",
        }).Build(),
        new ProviderKeyProtector(new FixedMasterKey()),
        http,
        Mesh.ServiceProvider.GetRequiredService<ILogger<EaGraphAuth>>());

    private static IObservable<ImmutableList<EaMailbox>> Until(
        EaGraphAuth auth, string user, Func<ImmutableList<EaMailbox>, bool> done) =>
        // The query index follows a write; wait for the listing to reflect it rather than reading once.
        Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => auth.GetMailboxes(user))
            .Where(done)
            .FirstAsync();

    [Fact]
    public async Task ASecondMailbox_IsStoredBesideThePrimary_ListedWithIt_AndDeduplicatedByAddress()
    {
        const string user = "ea-two-mailboxes-user";
        using var http = new HttpClient(new AddressTokenEndpoint(
            "max@fabrikam.example", "max@acme.example", "MAX@acme.example"));
        var auth = NewAuth(http);
        const string callback = "https://portal.test/auth/ea/callback";

        (await auth.ExchangeAndStore("c1", callback, user).Should().Within(TestTimeouts.Convergence).Emit()).Should().BeTrue();
        (await auth.ExchangeAndStore("c2", callback, user, EaMailboxes.New).Should().Within(TestTimeouts.Convergence).Emit()).Should().BeTrue();

        var two = await Until(auth, user, l => l.Count == 2).Should().Within(TestTimeouts.Convergence).Emit();
        two[0].IsPrimary.Should().BeTrue();
        two[0].Address.Should().Be("max@fabrikam.example");
        two[1].AccountId.Should().StartWith(EaMailboxes.AddedPrefix);
        two[1].Address.Should().Be("max@acme.example");
        two.Should().OnlyContain(m => m.Access.Connection == EaConnection.Connected);

        // Adding the SAME address again reconnects that mailbox instead of adding a third.
        (await auth.ExchangeAndStore("c3", callback, user, EaMailboxes.New).Should().Within(TestTimeouts.Convergence).Emit()).Should().BeTrue();
        await Task.Delay(500);
        var after = await auth.GetMailboxes(user).Should().Within(TestTimeouts.Convergence).Emit();
        string.Join(",", after.Select(m => m.AccountId)).Should().Be(string.Join(",", two.Select(m => m.AccountId)));

        // The legacy entry points still address the primary.
        (await auth.GetConnection(user).Should().Within(TestTimeouts.Convergence).Emit()).IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task RemovingThePrimary_KeepsTheAddedMailbox_AndRemovingTheAddedOneDeletesIt()
    {
        const string user = "ea-remove-mailboxes-user";
        using var http = new HttpClient(new AddressTokenEndpoint("max@fabrikam.example", "max@acme.example"));
        var auth = NewAuth(http);
        const string callback = "https://portal.test/auth/ea/callback";
        (await auth.ExchangeAndStore("c1", callback, user).Should().Within(TestTimeouts.Convergence).Emit()).Should().BeTrue();
        (await auth.ExchangeAndStore("c2", callback, user, EaMailboxes.New).Should().Within(TestTimeouts.Convergence).Emit()).Should().BeTrue();
        var added = (await Until(auth, user, l => l.Count == 2).Should().Within(TestTimeouts.Convergence).Emit())[1];

        (await auth.RemoveMailbox(user, EaMailboxes.Primary).Should().Within(TestTimeouts.Convergence).Emit()).Should().BeTrue();
        var left = await Until(auth, user, l => l.Count == 1).Should().Within(TestTimeouts.Convergence).Emit();
        left.Single().AccountId.Should().Be(added.AccountId,
            "the primary is emptied, never deleted — deleting it would cascade to the mailboxes beneath it");
        (await auth.GetConnection(user).Should().Within(TestTimeouts.Convergence).Emit())
            .Connection.Should().Be(EaConnection.NotConnected);
        (await auth.GetAccessToken(user, added.AccountId).Should().Within(TestTimeouts.Convergence).Emit())
            .Connection.Should().NotBe(EaConnection.NotConnected, "the added mailbox's grant is untouched");

        (await auth.RemoveMailbox(user, added.AccountId).Should().Within(TestTimeouts.Convergence).Emit()).Should().BeTrue();
        await Until(auth, user, l => l.Count == 0).Should().Within(TestTimeouts.Convergence).Emit();
    }

    [Fact]
    public async Task TheConnectRoute_WithAccountNew_AlwaysRunsTheConsent_WithTheAccountPicker()
    {
        var access = new AccessService();
        access.SetContext(new AccessContext { ObjectId = "user-1" });
        using var http = new HttpClient();
        var controller = new EaConsentController(NewAuth(http), access,
            Mesh.ServiceProvider.GetRequiredService<ILogger<EaConsentController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Request = { Scheme = "https", Host = new HostString("memex.example") } },
            },
        };

        var result = await controller.Connect(returnUrl: "/user-1/Settings", account: EaMailboxes.New, ct: CancellationToken.None);
        var url = result.Should().BeOfType<RedirectResult>().Which.Url;
        url.Should().Contain("prompt=select_account");
        url.Should().Contain(Uri.EscapeDataString(EaConsentController.StateFor(EaMailboxes.New, "/user-1/Settings")));

        (await controller.Connect(account: "../x", ct: CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
    }
}
