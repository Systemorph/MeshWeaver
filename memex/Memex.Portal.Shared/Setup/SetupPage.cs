using System.Collections.Immutable;
using System.Net;
using System.Text;

namespace Memex.Portal.Shared.Setup;

/// <summary>
/// The first-run wizard's page, rendered as self-contained HTML.
///
/// <para>🚨 <b>Hand-written markup here is not a breach of "never hand-roll UI" — it is the one
/// place the framework's UI cannot run.</b> Every <c>UiControl</c>, layout area and data binding in
/// this platform resolves through a mesh: hubs, streams, a workspace, a storage adapter. This page
/// is served when there is no storage, therefore no mesh, therefore none of that. The rule exists
/// so that a developer with the framework available uses it; a surface whose whole purpose is to
/// answer "which database?" has it available by definition nowhere. The same reasoning already
/// produced <c>ErrorRoutes</c>' plain error page.</para>
///
/// <para><b>No external asset, no script, no font.</b> The instance may be air-gapped, the CDN
/// unreachable and the static pipeline unconfigured — and this is the page that has to work when
/// nothing else does. One document, inline CSS, no JavaScript required for the form to submit.</para>
///
/// <para>🚨 <b>Every interpolated value is escaped</b> through <see cref="Escape"/>. The inputs
/// include a connection string and provider names, and the page re-renders them after a failed
/// submit — an unescaped round trip there is a reflected-XSS hole on the one surface that collects
/// the instance's credentials.</para>
/// </summary>
public static class SetupPage
{
    /// <summary>Renders the wizard.</summary>
    /// <param name="catalog">What the image offers.</param>
    /// <param name="strings">The viewer's locale.</param>
    /// <param name="submitted">The answers to re-fill after a failed submit, or null on first load.</param>
    /// <param name="problems">Blocking refusals, shown at the top.</param>
    /// <param name="warnings">Non-blocking notes, shown at the top.</param>
    /// <param name="token">The token to pre-fill, when it arrived in the query string.</param>
    public static string Render(
        SetupCatalog catalog,
        SetupStrings strings,
        SetupAnswers? submitted = null,
        ImmutableList<string>? problems = null,
        ImmutableList<string>? warnings = null,
        string? token = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(strings);

        var html = new StringBuilder(16 * 1024);
        Open(html, strings, strings.Title);

        html.Append($"<h1>{Escape(strings.Title)}</h1>");
        html.Append($"<p class=lead>{Escape(strings.Intro)}</p>");

        AppendIdentity(html, catalog, strings);
        AppendMessages(html, "problems", strings.ProblemsHeading, problems);
        AppendMessages(html, "warnings", strings.WarningsHeading, warnings);
        if (catalog.RegistryProblem is { } registryProblem)
            AppendMessages(html, "warnings", strings.WarningsHeading, [registryProblem]);

        html.Append("<form method=post action=\"/setup\" autocomplete=off>");

        // ── Token ───────────────────────────────────────────────────────────────────────────
        html.Append("<section><div class=field>");
        html.Append($"<label for=token>{Escape(strings.TokenLabel)}</label>");
        html.Append($"<input id=token name=token type=text required value=\"{Escape(token)}\" spellcheck=false>");
        html.Append($"<p class=help>{Escape(strings.TokenHelp)}</p>");
        html.Append("</div></section>");

        AppendStorage(html, catalog, strings, submitted);
        AppendPackages(html, catalog, strings, submitted);
        AppendSignIn(html, catalog, strings, submitted);
        AppendAi(html, catalog, strings, submitted);
        AppendModules(html, catalog, strings, submitted);

        html.Append($"<button type=submit>{Escape(strings.Submit)}</button>");
        html.Append("</form>");
        Close(html);
        return html.ToString();
    }

    /// <summary>
    /// PHASE ONE — the only question that must be answered before any other can be offered.
    ///
    /// <para>The operator types a NAME. The id is a guid the instance mints for itself, shown but
    /// not editable: it is claimed globally on the registry and can never be re-registered, so it
    /// must not be something a person picks and might re-pick.</para>
    /// </summary>
    /// <param name="strings">The viewer's locale.</param>
    /// <param name="instanceId">The guid this instance minted for itself.</param>
    /// <param name="defaultRegistry">The registry to pre-fill.</param>
    /// <param name="submitted">Values to re-fill after a failed submit.</param>
    /// <param name="problems">Blocking refusals.</param>
    /// <param name="token">The setup token, pre-filled from the query string.</param>
    public static string RenderIdentity(
        SetupStrings strings,
        string instanceId,
        string defaultRegistry,
        IdentityAnswers? submitted = null,
        ImmutableList<string>? problems = null,
        string? token = null)
    {
        ArgumentNullException.ThrowIfNull(strings);

        var html = new StringBuilder(8 * 1024);
        Open(html, strings, strings.HelloTitle);
        html.Append($"<h1>{Escape(strings.HelloTitle)}</h1>");
        html.Append($"<p class=lead>{Escape(strings.HelloIntro)}</p>");
        AppendMessages(html, "problems", strings.ProblemsHeading, problems);

        html.Append("<form method=post action=\"/setup/identity\" autocomplete=off>");
        html.Append("<section><div class=field>");
        html.Append($"<label for=token>{Escape(strings.TokenLabel)}</label>");
        html.Append($"<input id=token name=token type=text required value=\"{Escape(token)}\" spellcheck=false>");
        html.Append($"<p class=help>{Escape(strings.TokenHelp)}</p>");
        html.Append("</div></section>");

        html.Append($"<section><h2>{Escape(strings.IdentityHeading)}</h2>");
        html.Append($"<p class=help>{Escape(strings.IdentityHelp)}</p>");
        html.Append("<div class=field>");
        html.Append($"<label for=name>{Escape(strings.InstanceNameLabel)}</label>");
        html.Append(
            $"<input id=name name=\"identity.name\" type=text required autofocus "
            + $"value=\"{Escape(submitted?.Name)}\" placeholder=\"{Escape(strings.InstanceNamePlaceholder)}\">");
        html.Append("</div>");
        // The id is shown, not asked. Rendered read-only so the operator can see and copy what is
        // about to be claimed on their behalf — an id that appears from nowhere after the fact is
        // worse than one they watched being minted.
        html.Append("<div class=field>");
        html.Append($"<label for=iid>{Escape(strings.InstanceIdLabel)}</label>");
        html.Append(
            $"<input id=iid name=\"identity.id\" type=text readonly value=\"{Escape(instanceId)}\" spellcheck=false>");
        html.Append($"<p class=help>{Escape(strings.InstanceIdHelp)}</p>");
        html.Append("</div>");
        html.Append("<div class=field>");
        html.Append($"<label for=reg>{Escape(strings.RegistryLabel)}</label>");
        html.Append(
            $"<input id=reg name=\"identity.registry\" type=text value=\"{Escape(submitted?.RegistryUrl ?? defaultRegistry)}\" spellcheck=false>");
        html.Append($"<p class=help>{Escape(strings.RegistryHelp)}</p>");
        html.Append("</div>");
        html.Append("<div class=field>");
        html.Append($"<label for=bkey>{Escape(strings.BootstrapKeyLabel)}</label>");
        html.Append($"<input id=bkey name=\"identity.key\" type=password value=\"\" spellcheck=false>");
        html.Append($"<p class=help>{Escape(strings.BootstrapKeyHelp)}</p>");
        html.Append("</div></section>");

        html.Append($"<button type=submit>{Escape(strings.RegisterAndContinue)}</button>");
        html.Append("</form>");
        Close(html);
        return html.ToString();
    }

    /// <summary>The page shown once the manifest is written and the process is stopping.</summary>
    /// <param name="strings">The viewer's locale.</param>
    public static string RenderDone(SetupStrings strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        var html = new StringBuilder(2048);
        Open(html, strings, strings.Done);
        html.Append($"<h1>{Escape(strings.Done)}</h1>");
        html.Append($"<p class=lead>{Escape(strings.DoneDetail)}</p>");
        Close(html);
        return html.ToString();
    }

    /// <summary>The registered identity, stated at the top of phase two so the operator can see
    /// what this instance IS before answering anything else.</summary>
    private static void AppendIdentity(StringBuilder html, SetupCatalog catalog, SetupStrings strings)
    {
        if (catalog.Identity is not { } identity)
            return;
        html.Append("<div class=\"notice identity\">");
        html.Append($"<strong>{Escape(identity.Name)}</strong>");
        html.Append($"<div class=badge>{Escape(strings.RegisteredAs(identity.Id, identity.RegistryUrl))}</div>");
        if (!string.IsNullOrWhiteSpace(identity.Plan))
            html.Append($"<div class=badge>{Escape(strings.PlanIs(identity.Plan!))}</div>");
        html.Append("</div>");
    }

    /// <summary>
    /// What this instance's plan entitles it to. Rendered even when EMPTY, with a note saying so —
    /// an empty catalog is a legitimate answer, and silently showing nothing would read as a bug.
    /// </summary>
    private static void AppendPackages(
        StringBuilder html, SetupCatalog catalog, SetupStrings strings, SetupAnswers? submitted)
    {
        if (catalog.Identity is null)
            return;
        html.Append($"<section><h2>{Escape(strings.PackagesHeading)}</h2>");
        html.Append($"<p class=help>{Escape(strings.PackagesHelp)}</p>");
        if (catalog.Packages.IsEmpty)
        {
            html.Append($"<p class=help><em>{Escape(strings.NoPackages)}</em></p></section>");
            return;
        }
        foreach (var package in catalog.Packages)
        {
            var on = submitted is null
                ? package.PreSelected
                : submitted.ProvisionPackages.Any(id => OrdinalEquals(id, package.Id));
            html.Append("<label class=choice>");
            html.Append(
                $"<input type=checkbox name=packages value=\"{Escape(package.Id)}\"{(on ? " checked" : "")}>");
            html.Append($"<span>{Escape(package.Name)}");
            if (package.StorageType is { } storage)
                html.Append($" <span class=badge>{Escape(strings.ProvidesDatabase(storage))}</span>");
            if (package.Description is { } description)
                html.Append($" <em>{Escape(description)}</em>");
            html.Append("</span></label>");
        }
        html.Append("</section>");
    }

    private static void AppendStorage(
        StringBuilder html, SetupCatalog catalog, SetupStrings strings, SetupAnswers? submitted)
    {
        html.Append($"<section><h2>{Escape(strings.StorageHeading)}</h2>");
        html.Append($"<p class=help>{Escape(strings.StorageHelp)}</p>");

        // The first option is the pre-selected one, and the contributor orders the list. Nothing is
        // pre-selected when an answer came back — re-rendering must show what the operator chose.
        var chosen = submitted?.StorageType;
        var first = true;
        foreach (var option in catalog.Storage)
        {
            var isChecked = chosen is null ? first : OrdinalEquals(chosen, option.Type);
            html.Append("<label class=choice>");
            html.Append(
                $"<input type=radio name=\"storage.type\" value=\"{Escape(option.Type)}\"{(isChecked ? " checked" : "")}>");
            html.Append($"<span>{Escape(option.DisplayName)}</span>");
            html.Append("</label>");
            first = false;
        }

        // Both fields are always present rather than revealed per backend: the page carries no
        // JavaScript by design, and a field that is only sometimes in the DOM is a field the form
        // sometimes cannot submit. The composition ignores the one the chosen backend does not use.
        var hint = catalog.Storage.Select(o => o.ConnectionStringHint).FirstOrDefault(h => h is not null);
        html.Append("<div class=field>");
        html.Append($"<label for=cs>{Escape(strings.ConnectionStringLabel)}</label>");
        // 🚨 NEVER echoed back, for exactly the reason a client secret is not: a connection string
        // CARRIES A PASSWORD, and re-rendering it after a failed submit puts that password in the
        // page source, the browser cache and any screenshot of the form. This was inconsistent —
        // the sign-in secrets were already withheld and this one was not (Copilot review). The
        // operator retypes it, which is the same price the other credentials already pay.
        html.Append(
            $"<input id=cs name=\"storage.connectionString\" type=password value=\"\" "
            + $"placeholder=\"{Escape(hint)}\" spellcheck=false>");
        html.Append("</div><div class=field>");
        html.Append($"<label for=bp>{Escape(strings.BasePathLabel)}</label>");
        html.Append(
            $"<input id=bp name=\"storage.basePath\" type=text value=\"{Escape(submitted?.BasePath)}\" spellcheck=false>");
        html.Append("</div></section>");
    }

    private static void AppendSignIn(
        StringBuilder html, SetupCatalog catalog, SetupStrings strings, SetupAnswers? submitted)
    {
        html.Append($"<section><h2>{Escape(strings.SignInHeading)}</h2>");
        html.Append($"<p class=help>{Escape(strings.SignInHelp)}</p>");

        var devOption = catalog.SignIn.FirstOrDefault(o => o.IsSwitch);
        if (devOption is not null)
        {
            var on = submitted?.EnableDevLogin ?? !devOption.AlreadyConfigured || devOption.AlreadyConfigured;
            html.Append("<label class=choice>");
            html.Append(
                $"<input type=checkbox name=\"signin.dev\" value=on{(on ? " checked" : "")}"
                + $"{Disabled(devOption.AlreadyConfigured)}>");
            html.Append($"<span>{Escape(strings.DevLoginLabel)}");
            if (devOption.AlreadyConfigured)
                html.Append($" <span class=badge>{Escape(strings.AlreadyConfigured)}</span>");
            html.Append("</span></label>");
            html.Append($"<p class=help>{Escape(strings.DevLoginHelp)}</p>");
            html.Append("<div class=field>");
            html.Append($"<label for=devadmins>{Escape(strings.DevAdminsLabel)}</label>");
            html.Append(
                $"<input id=devadmins name=\"signin.devAdmins\" type=text "
                + $"value=\"{Escape(submitted?.DevAdminUsers)}\"{Disabled(devOption.AlreadyConfigured)}>");
            html.Append("</div>");
        }

        foreach (var option in catalog.SignIn.Where(o => !o.IsSwitch))
        {
            var answer = submitted?.SignIn.FirstOrDefault(a => OrdinalEquals(a.Name, option.Name));
            html.Append("<fieldset><legend>");
            html.Append(Escape(option.DisplayName));
            if (option.AlreadyConfigured)
                html.Append($" <span class=badge>{Escape(strings.AlreadyConfigured)}</span>");
            html.Append("</legend>");
            // 🚨 A route this DEPLOYMENT already answers is REPORTED, never offered for editing —
            // the fields are disabled, so the browser submits nothing for them. Rendering them
            // editable was a surface promising an effect it cannot deliver: the manifest is layered
            // UNDER the host's own configuration, so anything typed here would be accepted, stored,
            // and then silently outranked at the next boot. That is the shape of MeshWeaver.Plugins#980
            // — a surface showing its own stored answer while the process runs on a different one
            // (Copilot review, which caught the doc and the markup disagreeing).
            var locked = Disabled(option.AlreadyConfigured);
            Field(html, $"signin.{option.Name}.clientId", strings.ClientIdLabel, answer?.ClientId,
                extraAttributes: locked);
            if (option.HasTenant)
                Field(html, $"signin.{option.Name}.tenantId", strings.TenantLabel, answer?.TenantId,
                    extraAttributes: locked);
            // 🚨 A submitted secret is NEVER echoed back into the form. Re-filling a password field
            // after a failed submit is convenient and is also how a credential ends up in a browser
            // cache, a screenshot and a page source. The operator retypes it.
            Field(html, $"signin.{option.Name}.clientSecret", strings.ClientSecretLabel, null,
                password: true, extraAttributes: locked);
            html.Append("</fieldset>");
        }
        html.Append("</section>");
    }

    private static void AppendAi(
        StringBuilder html, SetupCatalog catalog, SetupStrings strings, SetupAnswers? submitted)
    {
        html.Append($"<section><h2>{Escape(strings.AiHeading)}</h2>");
        html.Append($"<p class=help>{Escape(strings.AiHelp)}</p>");

        foreach (var option in catalog.Ai)
        {
            var answer = submitted?.Ai.FirstOrDefault(a => OrdinalEquals(a.Name, option.Name));
            html.Append($"<fieldset><legend>{Escape(option.DisplayName)}</legend>");
            if (option.RequiresApiKey)
                Field(html, $"ai.{option.Name}.apiKey", strings.ApiKeyLabel, null, password: true);
            if (option.TakesEndpoint)
                // 🚨 The default endpoint is a PLACEHOLDER, never a value. Rendered as a value it
                // is submitted whether or not the operator looked at the row, so a provider nobody
                // chose arrives configured — measured on the first end-to-end run, where an
                // untouched "local / OpenAI-compatible" row wrote itself into the manifest. The
                // composition's "no key and no endpoint means not chosen" rule can only hold if an
                // untouched field actually submits nothing.
                Field(html, $"ai.{option.Name}.endpoint", strings.EndpointLabel,
                    answer?.Endpoint, placeholder: option.DefaultEndpoint);
            html.Append("</fieldset>");
        }

        html.Append($"<h3>{Escape(strings.EmbeddingHeading)}</h3>");
        html.Append($"<p class=help>{Escape(strings.EmbeddingHelp)}</p>");
        Field(html, "embedding.endpoint", strings.EndpointLabel, submitted?.EmbeddingEndpoint);
        Field(html, "embedding.model", strings.EmbeddingModelLabel,
            submitted?.EmbeddingModel ?? MeshWeaver.Mesh.InstanceEmbeddingsSelection.DefaultModel);
        html.Append("</section>");
    }

    private static void AppendModules(
        StringBuilder html, SetupCatalog catalog, SetupStrings strings, SetupAnswers? submitted)
    {
        html.Append($"<section><h2>{Escape(strings.ModulesHeading)}</h2>");
        html.Append($"<p class=help>{Escape(strings.ModulesHelp)}</p>");

        foreach (var option in catalog.Modules)
        {
            var on = submitted is null
                ? option.PreSelected
                : submitted.BootModules.Any(e => OrdinalEquals(e, option.Entry));
            html.Append("<label class=choice>");
            html.Append(
                $"<input type=checkbox name=modules value=\"{Escape(option.Entry)}\"{(on ? " checked" : "")}>");
            html.Append($"<span>{Escape(option.DisplayName)}");
            if (option.Description is { } description)
                html.Append($" <em>{Escape(description)}</em>");
            html.Append("</span></label>");
        }

        html.Append("</section>");
    }

    private static void Field(
        StringBuilder html, string name, string label, string? value, bool password = false,
        string? placeholder = null, string extraAttributes = "")
    {
        var id = name.Replace('.', '-');
        html.Append("<div class=field>");
        html.Append($"<label for=\"{Escape(id)}\">{Escape(label)}</label>");
        html.Append(
            $"<input id=\"{Escape(id)}\" name=\"{Escape(name)}\" type={(password ? "password" : "text")} "
            + $"value=\"{Escape(value)}\" placeholder=\"{Escape(placeholder)}\" spellcheck=false{extraAttributes}>");
        html.Append("</div>");
    }

    /// <summary>
    /// <c>disabled</c> when a route is already answered by this deployment.
    ///
    /// <para><b>disabled, not readonly.</b> A disabled control is not submitted at all, so the
    /// operator cannot send a value that would be silently outranked; a readonly one still posts
    /// its contents.</para>
    /// </summary>
    private static string Disabled(bool locked) => locked ? " disabled" : "";

    private static void AppendMessages(
        StringBuilder html, string cssClass, string heading, ImmutableList<string>? messages)
    {
        if (messages is not { Count: > 0 })
            return;
        html.Append($"<div class=\"notice {cssClass}\" role=alert><strong>{Escape(heading)}</strong><ul>");
        foreach (var message in messages)
            html.Append($"<li>{Escape(message)}</li>");
        html.Append("</ul></div>");
    }

    private static void Open(StringBuilder html, SetupStrings strings, string title)
    {
        // lang carries the resolved locale so a screen reader and the browser's own translation
        // prompt both read the page as what it is.
        var lang = MeshWeaver.Messaging.Locales.Resolve(strings.Locale);
        html.Append("<!doctype html><html lang=\"").Append(Escape(lang)).Append("\"><head>");
        html.Append("<meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\">");
        html.Append("<meta name=robots content=\"noindex,nofollow\">");
        html.Append($"<title>{Escape(title)}</title>");
        html.Append("<style>").Append(Css).Append("</style></head><body><main>");
    }

    private static void Close(StringBuilder html) => html.Append("</main></body></html>");

    private static bool OrdinalEquals(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// HTML-escapes a value for an attribute or text node. Null becomes empty.
    ///
    /// <para><see cref="WebUtility.HtmlEncode(string)"/> escapes <c>&lt; &gt; &amp; " '</c>, which covers
    /// both positions — every attribute in this page is double-quoted.</para>
    /// </summary>
    private static string Escape(string? value) =>
        string.IsNullOrEmpty(value) ? "" : WebUtility.HtmlEncode(value);

    private const string Css = """
        :root{color-scheme:light dark;--fg:#1b1b1f;--bg:#fbfbfd;--card:#fff;--line:#d8d8e0;
        --muted:#5b5b66;--accent:#3b5bdb;--warn:#8a5a00;--warnbg:#fff8e6;--err:#a11;--errbg:#fdeaea}
        @media(prefers-color-scheme:dark){:root{--fg:#e8e8ed;--bg:#16161a;--card:#1e1e24;
        --line:#33333d;--muted:#a0a0ad;--accent:#8ea3ff;--warn:#e0b050;--warnbg:#2a2312;
        --err:#ff9a9a;--errbg:#2c1a1a}}
        *{box-sizing:border-box}
        body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.55 system-ui,-apple-system,
        "Segoe UI",Roboto,sans-serif}
        main{max-width:44rem;margin:0 auto;padding:2.5rem 1.25rem 4rem}
        h1{font-size:1.6rem;margin:0 0 .25rem}
        h2{font-size:1.1rem;margin:0 0 .35rem}
        h3{font-size:1rem;margin:1.25rem 0 .35rem}
        .lead{color:var(--muted);margin:0 0 1.75rem}
        section{background:var(--card);border:1px solid var(--line);border-radius:10px;
        padding:1.25rem;margin-bottom:1.25rem}
        .help{color:var(--muted);font-size:.875rem;margin:.3rem 0 .9rem}
        .field{margin:0 0 .9rem}
        label{display:block;font-weight:600;font-size:.875rem;margin-bottom:.3rem}
        input[type=text],input[type=password],textarea{width:100%;padding:.5rem .6rem;
        border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--fg);
        font:inherit}
        input:focus,textarea:focus{outline:2px solid var(--accent);outline-offset:1px}
        .choice{display:flex;gap:.55rem;align-items:baseline;font-weight:400;margin:0 0 .45rem}
        .choice input{margin:0}
        .choice em{color:var(--muted);font-style:normal;font-size:.875rem}
        fieldset{border:1px solid var(--line);border-radius:8px;padding:.9rem;margin:0 0 .9rem}
        legend{font-weight:600;font-size:.875rem;padding:0 .35rem}
        .badge{font-weight:400;color:var(--muted);font-size:.8rem}
        .identity{background:var(--card);border-color:var(--line);color:var(--fg)}
        .identity .badge{display:block;margin-top:.2rem;font-family:ui-monospace,SFMono-Regular,monospace}
        input[readonly]{color:var(--muted);font-family:ui-monospace,SFMono-Regular,monospace}
        .notice{border-radius:8px;padding:.9rem 1.1rem;margin:0 0 1.25rem;border:1px solid}
        .notice ul{margin:.4rem 0 0;padding-left:1.1rem}
        .problems{background:var(--errbg);border-color:var(--err);color:var(--err)}
        .warnings{background:var(--warnbg);border-color:var(--warn);color:var(--warn)}
        button{background:var(--accent);color:#fff;border:0;border-radius:7px;padding:.6rem 1.4rem;
        font:600 15px/1 inherit;cursor:pointer}
        button:hover{filter:brightness(1.08)}
        """;
}
