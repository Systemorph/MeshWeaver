using System.Collections.Immutable;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Setup;

/// <summary>
/// Serves the first-run wizard — the production reader of <c>MeshBuilder.IsAwaitingSetup</c> that
/// its own doc comment has always demanded: <i>"a host that reads this true must serve the SETUP
/// surface and nothing else"</i>.
///
/// <para><b>And nothing else, literally.</b> While the instance is awaiting setup every request
/// that is not the wizard or a probe is redirected to it. That is not tidiness: with no storage
/// there are no hubs, so a page render, a sign-in callback or an API call reaches code whose
/// dependencies do not exist, and the failures are unhelpful in proportion to how far they get.
/// One surface, one answer.</para>
/// </summary>
public static class SetupEndpoints
{
    /// <summary>The wizard's path.</summary>
    public const string Path = "/setup";

    /// <summary>
    /// Paths that must keep working while the instance is in setup: the liveness probe, so an
    /// orchestrator does not kill the very pod an operator is configuring.
    ///
    /// <para>🚨 <c>/healthz</c> answering <c>200</c> here is correct and deliberate. An instance
    /// awaiting setup is not FAILING — it is waiting for a person, and a probe that reported it
    /// unhealthy would restart it in a loop, minting a new setup token every time and making the
    /// wizard unreachable in practice.</para>
    /// </summary>
    private static readonly ImmutableHashSet<string> AlwaysAllowed =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "/healthz", "/alive", "/health");

    /// <summary>
    /// Maps the wizard and, while <see cref="InstanceSetupStatusAccessor.IsAwaitingSetup"/>, the
    /// short-circuit that sends everything else to it.
    ///
    /// <para>Call EARLY in the pipeline — before authentication, before the Blazor router. A
    /// configured instance pays one delegate that answers false and forwards.</para>
    /// </summary>
    /// <param name="app">The application to map onto.</param>
    /// <returns>
    /// True when this instance is awaiting setup and the wizard has taken the pipeline over — in
    /// which case the caller must run the app IMMEDIATELY and map nothing else.
    ///
    /// <para>🚨 <b>A bool, because "and nothing else" is the caller's job to honour and it cannot
    /// be enforced from here.</b> Mapping the rest of a portal on an instance with no storage is
    /// not merely wasteful, it FAILS: <c>MapMeshWeaver</c> asserts that a permission evaluator is
    /// registered, and on a setup-mode host <c>ConfigureMemexMesh</c> returned before
    /// <c>AddRowLevelSecurity</c> ever ran. The host then dies at startup with a security assertion
    /// instead of serving the wizard that would fix it — measured, not theorised.</para>
    /// </returns>
    public static bool MapInstanceSetup(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var awaiting = app.Services.GetService<InstanceSetupStatusAccessor>()?.IsAwaitingSetup ?? false;
        if (!awaiting)
            return false;

        var token = app.Services.GetRequiredService<SetupAccessToken>();
        // 🚨 Console.WriteLine, NOT a logger, and that is the whole point. The token is the ONLY
        // way into this surface, and it was written with LogInformation under the category
        // `Memex.Portal.Shared.Setup.SetupEndpoints` — which every deployment filters to Warning.
        // So on a real cluster the banner never appeared: the wizard was serving, the token existed,
        // and nobody could learn it. A credential whose delivery a log level can suppress is not
        // delivered. stdout is what `kubectl logs` and `docker logs` show regardless of
        // configuration, and it is already how the hand-over banner reaches the operator.
        Console.WriteLine(token.ConsoleBanner(app.Urls.FirstOrDefault()));

        app.Use((ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "/";
            if (path.StartsWith(Path, StringComparison.OrdinalIgnoreCase) || AlwaysAllowed.Contains(path))
                return next();
            ctx.Response.Redirect(Path);
            return Task.CompletedTask;
        });

        app.MapGet(Path, (HttpContext ctx) =>
        {
            var strings = StringsFor(ctx);
            return Results.Content(
                SetupPage.Render(Catalog(ctx), strings, token: ctx.Request.Query["token"]),
                "text/html; charset=utf-8");
        });

        // 🚨 DisableAntiforgery is deliberate, and the setup token is what replaces it. Binding
        // IFormCollection makes ASP.NET demand the antiforgery middleware; adding a SECOND
        // unguessable form value would be the synchronizer-token pattern implemented twice, since
        // `token` already IS one — it is required, per-process, high-entropy, and readable only
        // from this instance's own console. A cross-site POST cannot carry it, which is precisely
        // the property antiforgery exists to provide. The alternative — wiring UseAntiforgery into
        // a host that has no storage, no session and no data protection keys yet — adds moving
        // parts to the one surface that has to work when nothing else does.
        app.MapPost(Path, (HttpContext ctx, IFormCollection form) => Apply(ctx, form, token))
            .DisableAntiforgery();

        return true;
    }

    /// <summary>
    /// Validates the submission, writes the manifest, and stops the process so it restarts
    /// configured.
    ///
    /// <para>🚨 <b>The restart is the delivery mechanism, not an afterthought.</b> Storage adapters,
    /// authentication schemes and module assemblies are all bound once while the host is BUILT;
    /// nothing in this platform re-registers an authentication scheme or opens a second storage
    /// backend after <c>builder.Build()</c>. So the honest artifact of this form is a written
    /// manifest plus a restart, and saying so is what <c>SignInSetupPlan</c> already refuses to lie
    /// about.</para>
    /// </summary>
    private static IResult Apply(HttpContext ctx, IFormCollection form, SetupAccessToken token)
    {
        var strings = StringsFor(ctx);
        var catalog = Catalog(ctx);

        if (!token.Matches(form["token"]))
            // Deliberately NOT re-rendering the submitted answers: a wrong token means this is
            // plausibly not the operator, and echoing back what was typed helps nobody legitimate.
            return Html(SetupPage.Render(catalog, strings, problems: [strings.TokenInvalid]), StatusCodes.Status403Forbidden);

        var answers = ReadAnswers(form, catalog);
        var configuration = ctx.RequestServices.GetRequiredService<IConfiguration>();
        var root = ModuleRoot.Resolve(configuration);
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(SetupEndpoints));

        // Provision the master key BEFORE composing, because composing encrypts. A deployment that
        // supplied its own key keeps it — EnsureCreated never overwrites.
        IProviderKeyProtector? protector = null;
        try
        {
            var masterKey = InstanceMasterKey.EnsureCreated(
                root, configuration[ConfigMasterKeyProvider.ConfigKey]);
            protector = new ProviderKeyProtector(new LiteralMasterKeyProvider(masterKey));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Composition turns a null protector into a refusal that names the setting and the
            // writable-directory requirement — the operator's two ways out.
            logger.LogError(ex, "Could not provision a key-protection master key under {Root}", root);
        }

        var plan = SetupComposition.Compose(
            answers, catalog, ctx.RequestServices.GetService<StorageBackendCatalog>() ?? StorageBackendCatalog.Empty,
            protector, strings, DateTimeOffset.UtcNow);

        if (plan.Manifest is null)
            return Html(
                SetupPage.Render(catalog, strings, answers, plan.Problems, plan.Warnings, form["token"]),
                StatusCodes.Status400BadRequest);

        plan.Manifest.Write(root);
        logger.LogInformation(
            "[InstanceSetup] manifest written to {Path}: storage {Storage}, {SignIn} sign-in route(s), "
            + "{Ai} model provider(s), embeddings {Embeddings}. Restarting.",
            InstanceManifest.PathFor(root),
            plan.Manifest.Storage?.Type,
            plan.Manifest.SignIn?.Providers.Count ?? 0,
            plan.Manifest.Ai?.Providers.Count ?? 0,
            plan.Manifest.Ai?.Embeddings is { IsConfigured: true } ? "configured" : "none");

        // Stop only AFTER the response is on the wire, or the operator's browser reports a
        // connection reset and they cannot tell a successful install from a crash.
        var lifetime = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        ctx.Response.OnCompleted(() =>
        {
            lifetime.StopApplication();
            return Task.CompletedTask;
        });
        return Html(SetupPage.RenderDone(strings), StatusCodes.Status200OK);
    }

    /// <summary>
    /// Reads the form into answers. Only fields the catalog OFFERS are read, so a hand-crafted POST
    /// cannot introduce a provider or module the image never advertised — the composition refuses
    /// those too, and refusing twice is the correct amount for a surface that writes credentials.
    /// </summary>
    private static SetupAnswers ReadAnswers(IFormCollection form, SetupCatalog catalog) =>
        new()
        {
            StorageType = form["storage.type"].ToString(),
            ConnectionString = Blank(form["storage.connectionString"]),
            BasePath = Blank(form["storage.basePath"]),
            EnableDevLogin = form.ContainsKey("signin.dev"),
            DevAdminUsers = Blank(form["signin.devAdmins"]),
            SignIn =
            [
                .. catalog.SignIn.Where(o => !o.IsSwitch).Select(o => new SetupSignInAnswer(
                    o.Name,
                    Blank(form[$"signin.{o.Name}.clientId"]),
                    Blank(form[$"signin.{o.Name}.tenantId"]),
                    Blank(form[$"signin.{o.Name}.clientSecret"]))),
            ],
            Ai =
            [
                .. catalog.Ai.Select(o => new SetupAiAnswer(
                    o.Name,
                    Blank(form[$"ai.{o.Name}.apiKey"]),
                    Blank(form[$"ai.{o.Name}.endpoint"]))),
            ],
            EmbeddingEndpoint = Blank(form["embedding.endpoint"]),
            EmbeddingModel = Blank(form["embedding.model"]),
            BootModules = [.. form["modules"].Select(v => v?.Trim()).OfType<string>().Where(v => v.Length > 0)],
            ProvisionPackages =
            [
                .. (form["packages"].ToString() ?? "")
                    .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ],
        };

    private static SetupCatalog Catalog(HttpContext ctx) =>
        ctx.RequestServices.GetService<ISetupCatalogProvider>()?.Describe() ?? SetupCatalog.Empty;

    /// <summary>
    /// The viewer's locale, read EXPLICITLY from <c>Accept-Language</c>.
    ///
    /// <para>🚨 Not <c>CultureInfo.CurrentUICulture</c>, which is banned platform-wide: an ambient
    /// culture on a shared thread pool is whatever the last request left there. There is no
    /// <c>AccessContext</c> yet — that needs a mesh — so the header is the honest source, and it is
    /// passed down as an argument rather than read again anywhere else.</para>
    /// </summary>
    private static SetupStrings StringsFor(HttpContext ctx)
    {
        var header = ctx.Request.Headers.AcceptLanguage.ToString();
        // "de-CH,de;q=0.9,en;q=0.8" → "de-CH" → Locales.Resolve narrows it to a supported language.
        var first = header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split(';')[0].Trim())
            .FirstOrDefault(part => part.Length > 0 && part != "*");
        return new SetupStrings(first);
    }

    private static IResult Html(string html, int status) =>
        Results.Content(html, "text/html; charset=utf-8", statusCode: status);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
