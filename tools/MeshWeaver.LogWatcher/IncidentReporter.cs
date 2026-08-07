using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Observability;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.LogWatcher;

/// <summary>
/// Delivers reports to the portal's <c>/api/log-incidents</c> endpoint.
///
/// <para>Delivery is <b>at-least-once</b> and the report carries a stable fingerprint, so a
/// redelivery folds into the existing incident instead of opening a second ticket. That is what
/// lets this side be simple: it can retry freely, because the portal — which owns the incident
/// identity — makes retrying harmless.</para>
/// </summary>
public sealed class IncidentReporter(
    HttpClient http,
    LogWatcherOptions options,
    IIoPool pool,
    ILogger<IncidentReporter>? logger = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Whether a delivery attempt succeeded. A 4xx other than 429 is <b>permanent</b> — a malformed
    /// or unauthorized report will not become valid by being sent again, so it is dropped with a
    /// loud log rather than retried until the disk fills.
    /// </summary>
    public enum Delivery
    {
        /// <summary>Accepted by the portal.</summary>
        Accepted,

        /// <summary>Failed, but worth retrying (portal down, 5xx, 429, network error).</summary>
        Retry,

        /// <summary>Failed permanently — retrying cannot help.</summary>
        Rejected,
    }

    /// <summary>Posts one report. Never throws: the outcome is the return value.</summary>
    public IObservable<Delivery> Send(LogIncidentReport report) =>
        pool.Invoke(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ReportUrl())
            {
                Content = JsonContent.Create(report, options: Json),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.IngestToken);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                logger?.LogInformation("Reported {Fingerprint} ({Category}) — {Status}",
                    report.Fingerprint, report.Category, (int)response.StatusCode);
                return Delivery.Accepted;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (IsPermanent(response.StatusCode))
            {
                logger?.LogError(
                    "Portal REJECTED report {Fingerprint} with {Status}: {Body}. Dropping it — this "
                    + "red log will not be ticketed.",
                    report.Fingerprint, (int)response.StatusCode, Trim(body));
                return Delivery.Rejected;
            }

            logger?.LogWarning("Portal returned {Status} for {Fingerprint}: {Body}. Will retry.",
                (int)response.StatusCode, report.Fingerprint, Trim(body));
            return Delivery.Retry;
        }).Catch((Exception ex) =>
        {
            logger?.LogWarning(ex, "Could not reach the portal to report {Fingerprint}. Will retry.",
                report.Fingerprint);
            return Observable.Return(Delivery.Retry);
        });

    /// <summary>4xx means the request itself is wrong — except 429, which is "not now".</summary>
    private static bool IsPermanent(HttpStatusCode status) =>
        (int)status is >= 400 and < 500 && status != HttpStatusCode.TooManyRequests;

    private string ReportUrl() =>
        $"{options.PortalUrl!.TrimEnd('/')}/api/log-incidents";

    private static string Trim(string body) =>
        body.Length > 400 ? body[..400] + "…" : body;
}
