using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.ControlLane;

/// <summary>What a target answered a POST with: the status, the body, and the body's signature.</summary>
/// <param name="StatusCode">The HTTP status.</param>
/// <param name="Body">The raw answer body.</param>
/// <param name="Signature">The answer's <c>X-Hub-Signature-256</c>, when the target signed it.</param>
public sealed record ControlLaneAnswer(int StatusCode, string Body, string? Signature);

/// <summary>How the control instance reaches a target's lane endpoint. HTTP by default; a test
/// hands the body to a second mesh's <see cref="ControlLaneReceiver"/> in-process.</summary>
public interface IControlLaneTransport
{
    /// <summary>POSTs one signed request body. Errors only when nothing answered.</summary>
    IObservable<ControlLaneAnswer> Post(Uri endpoint, string body, string signature);
}

/// <summary>
/// 🚨 THE CONTROL HALF of the lane (Doc/Architecture/ControlLane): signs a request with the TARGET
/// deployment's own key and posts it; verifies the target's signed answer; and verifies a report the
/// target sent back. Refuses to sign with anything but the deployment's own key
/// (<see cref="ControlLaneKeys.ControlKeyFor"/>).
/// </summary>
public static class ControlLaneClient
{
    /// <summary>The target's lane endpoint.</summary>
    public const string EndpointRoute = "/api/control-lane";

    /// <summary>How long a request is good for, from signing.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The endpoint of the instance at <paramref name="host"/> (a bare host name, or an absolute
    /// https URL). Null when it is neither. Pure.
    /// </summary>
    public static Uri? EndpointOf(string? host)
    {
        var value = (host ?? "").Trim().TrimEnd('/');
        if (value.Length == 0)
            return null;
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;
        return Uri.TryCreate(value + EndpointRoute, UriKind.Absolute, out var uri)
               && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0
            ? uri
            : null;
    }

    /// <summary>A fresh request id — 32 hex characters.</summary>
    public static string NewRequestId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Signs <paramref name="request"/> with <c>Hosting:PlatformWebhookSecret:{deployment}</c> and
    /// posts it to <paramref name="endpoint"/>. Emits the target's receipt — ACCEPTED only when the
    /// target answered 202 with a body it signed with the same key (so a 2xx from anything else is
    /// not a hand-over). Errors when there is no key of the deployment's own, or nothing answered.
    /// Cold.
    /// </summary>
    public static IObservable<ControlLaneReceipt> Send(IMessageHub hub, ControlLaneRequest request, Uri endpoint) =>
        Observable.Defer(() =>
        {
            var configuration = hub.ServiceProvider.GetService<IConfiguration>();
            var (key, refusal) = ControlLaneKeys.ControlKeyFor(configuration, request.Deployment);
            if (key is null)
                return Observable.Throw<ControlLaneReceipt>(new InvalidOperationException(refusal));
            var body = ControlLaneWire.Body(request);
            var transport = hub.ServiceProvider.GetRequiredService<IControlLaneTransport>();
            return transport.Post(endpoint, body, ControlLaneWire.Sign(body, key))
                .Take(1)
                .Select(answer => ReceiptOf(answer, key, request.RequestId, endpoint));
        });

    /// <summary>
    /// Signs a FORWARDED EVENT with the target deployment's own key and posts it to the target's
    /// lane endpoint. Emits the receipt — ACCEPTED only for a 202 the target signed with the same
    /// key naming this event. Errors when there is no key of the deployment's own, or nothing
    /// answered. Cold.
    /// </summary>
    /// <param name="hub">The control instance's hub.</param>
    /// <param name="evt">The event to forward.</param>
    /// <param name="endpoint">The target's lane endpoint (<see cref="EndpointOf"/>).</param>
    public static IObservable<ControlLaneReceipt> Forward(IMessageHub hub, ControlLaneEvent evt, Uri endpoint) =>
        Observable.Defer(() =>
        {
            var configuration = hub.ServiceProvider.GetService<IConfiguration>();
            var (key, refusal) = ControlLaneKeys.ControlKeyFor(configuration, evt.Deployment);
            if (key is null)
                return Observable.Throw<ControlLaneReceipt>(new InvalidOperationException(refusal));
            var body = ControlLaneWire.Body(evt);
            var transport = hub.ServiceProvider.GetRequiredService<IControlLaneTransport>();
            return transport.Post(endpoint, body, ControlLaneWire.Sign(body, key))
                .Take(1)
                .Select(answer => ReceiptOf(answer, key, evt.EventId, endpoint));
        });

    /// <summary>
    /// A forwarded event for <paramref name="deployment"/>, valid for <see cref="DefaultLifetime"/>
    /// from <paramref name="now"/>, with a fresh id. Pure but for the id.
    /// </summary>
    /// <param name="deployment">The target deployment id.</param>
    /// <param name="source">The event's origin (<see cref="ControlLaneEventSource"/>).</param>
    /// <param name="name">The origin's event name.</param>
    /// <param name="target">The target's declared inbox owner.</param>
    /// <param name="payload">The verified body, verbatim.</param>
    /// <param name="now">Now.</param>
    public static ControlLaneEvent NewEvent(string deployment, string source, string name, string target, string payload, DateTimeOffset now) =>
        new()
        {
            EventId = NewRequestId(),
            Deployment = deployment,
            Source = source,
            Name = name,
            Target = target,
            Payload = payload,
            IssuedAt = now,
            ExpiresAt = now + DefaultLifetime,
        };

    /// <summary>
    /// The receipt an answer carries. A 202 counts as ACCEPTED only when its body verifies with the
    /// deployment's key and names this request — an unsigned 2xx, or one for another request, is a
    /// refusal naming what came back. Pure.
    /// </summary>
    public static ControlLaneReceipt ReceiptOf(ControlLaneAnswer answer, string key, string requestId, Uri endpoint)
    {
        var (verdict, why, answeredId) = AnswerOf(answer.Body);
        if (answer.StatusCode == 202)
        {
            if (!ControlLaneWire.Verify(answer.Signature, answer.Body, key))
                return new ControlLaneReceipt(ControlLaneVerdict.Refused,
                    $"{endpoint} answered 202 but its answer is not signed with this deployment's key — whatever answered "
                    + "does not hold it, so this is not a hand-over", requestId);
            if (verdict != ControlLaneVerdict.Accepted || !string.Equals(answeredId, requestId, StringComparison.Ordinal))
                return new ControlLaneReceipt(ControlLaneVerdict.Refused,
                    $"{endpoint} answered 202 for request '{answeredId}' with verdict '{verdict}', not an acceptance of {requestId}", requestId);
            return new ControlLaneReceipt(ControlLaneVerdict.Accepted, null, requestId);
        }
        return new ControlLaneReceipt(verdict ?? $"http-{answer.StatusCode}",
            $"{endpoint} answered {answer.StatusCode}"
            + (why is { Length: > 0 } ? $": {why}" : "")
            + (answer.StatusCode == 401 ? " — the target's ControlLane:Key and this instance's key for the deployment are not byte-identical" : "")
            + (answer.StatusCode == 404 ? " — the target does not serve the control lane (an image without it)" : ""),
            requestId);
    }

    /// <summary>The body a target answers with. Pure.</summary>
    public static string AnswerBody(ControlLaneReceipt receipt) =>
        ControlLaneWire.Body(new { verdict = receipt.Verdict, requestId = receipt.RequestId, why = receipt.Why });

    private static (string? Verdict, string? Why, string? RequestId) AnswerOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null, null);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null, null);
            return (Field(root, "verdict"), Field(root, "why"), Field(root, "requestId"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }

        static string? Field(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>
    /// Verifies a report the control inbox stored: it must parse as a report, name a deployment
    /// this control instance holds a key of its OWN for, verify with EXACTLY that key, and — when it
    /// carries a plan — state the digest this code computes over it. Returns the report or the
    /// refusal. Pure over the configuration.
    /// </summary>
    public static (ControlLaneReport? Report, string? Refusal) VerifyReport(
        IConfiguration? configuration, string? signature, string body)
    {
        var report = ControlLaneWire.ParseReport(body);
        if (report is null)
            return (null, "the body is not a control-lane report");
        var (key, refusal) = ControlLaneKeys.ControlKeyFor(configuration, report.Deployment);
        if (key is null)
            return (null, refusal);
        if (!ControlLaneWire.Verify(signature, body, key))
            return (null, $"the report names '{report.Deployment}' but does not verify with that deployment's own key");
        if (report.Plan is { } plan && !string.Equals(plan.Digest(), report.PlanDigest, StringComparison.Ordinal))
            return (null, $"the report states plan digest {report.PlanDigest}, and this control instance computes "
                          + $"{plan.Digest()} over the same steps — the two digest implementations have drifted");
        return (report, null);
    }
}

/// <summary>The default transport: an HTTPS POST on the <c>Http</c> I/O pool.</summary>
public sealed class HttpControlLaneTransport : IControlLaneTransport, IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly IIoPool pool;

    /// <summary>Created by DI.</summary>
    public HttpControlLaneTransport(IMessageHub hub) =>
        pool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;

    /// <inheritdoc />
    public IObservable<ControlLaneAnswer> Post(Uri endpoint, string body, string signature) =>
        pool.Invoke(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(ControlLaneWire.SignatureHeader, signature);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var answerSignature = response.Headers.TryGetValues(ControlLaneWire.SignatureHeader, out var values)
                ? values.FirstOrDefault()
                : null;
            return new ControlLaneAnswer((int)response.StatusCode, text, answerSignature);
        });

    /// <inheritdoc />
    public void Dispose() => http.Dispose();
}

/// <summary>
/// The default report sink: a signed POST into the control instance's inbox —
/// <c>Hosting:ControlInbox:Url</c>, or <c>Hosting:ReportTo</c> + <c>/api/hooks/Hosting/PlatformBuilds</c>
/// — the SAME inbox a self-update announcement goes to. Only an answer of
/// <c>{"status":"accepted","signature":"verified"}</c> is a delivery (#3312).
/// </summary>
public sealed class HttpControlLaneReportSink : IControlLaneReportSink, IDisposable
{
    /// <summary>The declared control inbox URL.</summary>
    public const string InboxUrlKey = "Hosting:ControlInbox:Url";

    /// <summary>The control instance's base URL.</summary>
    public const string ReportToKey = "Hosting:ReportTo";

    /// <summary>The inbox route appended to <see cref="ReportToKey"/>.</summary>
    public const string InboxRoute = "/api/hooks/Hosting/PlatformBuilds";

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly IIoPool pool;

    /// <summary>Created by DI.</summary>
    public HttpControlLaneReportSink(IMessageHub hub) =>
        pool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;

    /// <summary>The control inbox URL, or null when neither key names one. Pure.</summary>
    public static string? InboxUrl(IConfiguration? configuration)
    {
        var declared = configuration?[InboxUrlKey]?.Trim();
        if (!string.IsNullOrEmpty(declared))
            return declared;
        var control = configuration?[ReportToKey]?.Trim().TrimEnd('/');
        return string.IsNullOrEmpty(control) ? null : control + InboxRoute;
    }

    /// <inheritdoc />
    public IObservable<System.Reactive.Unit> Send(IMessageHub hub, ControlLaneReport report, string body, string signature)
    {
        var url = InboxUrl(hub.ServiceProvider.GetService<IConfiguration>());
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Observable.Throw<System.Reactive.Unit>(new InvalidOperationException(
                $"no control inbox: neither {InboxUrlKey} nor {ReportToKey} names the control instance, so the report cannot be sent"));
        return pool.Invoke(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(ControlLaneWire.SignatureHeader, signature);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || !AcceptedVerified(text))
                throw new InvalidOperationException(
                    $"the control inbox at {uri} answered {(int)response.StatusCode} {text}".TrimEnd()
                    + ((int)response.StatusCode == 401
                        ? " — the control instance holds no key for this deployment that verifies it (Hosting:PlatformWebhookSecret:{deployment})"
                        : ""));
            return System.Reactive.Unit.Default;
        });
    }

    /// <summary>
    /// Whether an inbox answer says the delivery was ACCEPTED having VERIFIED its signature — both
    /// halves; "not-required" means the control instance checked nothing and is not a delivery. Pure.
    /// </summary>
    public static bool AcceptedVerified(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return false;
        try
        {
            using var document = JsonDocument.Parse(answer);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                   && status.GetString() == "accepted"
                   && root.TryGetProperty("signature", out var signature) && signature.ValueKind == JsonValueKind.String
                   && signature.GetString() == "verified";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => http.Dispose();
}
