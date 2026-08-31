#pragma warning disable CS1591

using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// 🚨 <b>#2836 — a transient 503 stranded package reconciliation for the whole life of the pod.</b>
///
/// <para><see cref="RegistryUpdateReconciler"/> deliberately has no poll timer: the bound it
/// promises is "a consumer learns on its NEXT BOOT", so the boot's bounded retry is the only thing
/// standing between a hiccup and a pod that never reconciles again. That retry declined to re-ask
/// on <see cref="InvalidOperationException"/>, reasoning it meant "a definite HTTP answer
/// (401/403/404) — the same answer will come back in two seconds".</para>
///
/// <para><b>The reasoning was sound about the statuses it named and false about the type.</b>
/// <see cref="RegistryPackageSource"/> threw that one bare type for EVERY non-2xx, keeping the
/// status only inside the message text. So a <c>503 Instance-key resolution is temporarily
/// unavailable — retry shortly</c> — the exact condition the retry exists for — was excluded by a
/// rule written for permission errors, and the installed set went stale silently, because the
/// portal itself stays up.</para>
///
/// <para>🚨 <b>The two halves are tested TOGETHER on purpose.</b> A policy that branches correctly
/// on a status the throw site never records is still broken, and each half passes its own test
/// while the system fails. So the last test feeds a REAL thrown exception, produced by a real HTTP
/// round trip, into the REAL predicate.</para>
/// </summary>
public class RegistryTransientFailureRetryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Answers with the status named in the FIRST PATH SEGMENT of the request
    /// (<c>https://registry.example/503/api/plugins</c> → 503), so each test picks its own
    /// condition through the URL it constructs the source with — no shared mutable state, and
    /// nothing to reset between tests.
    /// </summary>
    private sealed class StatusFromUrlHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.Segments;
            var status = segments.Length > 1
                && int.TryParse(segments[1].TrimEnd('/'), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var code)
                ? (HttpStatusCode)code
                : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    status == HttpStatusCode.ServiceUnavailable
                        ? "Instance-key resolution is temporarily unavailable — retry shortly"
                        : $"canned {(int)status}"),
                RequestMessage = request,
            });
        }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .AddHttpClient(InstanceRegistrationClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new StatusFromUrlHandler())
                .Services);

    private RegistryPackageSource SourceAnswering(HttpStatusCode status) =>
        new(Mesh, $"https://registry.example/{(int)status}", "mwi_key");

    // ─────────────────────────────────────────────────────────────────────────────
    // 1. The POLICY — which answers are worth re-asking.
    // ─────────────────────────────────────────────────────────────────────────────

    public static TheoryData<HttpStatusCode> TransientStatuses =>
    [
        HttpStatusCode.ServiceUnavailable,   // 503 — the status of #2836
        HttpStatusCode.TooManyRequests,      // 429 — the server explicitly invites a retry
        HttpStatusCode.BadGateway,           // 502 — may not have reached the app at all
        HttpStatusCode.GatewayTimeout,       // 504
        HttpStatusCode.InternalServerError,  // 500
        HttpStatusCode.RequestTimeout,       // 408
    ];

    [Theory]
    [MemberData(nameof(TransientStatuses))]
    public void ATransientAnswer_IsReAsked(HttpStatusCode status) =>
        Assert.True(
            RegistryUpdateReconciler.ShouldRetryFeedRead(
                new RegistryResponseException(status, $"failed ({(int)status})")),
            $"{(int)status} may clear on its own — not re-asking it strands the whole boot's "
            + "reconcile, and there is no poll behind this service to recover it");

    public static TheoryData<HttpStatusCode> DefiniteStatuses =>
    [
        HttpStatusCode.Unauthorized,   // 401 — a bad or revoked instance key
        HttpStatusCode.Forbidden,      // 403 — a grant this instance does not hold
        HttpStatusCode.NotFound,       // 404 — no such feed
        HttpStatusCode.Conflict,       // 409
        HttpStatusCode.BadRequest,     // 400
    ];

    [Theory]
    [MemberData(nameof(DefiniteStatuses))]
    public void ADefiniteRefusal_IsNotReAsked(HttpStatusCode status) =>
        Assert.False(
            RegistryUpdateReconciler.ShouldRetryFeedRead(
                new RegistryResponseException(status, $"failed ({(int)status})")),
            $"{(int)status} is a decision the registry has already made; re-asking only delays "
            + "the log line that names it");

    [Fact]
    public void ATransportFault_IsReAsked()
    {
        // The original #1500 case: the read never reached an HTTP answer at all.
        Assert.True(RegistryUpdateReconciler.ShouldRetryFeedRead(
            new HttpRequestException("connection reset")));
        Assert.True(RegistryUpdateReconciler.ShouldRetryFeedRead(
            new TimeoutException("the feed read timed out")));
    }

    [Fact]
    public void ANonHttpInvalidOperation_IsStillNotReAsked() =>
        // The pre-existing exclusion must survive: a malformed payload or a misconfiguration —
        // anything reported as a plain InvalidOperationException — is not a transient condition.
        Assert.False(RegistryUpdateReconciler.ShouldRetryFeedRead(
            new InvalidOperationException("the feed returned no packages array")));

    // ─────────────────────────────────────────────────────────────────────────────
    // 2. The THROW SITE — the status has to survive as data, or the policy is blind.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFeedRead_ThatIsRefused_CarriesTheStatus()
    {
        var thrown = await Assert.ThrowsAsync<RegistryResponseException>(async () =>
            await SourceAnswering(HttpStatusCode.Forbidden)
                .ListPackages("HEAD").FirstAsync().Timeout(TestTimeouts.Quick));

        Assert.Equal(HttpStatusCode.Forbidden, thrown.StatusCode);
        Assert.False(thrown.IsTransientFailure);
    }

    /// <summary>
    /// 🚨 The composition, and the test that actually pins #2836: a REAL 503 from a REAL feed read,
    /// fed to the REAL retry predicate. Before the fix the throw site produced a bare
    /// <see cref="InvalidOperationException"/> and the predicate answered <c>false</c> — the boot
    /// gave up on its first and only attempt. Testing either half alone goes on passing.
    /// </summary>
    [Fact]
    public async Task A503FromTheRealFeedRead_IsReAskedByTheRealPredicate()
    {
        var thrown = await Assert.ThrowsAsync<RegistryResponseException>(async () =>
            await SourceAnswering(HttpStatusCode.ServiceUnavailable)
                .ListPackages("HEAD").FirstAsync().Timeout(TestTimeouts.Quick));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, thrown.StatusCode);
        Assert.Contains("retry shortly", thrown.Message, StringComparison.Ordinal);
        Assert.True(RegistryUpdateReconciler.ShouldRetryFeedRead(thrown),
            "a 503 the registry itself marks retryable must be re-asked inside the boot window; "
            + "this service has no poll behind it, so declining leaves the installed set stale "
            + "for the whole life of the pod (#2836)");
    }
}
