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

        // PHASE ONE while the manifest carries no identity, PHASE TWO once it does. The manifest
        // is the state between them — a half-answered one keeping the instance in setup is exactly
        // what InstanceSetupState.AwaitingModules is for, so no session, cookie or in-memory step
        // counter is needed (and none would survive the restart-shaped lifecycle anyway).
        app.MapGet(Path, (HttpContext ctx) =>
        {
            var strings = StringsFor(ctx);
            var identity = IdentityOf(ctx);
            if (identity is not { IsRegistered: true })
                return Html(SetupPage.RenderIdentity(
                    strings, MintInstanceId(), DefaultRegistry(ctx),
                    token: ctx.Request.Query["token"]), StatusCodes.Status200OK);

            return Html(SetupPage.Render(
                CatalogFor(ctx, identity), strings, token: ctx.Request.Query["token"]),
                StatusCodes.Status200OK);
        });

        app.MapPost(Path + "/identity", (HttpContext ctx, IFormCollection form) =>
            RegisterIdentity(ctx, form, token)).DisableAntiforgery();

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
    /// PHASE ONE — claim the id, register with the registry, and record the result so phase two can
    /// list what this instance is entitled to.
    ///
    /// <para>🚨 <b>The issued key is persisted BEFORE anything else can fail.</b> The registry
    /// returns it exactly once and never re-issues it, and the id cannot be re-registered after
    /// deletion — so a crash between "registered" and "wrote it down" permanently burns that id.
    /// The manifest write is therefore the very next thing after a successful response, ahead of
    /// listing packages or rendering anything.</para>
    /// </summary>
    private static async Task<IResult> RegisterIdentity(
        HttpContext ctx, IFormCollection form, SetupAccessToken token)
    {
        var strings = StringsFor(ctx);
        var answers = new IdentityAnswers(
            Blank(form["identity.name"]), Blank(form["identity.id"]),
            Blank(form["identity.registry"]), Blank(form["identity.key"]));

        if (!token.Matches(form["token"]))
            return Html(SetupPage.RenderIdentity(
                strings, answers.Id ?? MintInstanceId(), DefaultRegistry(ctx), answers,
                [strings.TokenInvalid]), StatusCodes.Status403Forbidden);

        var problems = ImmutableList.CreateBuilder<string>();
        if (string.IsNullOrWhiteSpace(answers.Name))
            problems.Add(strings.ProblemNoInstanceName);
        // The id is minted by us and rendered read-only, so a malformed one means the form was
        // hand-crafted. Refuse rather than claim something the registry's alphabet rejects.
        var id = string.IsNullOrWhiteSpace(answers.Id) ? MintInstanceId() : answers.Id.Trim();
        if (!InstanceIdRules.IsWellFormed(id))
            problems.Add(strings.ProblemBadInstanceId(id));
        var registry = string.IsNullOrWhiteSpace(answers.RegistryUrl)
            ? DefaultRegistry(ctx) : answers.RegistryUrl.Trim();
        if (!Uri.TryCreate(registry, UriKind.Absolute, out _))
            problems.Add(strings.ProblemBadRegistry(registry));

        if (problems.Count > 0)
            return Html(SetupPage.RenderIdentity(
                strings, id, DefaultRegistry(ctx), answers, problems.ToImmutable(), form["token"]),
                StatusCodes.Status400BadRequest);

        var configuration = ctx.RequestServices.GetRequiredService<IConfiguration>();
        var root = ModuleRoot.Resolve(configuration);
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(SetupEndpoints));

        InstanceRegistrationPayloads.Response registration;
        try
        {
            var client = ctx.RequestServices.GetRequiredService<SetupRegistryClient>();
            registration = await client.RegisterAsync(
                registry, id, answers.Name!.Trim(), answers.BootstrapKey,
                homeUrl: $"{ctx.Request.Scheme}://{ctx.Request.Host}", ctx.RequestAborted);
        }
        catch (SetupRegistryException ex)
        {
            logger.LogWarning("[InstanceSetup] registration refused: {Reason}", ex.Message);
            return Html(SetupPage.RenderIdentity(
                strings, id, DefaultRegistry(ctx), answers, [ex.Message], form["token"]),
                StatusCodes.Status400BadRequest);
        }

        // Encrypt before persisting, and provision a master key if this install has none — the same
        // rule the rest of the wizard obeys: a secret that cannot be encrypted is refused, never
        // written in the clear.
        string? protectedKey = null;
        try
        {
            var masterKey = InstanceMasterKey.EnsureCreated(
                root, configuration[ConfigMasterKeyProvider.ConfigKey]);
            protectedKey = new ProviderKeyProtector(new LiteralMasterKeyProvider(masterKey))
                .Protect(registration.InstanceKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 🚨 The id is ALREADY CLAIMED at this point. Say so loudly: the operator must not
            // simply retry with a fresh id and leak another one, they must make the root writable.
            logger.LogError(ex,
                "[InstanceSetup] registered as {Id} but could NOT protect the issued key under {Root}",
                id, root);
            return Html(SetupPage.RenderIdentity(
                strings, id, DefaultRegistry(ctx), answers,
                [strings.ProblemKeyUnstorable(id)], form["token"]),
                StatusCodes.Status500InternalServerError);
        }

        var manifest = (InstanceManifest.Read(root) ?? InstanceSetupDefaults.Manifest()) with
        {
            State = InstanceSetupState.AwaitingModules,
            Identity = new InstanceIdentitySelection
            {
                Id = id,
                Name = answers.Name!.Trim(),
                RegistryUrl = registry,
                InstanceKey = protectedKey,
                Plan = registration.Plan,
            },
        };
        manifest.Write(root);
        logger.LogInformation(
            "[InstanceSetup] registered as {Id} ({Name}) at {Registry} on plan {Plan}",
            id, manifest.Identity!.Name, registry, registration.Plan ?? "(unstated)");

        return Results.Redirect($"{Path}?token={Uri.EscapeDataString(form["token"].ToString())}");
    }

    /// <summary>The identity this instance has already registered, or null.</summary>
    private static InstanceIdentitySelection? IdentityOf(HttpContext ctx)
    {
        var configuration = ctx.RequestServices.GetRequiredService<IConfiguration>();
        return InstanceManifest.Read(ModuleRoot.Resolve(configuration))?.Identity;
    }

    /// <summary>
    /// A fresh instance id. A guid, because the id is claimed GLOBALLY and never re-issued — it
    /// must not be something a person picks and might re-pick, and it must not collide with another
    /// installation set up the same afternoon. Lowercase, which the registry's alphabet requires.
    /// </summary>
    private static string MintInstanceId() => Guid.NewGuid().ToString("d").ToLowerInvariant();

    /// <summary>The registry to pre-fill, from configuration when the deployment states one.</summary>
    private static string DefaultRegistry(HttpContext ctx) =>
        Blank(ctx.RequestServices.GetRequiredService<IConfiguration>()["PluginCatalog:RegistryUrl"])
        ?? "https://memex.meshweaver.cloud";

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
        var identity = IdentityOf(ctx);
        // Phase two cannot be submitted before phase one: the options it offers are derived from
        // what the registration entitles this instance to.
        if (identity is not { IsRegistered: true })
            return Results.Redirect(Path);
        var catalog = CatalogFor(ctx, identity);

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

        // The identity was written in phase one and must survive completion — losing it would
        // strand the issued key, which the registry never re-issues.
        var completed = plan.Manifest with { Identity = identity };
        completed.Write(root);
        logger.LogInformation(
            "[InstanceSetup] manifest written to {Path}: storage {Storage}, {SignIn} sign-in route(s), "
            + "{Ai} model provider(s), embeddings {Embeddings}. Restarting.",
            InstanceManifest.PathFor(root),
            completed.Storage?.Type,
            completed.SignIn?.Providers.Count ?? 0,
            completed.Ai?.Providers.Count ?? 0,
            completed.Ai?.Embeddings is { IsConfigured: true } ? "configured" : "none");

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
                .. form["packages"].Select(v => v?.Trim()).OfType<string>().Where(v => v.Length > 0),
            ],
        };

    private static SetupCatalog Catalog(HttpContext ctx) =>
        ctx.RequestServices.GetService<ISetupCatalogProvider>()?.Describe() ?? SetupCatalog.Empty;

    /// <summary>
    /// Phase two's catalog: what the IMAGE offers, plus what the REGISTRY says this instance is
    /// entitled to.
    ///
    /// <para>🚨 A package that declares a <c>StorageType</c> becomes a database option ONLY when the
    /// image can already open that backend. Landing a storage module the image lacks would have to
    /// happen before persistence selection reads <c>Graph:Storage</c>, and package provisioning runs
    /// after the mesh is up — so offering one would record a backend that never resolves and fail at
    /// the NEXT boot, with the wizard gone. The package still appears in the plugin list; it just
    /// does not pretend to answer the database question.</para>
    ///
    /// <para>A registry that cannot be reached is REPORTED, not swallowed. An instance that
    /// registered but cannot see its plan is a state worth naming — an empty list would read as
    /// "your plan includes nothing".</para>
    /// </summary>
    private static SetupCatalog CatalogFor(HttpContext ctx, InstanceIdentitySelection identity)
    {
        var catalog = Catalog(ctx) with { Identity = identity };
        var configuration = ctx.RequestServices.GetRequiredService<IConfiguration>();
        var root = ModuleRoot.Resolve(configuration);

        var masterKey = InstanceMasterKey.Resolve(root, configuration[ConfigMasterKeyProvider.ConfigKey]);
        var key = masterKey is null || identity.InstanceKey is null
            ? null
            : new ProviderKeyProtector(new LiteralMasterKeyProvider(masterKey)).Unprotect(identity.InstanceKey);

        ImmutableList<PackageManifest> packages;
        try
        {
            packages = ctx.RequestServices.GetRequiredService<SetupRegistryClient>()
                .ListPackagesAsync(identity.RegistryUrl, key, ctx.RequestAborted)
                .GetAwaiter().GetResult();
        }
        catch (SetupRegistryException ex)
        {
            return catalog with { RegistryProblem = ex.Message };
        }

        var offered = packages
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .Select(p => new SetupPackageOption(
                p.Id, string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name!, p.Description,
                p.StorageType, PreSelected: false))
            .ToImmutableList();

        // Only backends the image can actually open — see the remarks.
        var openable = ctx.RequestServices.GetService<StorageBackendCatalog>() ?? StorageBackendCatalog.Empty;
        var fromPackages = offered
            .Where(p => p.StorageType is { } t && openable.Offers(t))
            .Where(p => !catalog.Storage.Any(o => string.Equals(o.Type, p.StorageType, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new SetupStorageOption(
                p.StorageType!, p.Name, NeedsConnectionString: true) { PackageId = p.Id })
            .ToImmutableList();

        return catalog with
        {
            Packages = offered,
            Storage = catalog.Storage.AddRange(fromPackages),
        };
    }

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
