using System.Net;

namespace MeshWeaver.AI;

/// <summary>
/// Static, pure classification of the failures a language-model provider can end a round with —
/// factored out of <c>ThreadExecution</c> so the decision "is this a legible, nameable condition or
/// an unknown engineering fault?" is unit-testable without a mesh, a thread, or a live endpoint.
///
/// <para>Why this exists (#476): when a round dies on the provider, <c>ThreadExecution</c> used to
/// paste <c>ex.Message</c> straight into the response cell's Text AND Summary. For the two failures
/// the portal actually hits that message is a raw transport dump — Azure/ClientModel render
/// <c>"Status: 429 (Too Many Requests) ErrorCode: RateLimitReached"</c> followed by the response body
/// and the COMPLETE HTTP header block. So the thread's own record of why it failed was an
/// unreadable, English-only blob, and a round that had been silently moved onto a substitute model
/// (<c>AgentChatClient.ApplyStaleModelFallback</c>) reported a rate limit for a model the user never
/// picked, with nothing saying why that model was in play.</para>
///
/// <para>This classifier only names the CONDITION. The prose is built at write time by the caller,
/// from the round's own <c>AccessContext.Locale</c> — the same presentation/condition split
/// <c>AgentChatClient.HasNoUsableModel</c> uses. The raw provider text is never discarded: it stays
/// on the <c>LogError(ex, …)</c> that already precedes the terminal write. Classification changes
/// what the USER reads, never what the operator can see.</para>
/// </summary>
public static class ProviderFailureClassifier
{
    /// <summary>The banner Azure.Core and System.ClientModel both put in the exception message.</summary>
    private const string StatusMarker = "Status: ";

    /// <summary>
    /// The HTTP status a provider refused the round with, or <c>null</c> when the failure carries no
    /// recognisable transport status (an ordinary engineering fault — a serialization bug, a tool
    /// exception — which must keep reporting its own message verbatim).
    ///
    /// <para>Walks the whole inner-exception chain: the streaming pipeline wraps provider faults
    /// (Microsoft.Extensions.AI middleware, the agent framework's invocation wrapper), so the typed
    /// transport exception is rarely the outermost one.</para>
    ///
    /// <para>Three probes, in order of authority. <see cref="HttpRequestException.StatusCode"/> is
    /// typed and covers the plain-HTTP providers (Ollama, custom gateways). The message banner covers
    /// <c>Azure.RequestFailedException</c> and <c>System.ClientModel.ClientResultException</c> —
    /// matched on text deliberately rather than on those types, so this file stays free of a provider
    /// SDK dependency and keeps working for any client that renders the same conventional banner
    /// (which is precisely why the raw text is so unreadable in the first place).</para>
    /// </summary>
    public static int? TryGetProviderStatus(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: { } code })
                return (int)code;

            if (TryParseStatusBanner(e.Message) is { } fromBanner)
                return fromBanner;
        }
        return null;
    }

    /// <summary>
    /// True when the provider refused the round because the deployment is out of quota (HTTP 429).
    /// This is the failure #476 reports on the portal, and the one an operator must be able to read
    /// off the thread without opening a log.
    /// </summary>
    public static bool IsRateLimited(Exception? ex) => TryGetProviderStatus(ex) == 429;

    /// <summary>
    /// True when the provider itself faulted (HTTP 5xx) — e.g. the managed endpoint's
    /// <c>500 {"message":"Internal server error: 'NoneType' object has no attribute 'items'"}</c>,
    /// the second symptom in #476. Distinct from <see cref="IsRateLimited"/> because the remedy
    /// differs: a 5xx is the deployment misbehaving, a 429 is the deployment being over its budget.
    /// </summary>
    public static bool IsProviderUnavailable(Exception? ex)
        => TryGetProviderStatus(ex) is >= 500 and < 600;

    /// <summary>
    /// Reads <c>"Status: 429 (Too Many Requests)"</c> — the conventional Azure.Core /
    /// System.ClientModel banner — and returns the numeric status, or <c>null</c>.
    ///
    /// <para>Anchored on the digits IMMEDIATELY after the marker and bounded to three of them, so a
    /// body that merely mentions a status ("Status: ok") or a prose sentence containing the word
    /// yields nothing rather than a bogus code.</para>
    /// </summary>
    private static int? TryParseStatusBanner(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return null;
        var at = message.IndexOf(StatusMarker, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var start = at + StatusMarker.Length;
        var end = start;
        while (end < message.Length && end - start < 3 && char.IsAsciiDigit(message[end]))
            end++;
        if (end == start)
            return null;
        // A longer digit run is not an HTTP status (a token count, an id) — reject rather than
        // truncate it to the first three digits.
        if (end < message.Length && char.IsAsciiDigit(message[end]))
            return null;
        return int.Parse(message.AsSpan(start, end - start));
    }
}
