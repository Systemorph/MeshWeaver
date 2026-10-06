using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 The ONE place that decides whether a registry answer (an HTTP status) or a registry fault
/// (an exception) is TRANSIENT — a condition that may clear on its own, so the work that met it is
/// retried — or DECIDED — an answer the registry will give identically next time, so the work is
/// final. A module reload records a transient outcome as <c>Faulted</c> (re-armed by the reconcile
/// pass, never final) and a decided one as <c>Failed</c> (<c>Doc/Architecture/ModuleReload</c>).
///
/// <para><b>Transient statuses:</b> every 5xx (the server failed to serve a request it accepts —
/// 500, 502 Bad Gateway, 503 Service Unavailable, 504 Gateway Timeout, …), plus 408 Request
/// Timeout and 429 Too Many Requests, where the server explicitly invites a retry. This is the
/// conventional transient-HTTP set (the one Polly's <c>HandleTransientHttpError</c> uses), so the
/// boundary is a published convention rather than a judgement re-litigated at each call site.
/// Everything else — 401, 403, 404, 409, … — is a decision the registry has already made.</para>
///
/// <para><b>Transient faults:</b> the transfer never reached an answer — a request timeout, a
/// connection reset or refused, a DNS or socket failure, an I/O error mid-body — or reached one of
/// the transient statuses above (an <see cref="HttpRequestException"/> carrying it, a
/// <see cref="RegistryResponseException"/>). Decided faults: a digest mismatch
/// (<see cref="OciDigestMismatchException"/> — the registry served bytes that are not the
/// artifact), and an <see cref="InvalidOperationException"/>, which the registry clients throw for
/// a definite refusal (401/403/404, a manifest with no bundle layer).</para>
///
/// <para>Callers ask HERE, never re-derive the set: a second copy of it is how a 503 once became
/// "a definite answer" (Systemorph/MeshWeaver#2836) and a bundle download answering 503 became a
/// final reload failure (MeshWeaver#6172).</para>
/// </summary>
public static class TransientRegistryFailure
{
    /// <summary>Whether <paramref name="status"/> may clear on its own — see the class summary.</summary>
    public static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500
        || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    /// <summary>Whether <paramref name="fault"/> may clear on its own — see the class summary.</summary>
    public static bool IsTransient(Exception fault) => fault switch
    {
        OciDigestMismatchException => false,
        RegistryResponseException answered => IsTransient(answered.StatusCode),
        HttpRequestException { StatusCode: { } status } => IsTransient(status),
        // A transport failure with no status: the connection was reset or refused, DNS failed.
        HttpRequestException => true,
        TimeoutException or OperationCanceledException => true,
        SocketException or IOException => true,
        // The registry clients' definite refusal (401/403/404, a malformed artifact).
        InvalidOperationException => false,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0
                                        && aggregate.InnerExceptions.All(IsTransient),
        // Anything else is a crash, and a crash is never final.
        _ => true,
    };
}
