using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Mesh;
using MeshWeaver.AI;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins what the EA consent flow tells Microsoft and what it tells the user (#6082).
///
/// <para><b>No forced prompt.</b> <c>prompt=consent</c> made Microsoft show its consent dialog on
/// every authorize request. In a tenant where users may not consent to the mail scopes, that dialog
/// is always "request admin approval" — also after an admin granted tenant-wide consent, so the
/// user looped. Without the parameter Microsoft prompts only for consent that is actually missing.</para>
///
/// <para><b>A visible outcome.</b> The callback used to redirect to the return URL whatever happened,
/// so a refused consent or a failed code exchange looked exactly like success. The return URL now
/// carries <c>eaConnect=connected</c> or <c>eaConnect=failed&amp;reason=…</c>, and Microsoft's
/// <c>error_description</c> reaches the log.</para>
/// </summary>
public class EaConsentOutcomeTest
{
    private sealed class FakeEaGraphAuth(bool exchangeSucceeds) : IEaGraphAuth
    {
        public bool IsConfigured => true;
        public string ConnectPath => EaConsentController.ConnectPath;

        public string BuildConsentUrl(string state, string redirectUri) =>
            "https://login.microsoftonline.example/consent?state=" + state;

        public IObservable<bool> ExchangeAndStore(
            string code, string redirectUri, string userObjectId) => Observable.Return(exchangeSucceeds);

        public IObservable<EaGraphAccess> GetAccessToken(string userObjectId) =>
            Observable.Return(EaGraphAccess.NotConnected());

        public IObservable<EaGraphAccess> GetConnection(string userObjectId) =>
            Observable.Return(EaGraphAccess.NotConnected());
    }

    private sealed class PassThroughProtector : IProviderKeyProtector
    {
        public string? Protect(string? plaintext) => plaintext;
        public string? Unprotect(string? stored) => stored;
    }

    private static EaConsentController Controller(
        IEaGraphAuth ea, ILogger<EaConsentController> logger)
    {
        var access = new AccessService();
        access.SetContext(new AccessContext { ObjectId = "user-1" });
        return new EaConsentController(ea, access, logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request = { Scheme = "https", Host = new HostString("memex.example") },
                },
            },
        };
    }

    [Fact]
    public void The_consent_url_does_not_force_the_consent_dialog()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Microsoft:ClientId"] = "client-1",
                ["Authentication:Microsoft:ClientSecret"] = "secret-1",
                [MicrosoftTenant.ConfigurationKey] = "11111111-2222-3333-4444-555555555555",
            })
            .Build();
        using var http = new HttpClient();
        var ea = new EaGraphAuth(
            new ServiceCollection().BuildServiceProvider(), configuration, new PassThroughProtector(), http);

        var url = ea.BuildConsentUrl("state-1", "https://memex.example/auth/ea/callback");

        url.Should().NotContain("prompt=",
            "a forced prompt turns into an admin-approval request in tenants where users may not "
            + "consent, even after an admin granted consent for the whole tenant (#6082)");
    }

    [Fact]
    public async Task A_consent_Microsoft_refused_reports_failed_and_logs_the_description()
    {
        var logger = new CapturingLogger<EaConsentController>();

        var result = await Controller(new FakeEaGraphAuth(exchangeSucceeds: true), logger).Callback(
            code: null, state: "/inbox", error: "access_denied",
            errorDescription: "AADSTS65004: User declined to consent", ct: CancellationToken.None);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/inbox?eaConnect=failed&reason=microsoft");
        logger.Lines(LogLevel.Warning).Should().Contain(line => line.Contains("AADSTS65004"),
            "Microsoft's error_description names the cause; the error code alone does not");
    }

    [Fact]
    public async Task A_failed_code_exchange_reports_failed()
    {
        var logger = new CapturingLogger<EaConsentController>();

        var result = await Controller(new FakeEaGraphAuth(exchangeSucceeds: false), logger).Callback(
            code: "code-1", state: "/inbox", error: null, errorDescription: null, ct: CancellationToken.None);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/inbox?eaConnect=failed&reason=exchange");
    }

    [Fact]
    public async Task A_stored_grant_reports_connected()
    {
        var logger = new CapturingLogger<EaConsentController>();

        var result = await Controller(new FakeEaGraphAuth(exchangeSucceeds: true), logger).Callback(
            code: "code-1", state: "/inbox?tab=mail", error: null, errorDescription: null,
            ct: CancellationToken.None);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/inbox?tab=mail&eaConnect=connected",
                "a return URL that already has a query string gets the outcome appended to it");
    }
}
