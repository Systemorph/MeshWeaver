using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// 🚨 THE ANONYMOUS VISITOR'S LANGUAGE, proven on a REAL Blazor circuit.
///
/// <para><b>What was broken.</b> Every localized string resolves explicitly off
/// <see cref="AccessContext.Locale"/> — deliberately, because a layout-area render hops the hub
/// scheduler and an ambient <c>CultureInfo.CurrentUICulture</c> would not survive it (one user's UI
/// would pick up another user's language). That field is populated from the signed-in user's
/// profile. An ANONYMOUS visitor has no profile, so it was <see langword="null"/> for every one of
/// them and the portal answered in English regardless of what their browser asked for. The audience
/// that decision was written for — a first-time visitor hitting a paywall, an invite, a public
/// course page — is anonymous BY DEFINITION, so the feature was inert for exactly them.</para>
///
/// <para><b>Why this test drives a real circuit instead of asserting a pure function.</b> The
/// portal's pages render through the Blazor CIRCUIT, not the SSR pass
/// (<c>Routes.razor</c> returns early until <c>BrowserDimensionWatcher</c> resolves the viewport, so
/// the server-rendered shell contains no page content at all). The only question that matters is
/// therefore whether the circuit can still see the <c>Accept-Language</c> header of the
/// <c>/_blazor</c> request that established it — and no unit test can answer that. A test of
/// <c>Locales.Negotiate</c> alone would stay green while the browser saw nothing, which is the exact
/// failure mode this suite exists to rule out. So: a real <c>WebApplication</c>, the real Blazor
/// <c>ComponentHub</c>, a real SignalR WebSocket carrying a real header, the real
/// <see cref="CircuitAccessHandler"/>, and the real per-circuit
/// <see cref="ICircuitContextAccessor"/> that the portal hub stamps on every post.</para>
///
/// <para><b>The ordering claim it also pins.</b> Blazor resolves the circuit's
/// <c>CircuitHandler</c>s — hence runs this handler's constructor, which is where the header is read
/// — and then runs <c>OnCircuitOpenedAsync</c> / <c>OnConnectionUpAsync</c> BEFORE it adds and
/// renders any root component. The probe below observes the identity in
/// <c>OnConnectionUpAsync</c>, i.e. at the last moment before the first render, so a seed that
/// arrived one render too late would fail here rather than look correct.</para>
/// </summary>
public class AnonymousCircuitLocaleSeedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Where the probe handler publishes the identity the real handler resolved.</summary>
    private sealed class CircuitIdentitySink
    {
        private readonly ReplaySubject<AccessContext?> resolved = new(1);
        public void Publish(AccessContext? context) => resolved.OnNext(context);

        /// <summary>The first identity any circuit resolved, or a timeout if none ever opened.</summary>
        public Task<AccessContext?> FirstAsync(CancellationToken ct) =>
            resolved.FirstAsync().Timeout(TimeSpan.FromSeconds(20)).ToTask(ct);
    }

    /// <summary>
    /// Reads what <see cref="CircuitAccessHandler"/> wrote onto the circuit-scoped accessor. Ordered
    /// AFTER it (<see cref="CircuitHandler.Order"/> 0), and observing in
    /// <c>OnConnectionUpAsync</c> — the last framework hook before the first component renders.
    /// </summary>
    private sealed class CircuitIdentityProbe(ICircuitContextAccessor accessor, CircuitIdentitySink sink)
        : CircuitHandler
    {
        public override int Order => 1000;

        public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
        {
            sink.Publish(accessor.UserContext);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Lets the SignalR test client speak plain JSON to Blazor's <c>ComponentHub</c>.
    ///
    /// <para>Blazor restricts that hub to its own <c>blazorpack</c> protocol, whose implementation is
    /// <c>internal</c> — so a test client cannot speak it. Widening the SERVER's accepted protocol
    /// list is the only seam, and it changes nothing about the code under test: the hub, the circuit
    /// factory, the circuit handlers and the HTTP request are all the production ones; only the wire
    /// encoding of two string arguments differs.</para>
    /// </summary>
    private sealed class AllowJsonProtocol<THub> : IPostConfigureOptions<HubOptions<THub>>
        where THub : Hub
    {
        public void PostConfigure(string? name, HubOptions<THub> options)
            => options.SupportedProtocols = ["json", "blazorpack"];
    }

    /// <summary>
    /// A <c>RootComponentOperationBatch</c> with no operations — the shape <c>blazor.web.js</c> sends,
    /// minus the data-protected component markers a test cannot forge. Zero operations is enough:
    /// the framework creates the circuit handlers and runs the circuit-opened / connection-up
    /// lifecycle on the FIRST <c>UpdateRootComponents</c> call, before it looks at the operations.
    /// </summary>
    private const string EmptyRootComponentBatch = """{"batchId":1,"operations":[]}""";

    private readonly CircuitIdentitySink sink = new();
    private WebApplication? portal;

    /// <summary>
    /// The portal's Blazor wiring, reduced to the pieces the circuit identity actually needs — the
    /// same registrations <c>AddBlazor()</c> makes, plus the probe. Everything that decides the
    /// answer (the circuit handler, the circuit-context accessor, the connection-language filter,
    /// the Blazor hub itself) is the real production type.
    /// </summary>
    private WebApplication BuildPortal()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(sink);
        builder.Services.AddHttpContextAccessor();
        // Exactly what AddBlazor() registers for the language seed — the connection-scoped source
        // plus the global SignalR filter that publishes it. Registering it here rather than calling
        // AddBlazor() keeps the harness minimal, but it must stay in step with AddBlazor: if these
        // two drift, the long-polling rows below are what notices.
        builder.Services.AddSingleton<CircuitRequestLanguage>();
        builder.Services.AddSingleton<CircuitRequestLanguageFilter>();
        builder.Services.Configure<HubOptions>(o => o.AddFilter<CircuitRequestLanguageFilter>());
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.TryAddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();
        builder.Services.AddScoped<ICircuitContextAccessor, CircuitContextAccessor>();
        builder.Services.AddScoped<CircuitAccessHandler>();
        builder.Services.AddScoped<CircuitHandler>(sp => sp.GetRequiredService<CircuitAccessHandler>());
        builder.Services.AddScoped<CircuitHandler, CircuitIdentityProbe>();

        // See AllowJsonProtocol — ComponentHub is internal, so the option has to be reached by name.
        var componentHub = typeof(CircuitHandler).Assembly
            .GetType("Microsoft.AspNetCore.Components.Server.ComponentHub")
            ?? throw new InvalidOperationException(
                "Blazor's ComponentHub type was not found — the SignalR entry point this test drives "
                + "has moved, and the test must be re-pointed rather than skipped.");
        builder.Services.AddSingleton(
            typeof(IPostConfigureOptions<>).MakeGenericType(
                typeof(HubOptions<>).MakeGenericType(componentHub)),
            typeof(AllowJsonProtocol<>).MakeGenericType(componentHub));

        var app = builder.Build();
        app.UseAntiforgery();
        app.MapRazorComponents<RouterProbeApp>().AddInteractiveServerRenderMode();
        return app;
    }

    /// <inheritdoc />
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        portal = BuildPortal();
        await portal.StartAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (portal is not null)
            await portal.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>
    /// Opens a real Blazor circuit over a real SignalR connection whose request carries
    /// <paramref name="acceptLanguage"/>, and returns the identity the circuit resolved.
    ///
    /// <para>The two hub calls mirror what <c>blazor.web.js</c> does: <c>StartCircuit</c> with an
    /// EMPTY component list (a Blazor Web App sends no initial components — they arrive via
    /// <c>UpdateRootComponents</c>), then <c>UpdateRootComponents</c>, which is where the framework
    /// deliberately defers creating the circuit handlers to. That deferral is the reason the header
    /// is still readable: this whole exchange runs inside the live <c>/_blazor</c> request.</para>
    ///
    /// <para><paramref name="transport"/> is a real dimension, not thoroughness for its own sake: a
    /// browser behind a proxy that blocks WebSockets falls back to long polling, where each poll is
    /// a SEPARATE request. If the seed only survived one transport, the feature would work for most
    /// visitors and silently not for the rest — and "most" is not something a test should leave
    /// unstated.</para>
    /// </summary>
    private async Task<AccessContext?> OpenCircuitAsync(
        string? acceptLanguage, HttpTransportType transport = HttpTransportType.WebSockets)
    {
        var server = portal!.GetTestServer();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "_blazor"), o =>
            {
                o.Transports = transport;
                // WebSockets: skip the negotiate round-trip so the handshake IS the one request that
                // could have supplied the language — no ambiguity about where it came from. Long
                // polling has to negotiate, so the header goes on every request the client makes.
                o.SkipNegotiation = transport == HttpTransportType.WebSockets;
                if (acceptLanguage is not null)
                    o.Headers["Accept-Language"] = acceptLanguage;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.WebSocketFactory = async (context, ct) =>
                {
                    var wsClient = server.CreateWebSocketClient();
                    wsClient.ConfigureRequest = request =>
                    {
                        if (acceptLanguage is not null)
                            request.Headers["Accept-Language"] = acceptLanguage;
                    };
                    return await wsClient.ConnectAsync(context.Uri, ct);
                };
            })
            .Build();

        // Blazor reports a rejected hub call by pushing "JS.Error" and aborting the connection, which
        // otherwise surfaces to the caller as a bare TaskCanceledException. Surface the real reason.
        var hubError = string.Empty;
        connection.On<string>("JS.Error", message => hubError = message);
        connection.Closed += ex =>
        {
            if (ex is not null)
                hubError = string.IsNullOrEmpty(hubError) ? ex.ToString() : $"{hubError}; {ex}";
            return Task.CompletedTask;
        };

        await using (connection)
        {
            await connection.StartAsync(TestContext.Current.CancellationToken);
            try
            {
                await connection.InvokeAsync<string?>(
                    "StartCircuit", "http://localhost/", "http://localhost/", "[]", "",
                    TestContext.Current.CancellationToken);
                await connection.InvokeAsync(
                    "UpdateRootComponents", EmptyRootComponentBatch, "",
                    TestContext.Current.CancellationToken);
                return await sink.FirstAsync(TestContext.Current.CancellationToken);
            }
            catch (Exception ex) when (!string.IsNullOrEmpty(hubError))
            {
                throw new InvalidOperationException(
                    $"Blazor refused to open the circuit: {hubError}", ex);
            }
        }
    }

    /// <summary>
    /// 🚨 THE PAYWALL CASE, and the reason this change exists. An anonymous visitor whose browser
    /// asks for British English gets an ENGLISH identity — on a portal whose content may well be
    /// German. Before the fix <c>Locale</c> was null here and every such visitor was served the
    /// default language no matter what they asked for.
    /// </summary>
    [Theory(Timeout = 60000)]
    [InlineData(HttpTransportType.WebSockets)]
    [InlineData(HttpTransportType.LongPolling)]
    public async Task AnonymousCircuit_WithEnglishBrowser_ResolvesEnglish(HttpTransportType transport)
    {
        var identity = await OpenCircuitAsync("en-GB", transport);

        identity.Should().NotBeNull("an open circuit always resolves an identity, anonymous at minimum");
        identity!.ObjectId.Should().Be(WellKnownUsers.Anonymous);
        identity.Locale.Should().Be("en",
            "en-GB folds onto the primary subtag, exactly as a signed-in profile's tag would");
    }

    /// <summary>
    /// The mirror, which is what stops "always English" from passing: the same anonymous circuit
    /// with a German browser must resolve GERMAN. A fix that hard-coded the default would satisfy
    /// the test above and fail here.
    /// </summary>
    [Theory(Timeout = 60000)]
    [InlineData(HttpTransportType.WebSockets)]
    [InlineData(HttpTransportType.LongPolling)]
    public async Task AnonymousCircuit_WithGermanBrowser_ResolvesGerman(HttpTransportType transport)
    {
        var identity = await OpenCircuitAsync("de-CH,de;q=0.9,en;q=0.8", transport);

        identity!.Locale.Should().Be("de",
            "the viewer's language wins for chrome — a Swiss-German browser gets German");
    }

    /// <summary>
    /// A language this deployment does not ship must degrade CLEANLY — no throw, no half-set
    /// identity — and stay <see langword="null"/> rather than being pinned to a guess, so a later
    /// and better answer (a profile that loads after sign-in) is not shadowed. Rendering then falls
    /// back to English through <c>Locales.Resolve</c>, which is the correct outcome.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnonymousCircuit_WithAnUnsupportedLanguage_FallsBackCleanly()
    {
        var identity = await OpenCircuitAsync("fr-FR,fr;q=0.9");

        identity.Should().NotBeNull("an unsupported language must not break circuit identity resolution");
        identity!.Locale.Should().BeNull(
            "unsupported must stay distinguishable from an explicit request for English");
    }

    /// <summary>A client that sends no header at all is the no-preference case — unchanged behaviour.</summary>
    [Fact(Timeout = 60000)]
    public async Task AnonymousCircuit_WithNoHeader_ResolvesNoLanguage()
    {
        var identity = await OpenCircuitAsync(acceptLanguage: null);

        identity.Should().NotBeNull();
        identity!.Locale.Should().BeNull();
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  PRECEDENCE — a SEED, never an override. Pure, because it is a pure rule, and because both
    //  entry paths (the circuit and the SSR request) reach it through the same function.
    // ════════════════════════════════════════════════════════════════════════════════════════

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// 🚨 The constraint that keeps the seed honest. A signed-in user who chose English in their
    /// profile must KEEP English on a German browser. Seeding is for the case where we know nothing;
    /// the moment the profile states a language, the header stops mattering.
    /// </summary>
    [Fact]
    public void AStoredPreference_BeatsTheBrowsersHeader()
    {
        // What the entry paths build: claims identity + the request's negotiated language.
        var seed = new AccessContext { ObjectId = "rbuergi", Locale = "de" };
        var profile = new MeshWeaver.Mesh.MeshNode("rbuergi")
        {
            Name = "Roland",
            Content = new User { Email = "rbuergi@systemorph.com", Locale = "en" }
        };

        MeshUserProjection.Apply(seed, profile, JsonOptions).Locale.Should().Be("en");
    }

    /// <summary>
    /// The other half: a signed-in user who has never stated a preference finally gets their
    /// browser's language instead of unconditional English. An unset profile field means "no
    /// preference", which must fall back to the seed rather than erase it.
    /// </summary>
    [Fact]
    public void NoStoredPreference_FallsBackToTheBrowsersHeader()
    {
        var seed = new AccessContext { ObjectId = "rbuergi", Locale = "de" };
        var profile = new MeshWeaver.Mesh.MeshNode("rbuergi")
        {
            Name = "Roland",
            Content = new User { Email = "rbuergi@systemorph.com" }
        };

        MeshUserProjection.Apply(seed, profile, JsonOptions).Locale.Should().Be("de");
    }
}
