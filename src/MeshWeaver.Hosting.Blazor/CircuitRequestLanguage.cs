using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// Carries the language of the <c>/_blazor</c> connection into the circuit that is being built on
/// it, so <see cref="CircuitAccessHandler"/> can seed an ANONYMOUS visitor's
/// <c>AccessContext.Locale</c> from <c>Accept-Language</c>.
///
/// <para>🚨 <b>Why this exists instead of just reading <c>IHttpContextAccessor</c>.</b> The accessor
/// works over WebSockets — the upgrade request stays in flight for the connection's whole life, so
/// its <c>HttpContext</c> is still there when Blazor constructs the circuit's handlers. Under LONG
/// POLLING it does not: every poll is a separate request, and ASP.NET nulls the accessor's holder
/// when a request completes, so the handler sees nothing and the visitor silently gets English. A
/// browser behind a proxy that blocks WebSockets falls back to long polling, which is precisely a
/// corporate-network shape — so "works on WebSockets" would have meant the fix reaches most
/// visitors and silently misses the rest. Measured, not assumed: the long-polling rows of
/// <c>AnonymousCircuitLocaleSeedTest</c> fail against the accessor-only version.</para>
///
/// <para>SignalR, unlike ASP.NET's request accessor, keeps an <c>IHttpContextFeature</c> on the
/// CONNECTION and refreshes it per request, so <c>HubCallerContext.GetHttpContext()</c> answers for
/// every transport. <see cref="CircuitRequestLanguageFilter"/> reads it in the hub-invocation flow
/// that <i>creates</i> the circuit handlers, and publishes it here.</para>
/// </summary>
public sealed class CircuitRequestLanguage
{
    // AsyncLocal, and an INSTANCE field on a mesh-scoped singleton — the same shape AccessService
    // uses for the per-circuit identity, and for the same reason: the value must be visible to the
    // work this hub invocation dispatches and to nothing else. Never static: two circuits opening
    // concurrently must not see each other's language.
    private readonly AsyncLocal<string?> negotiated = new();

    /// <summary>
    /// The supported language tag this connection's browser asked for, or <see langword="null"/>
    /// when it asked for nothing we ship — or when the reader is not on a hub-invocation flow at
    /// all, in which case the caller falls back and, ultimately, renders English.
    /// </summary>
    public string? Current => negotiated.Value;

    /// <summary>
    /// Publishes the language for the remainder of the current hub invocation, including everything
    /// it awaits or dispatches. Called only by <see cref="CircuitRequestLanguageFilter"/>.
    /// </summary>
    public void Set(string? locale) => negotiated.Value = locale;
}

/// <summary>
/// Reads <c>Accept-Language</c> off the SignalR connection's HTTP context and publishes it on
/// <see cref="CircuitRequestLanguage"/> for the duration of each hub invocation.
///
/// <para>Registered as a GLOBAL SignalR filter, because Blazor's <c>ComponentHub</c> is
/// <c>internal</c> and cannot be named in a per-hub registration. On any other hub this is a
/// two-field no-op.</para>
///
/// <para><b>Why a filter and not the handler itself.</b> Blazor builds the circuit's DI scope and
/// constructs its <c>CircuitHandler</c>s inside a hub method — <c>StartCircuit</c> for a classic
/// Blazor Server payload, the first <c>UpdateRootComponents</c> for a Blazor Web App. A filter wraps
/// exactly that invocation, so the value set here flows into the construction (and into the
/// circuit-opened lifecycle it dispatches) on every transport, without depending on how long the
/// underlying HTTP request happens to live.</para>
/// </summary>
public sealed class CircuitRequestLanguageFilter(CircuitRequestLanguage language) : IHubFilter
{
    /// <inheritdoc />
    public ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        Publish(invocationContext.Context);
        return next(invocationContext);
    }

    /// <inheritdoc />
    public Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        Publish(context.Context);
        return next(context);
    }

    private void Publish(HubCallerContext context) =>
        language.Set(Locales.Negotiate(
            context.GetHttpContext()?.Request.Headers.AcceptLanguage.ToString()));
}
