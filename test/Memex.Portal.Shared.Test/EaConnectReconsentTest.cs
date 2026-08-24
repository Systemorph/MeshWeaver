using System;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the consent flow's idempotence: a user whose Executive-Assistant grant is already stored
/// is NOT sent back through Microsoft's consent dialog when they hit <c>/auth/ea/connect</c> again.
///
/// <para><b>Why this test exists.</b> <c>BuildConsentUrl</c> deliberately carries
/// <c>prompt=consent</c> (the first connect must mint a refresh token for every scope), and
/// <c>Connect</c> used to redirect there unconditionally. Every visit to the connect link —
/// a second click on the EA's "please connect" hint, a bookmarked URL — re-ran the full Microsoft
/// consent, and users read the repeat dialog as "my consent was never saved" (reported 2026-08-23).
/// The grant WAS stored; only the entry point ignored it.</para>
/// </summary>
public class EaConnectReconsentTest
{
    private sealed class FakeEaGraphAuth(bool connected) : IEaGraphAuth
    {
        public bool ConsentUrlBuilt { get; private set; }

        public bool IsConfigured => true;
        public string ConnectPath => EaConsentController.ConnectPath;

        public string BuildConsentUrl(string state, string redirectUri)
        {
            ConsentUrlBuilt = true;
            return "https://login.microsoftonline.example/consent?state=" + state;
        }

        public Task<bool> ExchangeAndStoreAsync(
            string code, string redirectUri, string userObjectId, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<string?> GetAccessTokenAsync(string userObjectId, CancellationToken ct) =>
            Task.FromResult<string?>(connected ? "token" : null);

        public Task<bool> IsConnectedAsync(string userObjectId, CancellationToken ct) =>
            Task.FromResult(connected);
    }

    private static EaConsentController Controller(FakeEaGraphAuth ea)
    {
        var access = new AccessService();
        access.SetContext(new AccessContext { ObjectId = "user-1" });
        var controller = new EaConsentController(ea, access, NullLogger<EaConsentController>.Instance)
        {
            // A real request context so the not-connected branch can compose its callback URI.
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request = { Scheme = "https", Host = new HostString("memex.example") },
                },
            },
        };
        return controller;
    }

    [Fact]
    public async Task An_already_connected_user_bounces_back_without_a_Microsoft_round_trip()
    {
        var ea = new FakeEaGraphAuth(connected: true);

        var result = await Controller(ea).Connect(returnUrl: "/rbuergi");

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/rbuergi",
                "a stored grant means there is nothing to consent — the connect link must be "
                + "idempotent, not a forced prompt=consent round trip");
        ea.ConsentUrlBuilt.Should().BeFalse("the consent URL must not even be composed");
    }

    [Fact]
    public async Task Force_still_runs_the_full_consent_for_a_connected_user()
    {
        var ea = new FakeEaGraphAuth(connected: true);

        var result = await Controller(ea).Connect(returnUrl: "/rbuergi", force: true);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().StartWith("https://login.microsoftonline.example/consent",
                "?force=true is the deliberate re-consent escape hatch (scope additions, rotation)");
    }

    [Fact]
    public async Task A_not_yet_connected_user_is_sent_to_the_Microsoft_consent()
    {
        var ea = new FakeEaGraphAuth(connected: false);

        var result = await Controller(ea).Connect(returnUrl: "/rbuergi");

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().StartWith("https://login.microsoftonline.example/consent");
        ea.ConsentUrlBuilt.Should().BeTrue();
    }
}
