using System.IdentityModel.Tokens.Jwt;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using Memex.Portal.Shared.Email;
using Memex.Portal.Shared.SelfUpdate;
using Memex.Portal.Shared.Settings;
using Memex.Portal.Shared.Social;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Hosting.Grpc;
using MeshWeaver.Blazor.Pages;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Blazor.Portal.Authentication;
using MeshWeaver.Hosting.AspNetCore.Portal.Authentication;
using MeshWeaver.Blazor.Portal.Chat;
using MeshWeaver.Blazor.Portal.Components;
using MeshWeaver.Blazor.Portal.Layout;
using MeshWeaver.ContentCollections;
using MeshWeaver.Documentation;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.PluginCatalog;
using MeshWeaver.InstanceSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Hosting.Blazor;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Hosting.SignalR;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using PortalAuthOptions = MeshWeaver.Hosting.AspNetCore.Portal.Authentication.AuthenticationOptions;

namespace Memex.Portal.Shared;

public static class MemexConfiguration
{
    /// <summary>
    /// Conditional fluent step: applies <paramref name="apply"/> only when
    /// <paramref name="condition"/> holds — keeps feature-flagged registrations readable inside
    /// long builder chains.
    /// </summary>
    public static T If<T>(this T value, bool condition, Func<T, T> apply)
        => condition ? apply(value) : value;

    /// <summary>
    /// Configures web portal services for Memex.
    /// Pattern taken from MeshWeaver.Portal's SharedPortalConfiguration.
    /// </summary>
    public static void ConfigureMemexServices(this WebApplicationBuilder builder)
    {
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true,
                reloadOnChange: true)
            .AddEnvironmentVariables();

        var services = builder.Services;

        // Trust forwarded headers from Azure Container Apps reverse proxy
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                                     | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                                     | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        services.AddRazorPages();

        // Static web assets of modules that ship via modules/<Name>/ (#1724). Mounted by
        // UseMeshModuleStaticAssets below; the manifest it builds also tells App.razor which
        // module stylesheets to link, since a flipped module's scoped CSS is published
        // standalone rather than folded into the host's <App>.styles.css aggregate.
        services.AddMeshModuleStaticAssets();

        // ── GUI shells (Features:Gui) — the Blazor pipeline is OPTIONAL per deployment ─────────
        // A next-only portal (Features__Gui__Blazor=false) serves every mesh surface (REST,
        // gRPC-web, SignalR, MCP, auth, static assets) and NO Razor components: no circuit, no
        // Blazor view registry, no per-circuit portal hubs. Both shells default ON — an absent
        // section preserves today's behaviour.
        var guiShells = (builder.Configuration
            .GetSection(MemexFeatureOptions.SectionName)
            .Get<MemexFeatureOptions>() ?? new MemexFeatureOptions()).Gui;
        if (guiShells.Blazor)
            services.AddRazorComponents()
                .AddInteractiveServerComponents()
                .AddHubOptions(opt =>
                {
                    opt.DisableImplicitFromServicesParameters = true;
                })
                .AddBlazorPortalServices();

        // Onboarding service — pulls the three-row dual-write out of
        // Onboarding.razor so it's unit-testable end-to-end.
        services.AddScoped<Memex.Portal.Shared.Authentication.UserOnboardingService>();
        // Invitation service — reads/writes Invitation nodes for invitation-only onboarding.
        services.AddScoped<Memex.Portal.Shared.Authentication.InvitationService>();
        // Space invite — grant an existing user now, or schedule the grant (+ create an invitation)
        // for when an unknown email's account is created. Backed by the ScheduledActionRunner.
        services.AddSingleton<MeshWeaver.Graph.SpaceInviteService>();

        // AI services — thread persistence is handled via MeshNodes.
        // Anthropic / AzureFoundry / AzureOpenAI registration is now a
        // single per-provider builder extension (.AddAnthropic() etc.)
        // wired in ConfigureMemexMesh — that one call registers the catalog
        // source + IOptions binding + IChatClientFactory.
        //
        // Deploy-time feature flags gate which providers/CLIs ship. Defaults are
        // all-on (an absent Features section = current behaviour, no regression).
        // A disabled flag is the operator's intent and wins even if a key is
        // configured. Both the services-tier factory registration here AND the
        // mesh-tier catalog source in ConfigureMemexMesh are gated symmetrically
        // so a provider can't half-register.
        var features = builder.Configuration
            .GetSection(MemexFeatureOptions.SectionName)
            .Get<MemexFeatureOptions>() ?? new MemexFeatureOptions();

        // Bind Features as IOptions so application code (e.g. the onboarding flow's
        // self-provisioning gate) resolves the toggles through standard DI rather
        // than re-reading the configuration section ad hoc.
        services.Configure<MemexFeatureOptions>(
            builder.Configuration.GetSection(MemexFeatureOptions.SectionName));

        // System email (Microsoft Graph /sendMail). Disabled by default → NoOp sender so
        // local dev and tests never send. When Email:Enabled=true, GraphEmailSender sends as
        // the configured no-reply mailbox using the Mail.Send application permission. Backs the
        // invitation flow (admin Invitations settings tab).
        var emailOptions = builder.Configuration
            .GetSection(EmailOptions.SectionName)
            .Get<EmailOptions>() ?? new EmailOptions();
        services.AddSingleton(emailOptions);
        // The notification triage runner (escalates in-app notifications to email/Teams per each
        // recipient's NotificationRules) rides the MeshWeaver.Notifications.Channels module
        // (Modules:Assemblies); its hosted service self-skips unless Email:Enabled.

        // Executive Assistant: per-user JUST-IN-TIME delegated Graph access (the user consents to the
        // EA touching THEIR OWN mailbox only when they first use the tool — no standing app
        // permission). EaGraphAuth drives the consent/token flow; it is raw OAuth over HTTP, so it
        // stays HERE with its consent controller — the EA's mailbox TOOLS (which do use the Graph
        // SDK) ride the MeshWeaver.Mail.MicrosoftGraph module and depend only on this seam.
        //
        // 🚨 UNCONDITIONAL, and it must stay that way. EaConsentController is registered by
        // AddControllers().AddApplicationPart(...) below — which discovers controllers by TYPE, with
        // no idea what any of them needs — so /auth/ea/connect and /auth/ea/callback are routed on
        // EVERY deployment. Gating this one registration on Email:Enabled therefore did not disable
        // the endpoint; it left it routed with an unresolvable dependency, and MVC's activator threw
        // "Unable to resolve service for type 'MeshWeaver.Mesh.IEaGraphAuth' while attempting to
        // activate 'EaConsentController'" — a deterministic 500 on the consent flow, in memex prod
        // (issue #2218). A controller and its dependencies are one unit: whatever routes the one
        // must register the other.
        //
        // Nothing is enabled by registering it. The descriptor is inert (a typed HttpClient factory
        // registration does no work until something resolves it), and IEaGraphAuth carries its own
        // honest feature probe: EaGraphAuth.IsConfigured reads Authentication:Microsoft
        // ClientId/ClientSecret — the credentials the DELEGATED flow actually needs, which are not
        // what Email:Enabled describes (that gates the SYSTEM mailbox's app-permission sender). The
        // controller checks IsConfigured first and answers 400 "not configured", which is the honest
        // answer for a deployment that has not set up the EA — instead of a 500 that reads as a bug.
        services.AddHttpClient<IEaGraphAuth, Authentication.EaGraphAuth>();

        // 🚨 TryAdd, deliberately. The Graph sender lives in the MeshWeaver.Mail.MicrosoftGraph
        // module, which registers it with a plain AddSingleton. The pairing is ORDER-INDEPENDENT:
        // whichever runs first, the LAST registration of a service type is what GetRequiredService
        // returns and TryAdd declines when any registration already exists. Module listed ⇒ the
        // Graph sender wins; module absent ⇒ this no-op keeps OutboundEmailSender and
        // InvitationEmailSender — both of which GetRequiredService<IEmailSender> — resolvable
        // instead of throwing at startup.
        //
        // 🚨 Resolvable is NOT the same as able to send, and conflating the two was #2023: with
        // Email:Enabled=true and the module absent, this fallback used to report success for every
        // send, so OutboundEmailSender stamped queued mail New → Sending → Sent while nothing left
        // the process. Both halves of that are now refused — the no-op fails loudly on this
        // configuration (NoOpEmailSender), and the two watchers decline to start at all
        // (EmailDeliveryGuard), leaving mail visibly queued instead of falsely delivered.
        services.TryAddSingleton<IEmailSender, NoOpEmailSender>();

        // Inbound email→agent channel (intake). Mail is treated as a chat device: each inbound email
        // finds-or-creates a conversation thread and appends its latest message (referencing the email
        // by path). The Graph subscription self-skips unless Email:Enabled && Email:InboundEnabled.
        // (GraphMail, EmailInboundProcessor and the Graph subscription watcher ride the
        // MeshWeaver.Mail.MicrosoftGraph module, together with POST /api/email — the change
        // notification webhook — which it contributes through MapMeshModuleEndpoints.)
        // Mesh-driven reply sender: drains agent-emitted Outbound Email nodes (Status=New) via Graph.
        services.AddHostedService<OutboundEmailSender>();
        // Mesh-driven invitation emailer: emails any Pending Invitation node not yet emailed
        // (EmailSentAt==null), from ANY entry point (Invitations tab, MCP, REST). Self-skips
        // unless Email:Enabled. Decouples the invite email from the UI handler.
        services.AddHostedService<Email.InvitationEmailSender>();
        // Event-subscription runner: fires durable "when THIS trigger fires, run THAT continuation"
        // subscriptions (e.g. grant a Space role the moment an invited user's account is created) —
        // live via the change feed + reconciled against current state on startup so a trigger during
        // downtime still fires. Migrates any legacy ScheduledAction nodes on startup.
        services.AddHostedService<MeshWeaver.Graph.EventSubscriptionRunner>();
        // Access-grant notifier: watches AccessAssignment creations on the change feed and notifies the
        // granted user ("You've been given <role> access to <node>") through NotificationService.Dispatch
        // (honours their per-category bell/email preferences). Covers every grant path in one place.
        services.AddHostedService<MeshWeaver.Graph.AccessGrantNotifier>();
        // Startup-error reporter: an extra ILoggerProvider buffers every Error/Critical logged during
        // the startup window (host build → ApplicationStarted); once the host is up, ONE Admin-partition
        // bell notification summarizes them so platform admins learn about a degraded boot (failed seed
        // import, hub-initialization failure, DI fault) without reading pod logs. RLS on the Admin
        // partition scopes the bell to platform admins. Best-effort end to end — it never fails startup.
        services.AddSingleton<MeshWeaver.Graph.StartupErrorBuffer>();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider,
            MeshWeaver.Graph.StartupErrorBufferLoggerProvider>();
        services.AddHostedService<MeshWeaver.Graph.StartupErrorNotifier>();
        // App-level event-log outbox: durably records every change-feed event (Postgres in prod via
        // PostgreSqlEventLogStore, else in-memory) + replays not-yet-processed entries on startup.
        services.AddMeshEventLog();

        // Microsoft Teams bot channel (bidirectional). Registered always but INERT unless Teams:Enabled
        // and Bot credentials are set (TeamsClient.IsConfigured gates the endpoint + sender). Activate by
        // provisioning an Azure Bot resource + Teams app and setting the Teams config.
        // (The Teams bot channel rides the MeshWeaver.Teams module: the client, the inbound router
        // and the reply sender register through its attribute, and POST /api/teams/messages is
        // contributed through MapMeshModuleEndpoints. Everything stays inert until the bot
        // credentials are configured — the client reports IsConfigured false, the endpoint answers
        // 404 and the reply sender self-skips at ApplicationStarted.)

        // Shared on-disk WORKSPACE dir the agent→skill sync maintains (.claude/skills + AGENTS.md); both
        // CLI harnesses set it as the session's working directory so every session sees the MeshWeaver
        // agents/skills + the mesh-is-via-MCP base instructions. Defaults to a sibling of the per-user
        // .claude root (e.g. /mnt/users → /mnt/users/_skills) when not explicitly configured.
        var skillsDir = builder.Configuration["Skills:Directory"];
        if (string.IsNullOrWhiteSpace(skillsDir))
        {
            var claudeRoot = builder.Configuration["ClaudeCode:ConfigDirRoot"]?.TrimEnd('/', '\\');
            skillsDir = string.IsNullOrEmpty(claudeRoot) ? null : $"{claudeRoot}/_skills";
        }

            // The ClaudeCode/Copilot packs bind their own options (incl. the SkillsDirectory
            // derivation) from configuration when loaded via Modules:Assemblies.

        // Reactive skill→file sync: writes AGENTS.md (the base "mesh-is-via-MCP" instructions + a LISTING
        // of the platform nodeType:Skill catalog — name, description, load path) to the shared volume and
        // keeps it in sync as skill nodes change (observable query). Skill BODIES are never written to
        // disk — the harness reads each on demand via the meshweaver MCP `get`. Runs for the process lifetime.
        if ((features.Ai.Clis.ClaudeCode || features.Ai.Clis.Copilot) && !string.IsNullOrWhiteSpace(skillsDir))
        {
            services.Configure<Skills.AgentSkillSyncOptions>(o => o.Directory = skillsDir);
            services.AddHostedService<Skills.AgentSkillSyncService>();
        }

        // Register the AI chat services (must be after all factory registrations)
        services.AddAgentChatServices();

        // (The WebSearch agent tools ride the MeshWeaver.AI.WebSearch module — listed under
        // Modules:Assemblies, binding its own WebSearch configuration section. Agent plugins
        // resolve by name out of DI, so the composition root carries no reference to them.)

        services.AddHttpContextAccessor();
        services.AddHttpClient();
        services.AddSignalR();
        services.AddControllers()
                .AddApplicationPart(typeof(MemexConfiguration).Assembly);
        services.Configure<StylesConfiguration>(
            builder.Configuration.GetSection("Styles"));

        // Register API token service for MCP bearer auth and OAuth code store
        services.AddSingleton<ApiTokenService>();
        // Registers MeshWeaver installations and issues their instance keys. Separate from
        // ApiTokenService on purpose: an instance key identifies a DEPLOYMENT and carries no user
        // identity or roles, so it can never be replayed as its owner against the mesh API.
        services.AddSingleton<MeshWeaverInstanceService>();
        services.AddSingleton<OAuthCodeStore>();
        // Draws the fallback Open Graph card for public pages that authored no image. Singleton
        // because it decodes its embedded font once and holds the typeface as an instance field.
        services.AddSingleton(sp => new Memex.Portal.Shared.Seo.OgCardRenderer(
            sp.GetRequiredService<IConfiguration>()["Portal:SiteName"]
            ?? sp.GetRequiredService<IConfiguration>()["Portal:InstanceName"]
            ?? "Memex"));
        // Automatic, token-based MCP back-connection for the co-hosted Claude Code / Copilot CLIs.
        // The chat clients resolve this at spawn to mint/reuse the per-user MCP ApiToken + URL.
        services.AddSingleton<MeshWeaver.AI.Connect.IMcpBackConnection, McpBackConnectionService>();
        // ModelProviderService backs the Models settings tab — users store
        // their own AI provider credentials as MeshNodes in their namespace.
        services.AddSingleton<Memex.Portal.Shared.Models.ModelProviderService>();
        // ProviderModelLister moved to MeshWeaver.AI (AddAgentChatServices registers it) — the
        // add-provider flow and the OpenAI module's model-discovery sync both resolve it there.
        // OpenAI-compatible (Ollama) model auto-discovery rides the MeshWeaver.AI.OpenAI MODULE
        // now (OpenAIProvidersAttribute registers the hosted sync; it self-gates on
        // OpenAICompatible:Endpoint + DiscoverModels=true) — nothing to register here.

        // GitHub sync — per-user OAuth credential (device flow) + bidirectional
        // Space ↔ GitHub sync (export = "sync back"; import = create / re-import a
        // Space at any commit). The OAuth client id is bound from GitHub:OAuth;
        // absent a client id the Connect flow is gracefully disabled.
        services.AddGitHubSyncServices();
        services.Configure<GitHubOAuthOptions>(builder.Configuration.GetSection("GitHub:OAuth"));
        // GitHub App machine identity (GitHub:App:ClientId + PrivateKey [+ InstallationId/Owner]):
        // server-side sync — the plugin registry pulling the plugins repo — logs on AS THE APP
        // (installation token), never with a user's personal credential.
        services.Configure<GitHubAppOptions>(builder.Configuration.GetSection("GitHub:App"));
        // Framework-release broadcaster subscribers (FrameworkBroadcast:Subscribers) — the interim
        // config source for who gets a repository_dispatch when the platform releases. Only the
        // control instance carries a list; the durable home is the Hosting fleet's subscriber
        // registry, which passes its set to FrameworkReleaseBroadcaster.Broadcast directly.
        services.Configure<FrameworkBroadcastOptions>(builder.Configuration.GetSection("FrameworkBroadcast"));
        // (Course assets ride the MeshWeaver.Courses module — its attribute registers the
        // resolver and contributes GET /assets/{Space}/{path…} through MapMeshModuleEndpoints.
        // It reads the same GitHub App credentials configured above.)

        // Instance sync — bidirectional Space replication to another MeshWeaver instance
        // (per-space registry at {space}/_Sync; offline changes accumulate in the durable
        // manifest and drain when the remote is reachable again).
        services.AddInstanceSyncServices();

        // Per-user CLI Connect (Settings → Models, CLI providers). The
        // ConnectSessionManager is a mesh-scoped singleton holding the live
        // login Process between "show URL" and "paste code" (instance dict,
        // 5-min timeout). Each gated CLI registers its IConnectStrategy. The
        // captured token is persisted as an encrypted ModelProvider via the
        // ConnectTokenSink (seam over ModelProviderService, so the AI layer
        // never references the portal assembly).
        services.AddSingleton<MeshWeaver.AI.Connect.IConnectTokenSink, Memex.Portal.Shared.Models.ConnectTokenSink>();
        services.AddSingleton<MeshWeaver.AI.Connect.ConnectSessionManager>();
        if (features.Ai.Clis.ClaudeCode)
        {
            services.AddSingleton<MeshWeaver.AI.Connect.IConnectStrategy, MeshWeaver.AI.Connect.ClaudeConnectStrategy>();
            // Wire the Connect login: bind ClaudeConnect:* overrides, default the PTY wrapper ON for
            // the co-hosted Linux portal (claude setup-token renders an Ink UI that needs a real TTY —
            // see ClaudeConnectStrategy), and mirror the per-user .claude root the co-hosted client uses
            // (ClaudeCode:ConfigDirRoot, e.g. /mnt/users) so each user logs in under their own dir.
            services.Configure<MeshWeaver.AI.Connect.ClaudeConnectOptions>(o =>
            {
                builder.Configuration.GetSection("ClaudeConnect").Bind(o);
                if (builder.Configuration["ClaudeConnect:UsePseudoTerminal"] is null && !OperatingSystem.IsWindows())
                    o.UsePseudoTerminal = true;
                if (string.IsNullOrEmpty(o.ConfigDirRoot))
                    o.ConfigDirRoot = builder.Configuration["ClaudeCode:ConfigDirRoot"];
            });
        }
        // CopilotConnectStrategy registers from the Copilot pack (Modules:Assemblies).

        // Social publishing (LinkedIn connect/publish/page-sync + node-menu providers) rides the
        // MeshWeaver.Social MODULE (Modules:Assemblies): SocialMeshModuleAttribute registers the
        // DI services + menu providers, SocialModuleAttribute contributes the endpoints via
        // app.MapMeshModuleEndpoints() below, and the module now registers its own ApiCredential
        // NodeType. Only the LinkedIn SIGN-IN scheme (AddLinkedInAuthentication) stays compiled
        // here, because auth schemes configure before the host builds — module or no module.

        // Configure authentication
        var authSection = builder.Configuration.GetSection(PortalAuthOptions.SectionName);
        var entraIdConfig = builder.Configuration.GetSection("EntraId");

        // Determine provider mode from configuration
        var hasExternalProviders = AuthenticationBuilderExtensions.HasExternalProviders(builder.Configuration);
        var externalProviders = AuthenticationBuilderExtensions.GetConfiguredProviders(builder.Configuration);

        var provider = authSection["Provider"]
            ?? (hasExternalProviders ? AuthenticationProviders.Custom
                : entraIdConfig.GetChildren().Any() ? AuthenticationProviders.MicrosoftIdentity
                : AuthenticationProviders.Dev);

        var enableDevLogin = authSection.GetValue<bool?>("EnableDevLogin")
                             ?? (provider == AuthenticationProviders.Dev);

        // Register authentication navigation service
        services.AddAuthenticationNavigation(options =>
        {
            options.Provider = provider;
            options.Providers = externalProviders;
            options.EnableDevLogin = enableDevLogin;

            if (authSection["LoginPath"] is { } loginPath)
                options.LoginPath = loginPath;
            if (authSection["LogoutPath"] is { } logoutPath)
                options.LogoutPath = logoutPath;
        });

        // Reserved single-segment Blazor page routes (/login, /privacy, /search, …) — derived
        // from the SAME assemblies Routes.razor gives the Router. NavigationService short-circuits
        // these before mesh path resolution, so a bare page URL (e.g. the anonymous /privacy) is
        // never resolved as a partition root and anonymous-gated to /login. See PageRouteRegistry.
        services.AddSingleton(new MeshWeaver.Hosting.Blazor.PageRouteRegistry(
            typeof(Routes).Assembly,
            typeof(MeshWeaver.Blazor.Pages.ApplicationPage).Assembly,
            typeof(MeshWeaver.Blazor.Portal.Pages.CreateNode).Assembly));

        // Data protection: set application name here, but key persistence is deployment-specific.
        // Monolith → PersistKeysToFileSystem (in Program.cs)
        // Distributed → PersistKeysToAzureBlobStorage + ProtectKeysWithAzureKeyVault (in Program.cs)
        services.AddDataProtection()
            .SetApplicationName("MemexPortal");

        if (provider == AuthenticationProviders.MicrosoftIdentity && !hasExternalProviders)
        {
            // Legacy single-provider MicrosoftIdentity mode (OIDC via EntraId section)
            JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
            services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApp(entraIdConfig);
            // This branch has its own OIDC correlation/nonce handshake cookies, and an abandoned
            // login here piles them up exactly like the unified handlers' — same eviction policy,
            // same constant (see AuthenticationBuilderExtensions.LoginHandshakeCookieMaxAge).
            services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.CorrelationCookie.MaxAge = AuthenticationBuilderExtensions.LoginHandshakeCookieMaxAge;
                options.NonceCookie.MaxAge = AuthenticationBuilderExtensions.LoginHandshakeCookieMaxAge;
            });
            services.AddControllersWithViews()
                .AddMicrosoftIdentityUI();
        }
        else
        {
            // Unified cookie-based auth: supports dev login, external providers, or both
            var authBuilder = services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.LogoutPath = hasExternalProviders ? "/auth/logout" : "/dev/logout";
                options.ExpireTimeSpan = TimeSpan.FromDays(14);
                options.SlidingExpiration = true;
                options.Cookie.Name = "MemexAuth";
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
            });

            // Register external providers from configuration
            authBuilder
                .AddMicrosoftAuthentication(builder.Configuration)
                .AddGoogleAuthentication(builder.Configuration)
                .AddLinkedInAuthentication(builder.Configuration)
                .AddAppleAuthentication(builder.Configuration);
        }

        // MCP auth is deliberately separate from the Blazor cookie pipeline above —
        // see McpAuthenticationExtensions for the "why". Bearer-only, no cookie leakage,
        // proper 401 + WWW-Authenticate on anonymous requests so MCP clients can
        // discover the auth server.
        services.AddMcpAuthentication();

        // REST surface for the mesh — same Bearer-token policy as MCP, lifts the
        // multipart upload size cap. See MeshApiEndpoints.
        services.AddMeshApi();

        // Centralized speech-to-text is a MODULE now (MeshWeaver.Speech in Modules:Assemblies —
        // SpeechModuleAttribute binds the `Speech` section via the options pipeline and registers
        // ISpeechTranscriber). The compiled surface degrades without it: the mic UI resolves the
        // transcriber optionally and hides, and POST /api/speech/transcribe answers 503. See
        // Doc/Architecture/CentralizedSpeech.
    }

    /// <summary>
    /// Says out loud, once at startup, which ACTIVATED modules did not host-load here (#2093).
    ///
    /// <para>🚨 <b>Why this cannot be left to the module's own code.</b> A module that does not load
    /// runs nothing — including anything that would have complained. <c>MapMeshModuleEndpoints</c>
    /// scans only LOADED assemblies, so an activated endpoint provider that never made it into the
    /// process contributes no routes and its whole HTTP surface answers 404 for the pod's lifetime,
    /// with no exception, no warning and nothing to grep. On memex.systemorph that was <c>/mcp</c>,
    /// dead through two clean rolling restarts while <c>/health</c> and <c>/readyz</c> were 200 and
    /// the activation record cheerfully listed the module as installed. Absence of evidence read as
    /// evidence of absence, again.</para>
    ///
    /// <para>The two cases are reported differently because the remedies are: a module whose bytes
    /// are on the volume is one restart from working, while a module whose landed assembly is GONE
    /// is a half-completed landing that no restart repairs — that one is an ERROR naming the
    /// re-install. Same reader, same wording, as the <c>pending_module_activation</c> health check,
    /// so the pod log and the probe can never disagree.</para>
    /// </summary>
    private static void ReportUnloadedActivatedModules(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MeshWeaver.PluginCatalog.ModuleActivation");
        // 🚨 CONSTRUCTED, not resolved — deliberately. PendingModuleActivations is registered in
        // the MESH container (AddPluginCatalog), which is why every other caller reaches it through
        // `hub.ServiceProvider` and guards with GetService. Asking `app.Services` for it would
        // throw at startup on any host where the two containers differ — a diagnostic that CRASHES
        // the portal it exists to inform is worse than the silence it replaces. Constructing costs
        // nothing and cannot differ: the reader is a stateless file reader that starts nothing and
        // writes nothing, and the registration is this same one-liner over the same resolved
        // module root.
        var report = new PendingModuleActivations(app.Configuration).Read();

        if (report.IsUndetermined)
        {
            logger.LogError(
                "Module endpoint contributions mapped, but this pod cannot say whether an "
                + "activated module failed to load: {Reason}", report.Describe());
            return;
        }

        if (report.HasUnresolvable)
            logger.LogError(
                "🚨 {Count} ACTIVATED module(s) are not loaded in this process and NO RESTART will "
                + "load them — any HTTP endpoint, view or provider they contribute is silently "
                + "absent (a 404 with no error) until the package is re-installed: {Detail}",
                report.Unresolvable.Count,
                ModuleActivationStatus.DescribeUnresolvable(report.Unresolvable));

        if (report.HasPending)
            logger.LogWarning(
                "{Count} activated module(s) are landed but not loaded in this process — whatever "
                + "they contribute (endpoints included) is absent until a restart: {Detail}",
                report.Pending.Count, ModuleActivationStatus.Describe(report.Pending));
    }

    /// <summary>
    /// Fails fast on the content-storage configuration that GUARANTEES silent data loss (issue #435):
    /// a DEPLOYED (non-development) <c>FileSystem</c> content store whose <c>BasePath</c> is empty or
    /// relative. Such a path resolves against the container's ephemeral working directory (<c>/app</c>),
    /// so every uploaded collection file is written to disk that vanishes on the next pod restart or
    /// grain teardown — reads succeed for minutes, then the files are gone, with no signal to the user.
    /// We cannot verify from code that an <em>absolute</em> path is a durable mount, but we CAN reject
    /// the empty/relative footgun outright. A local <c>Development</c> run keeps the relative-to-working-
    /// tree convenience (the Monolith's <c>Storage:BasePath = "../../samples/Graph"</c>).
    /// <para>Pure decision (no I/O) so it is unit-testable without spinning a mesh.</para>
    /// </summary>
    /// <param name="contentStorageConfig">The parsed <c>Storage</c> section, or <c>null</c> when unconfigured.</param>
    /// <param name="isDevelopment"><c>true</c> for a local Development run (relative BasePath allowed).</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a non-development FileSystem content store has an empty or relative <c>BasePath</c>.
    /// </exception>

    public static void ValidateContentStorageDurability(
        ContentCollectionConfig? contentStorageConfig, bool isDevelopment)
    {
        // Development resolves a relative BasePath against a stable working tree — that's the intended
        // local convenience, not the ephemeral-container footgun. Nothing configured → nothing to guard.
        if (isDevelopment || contentStorageConfig is null)
            return;
        // Only the FileSystem store roots files on a local path. AzureBlob/other stores don't use
        // BasePath as a filesystem root (it's a blob prefix), so the durability concern doesn't apply.
        if (!string.Equals(contentStorageConfig.SourceType, "FileSystem", StringComparison.OrdinalIgnoreCase))
            return;

        var basePath = contentStorageConfig.BasePath;
        var isEmpty = string.IsNullOrWhiteSpace(basePath);
        // An absolute path is the operator's chosen mount; code can't verify it's durable, so allow it.
        if (!isEmpty && Path.IsPathRooted(basePath))
            return;

        throw new InvalidOperationException(
            "Content storage misconfiguration (issue #435): Storage:SourceType is 'FileSystem' but "
            + (isEmpty ? "Storage:BasePath is empty." : $"Storage:BasePath ('{basePath}') is relative.")
            + " A FileSystem content store with an empty or relative BasePath resolves against the "
            + "container's ephemeral working directory, so uploaded collection files are written to "
            + "storage that is SILENTLY LOST on the next pod restart or grain teardown. Set "
            + "Storage:BasePath to an ABSOLUTE path backed by a durable volume (e.g. '/mnt/content' on "
            + "a PersistentVolumeClaim), or use Storage:SourceType 'AzureBlob'. Refusing to start so the "
            + "misconfiguration surfaces now rather than after users have uploaded and lost files.");
    }

    /// <summary>
    /// The AI (✨) menu's COMPILED remainder: exactly the imperative "New thread" entry. The
    /// navigation entries (Threads / Models / Tiers / Providers / Agents / Skills) migrated to
    /// seeded <c>UiContribution</c> nodes (<see cref="AiMenuContributions"/>, WS7 slice 3) — they
    /// are pure links, so they ride the same lane a plugin's AI-menu entry arrives through.
    /// This list stays the SINGLE SOURCE OF TRUTH for the imperative entry, shared by the hub
    /// registration below and the unit tests, so a regression can't quietly drop it.
    /// <para>
    /// 🚨 "New thread" carries NO <c>Href</c> because it CANNOT: the composer lives at
    /// <c>/User/{me}/Chat</c>, and the signed-in user is not known here — this seed is static and
    /// registered per node hub, with no viewer identity. So the destination has to be resolved at CLICK
    /// time from the circuit's <c>AccessService</c>. That is what the
    /// <see cref="PortalLayoutBase.AiNewThreadAction"/> sentinel is for: <c>HandleMenuItemClick</c>
    /// matches it FIRST and returns (open the composer in the MAIN pane + close the side panel), so an
    /// <c>Href</c> added here would be silently dead code rather than a redirect. Keep it null so the
    /// declaration matches the behaviour.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<NodeMenuItemDefinition> AiMenuItems { get; } =
    [
        // ➕ is deliberately a LANGUAGE-NEUTRAL glyph, not the chat.svg the "Threads" entry uses:
        // "new" is the whole meaning here, and a plus carries it in every locale without translation
        // (and without colliding visually with "Threads", which is also a chat bubble). Emoji icons
        // are the established convention in these menus — the Node menu is ✏️ 🔖 ➡️ 📋 🗑️ — and
        // MeshNodeImageHelper.IsEmoji routes it to a <span>, never an <img>.
        new NodeMenuItemDefinition("New thread", PortalLayoutBase.AiNewThreadAction,
            Icon: "➕", Order: 0,
            Tooltip: "Start a new conversation")
            { LabelKey = "menu.newThread", TooltipKey = "menu.newThreadTooltip" },
    ];

    extension<TBuilder>(TBuilder builder) where TBuilder : MeshBuilder
    {
        /// <summary>
        /// Configures the mesh with Graph domain only.
        ///
        /// Configuration is read from appsettings:
        /// - Graph:Storage:Type - Storage type: "FileSystem", "AzureBlob", "PostgreSql", "Cosmos"
        ///   or "Snowflake". Cosmos and Snowflake are BOOT PACKS: their factories register only
        ///   when the matching DLL (MeshWeaver.Hosting.Cosmos / .Snowflake) is listed under
        ///   Modules:Assemblies — installation runs before this selection, so ordering is safe.
        /// - Graph:Storage:BasePath - Base path for FileSystem storage
        /// - Graph:Storage:ConnectionString - Connection string for AzureBlob/Cosmos
        /// - storage - Content collection configuration (Name, SourceType, BasePath)
        /// </summary>
        public TBuilder ConfigureMemexMesh(IConfiguration configuration, bool isDevelopment = false)
        {
            // Boot-time module packs: DLL paths listed under Modules:Assemblies are loaded into
            // the default ALC BEFORE the container builds, and their MeshNodeProviderAttribute
            // registrations (services + nodes + hub configuration) fold into this mesh — the
            // per-deployment "which packs does this instance run" knob (Doc/Architecture/
            // UiExtensibility). Empty/absent = no-op; a listed path that fails to load should
            // fail loudly at startup, never silently run without the pack.
            //
            // #1664 step 9 — the effective set is the appsettings baseline ∪ the ENABLED entries
            // of the modules/activation.json sidecar (store-installed modules landed by
            // ModuleLandingService), deduped by name. Sidecar entries are guarded: a declared
            // minMeshVersion FLOOR the running platform no longer satisfies (a rollback below the
            // module's requirement) or a missing DLL SKIPS the entry with a loud stderr line —
            // never a crash, the deployment must boot; the entry stays for when the platform
            // moves forward again. A landed module's built-against MVID is diagnostic only:
            // modules bind by simple name across platform builds (the strict MVID gate is the
            // NodeType bake lane's). Pre-DI, so diagnostics go to stderr (pod stdout/stderr ship
            // to Loki regardless).
            var moduleAssemblies = configuration.GetSection("Modules:Assemblies").Get<string[]>();
            // 🚨 The SAME root ModuleLandingService writes (ModuleRoot) — never
            // AppContext.BaseDirectory directly. They must name one directory: a landed module
            // read from somewhere else is simply invisible, and on a deployment whose /app is
            // read-only the writer cannot use AppContext.BaseDirectory at all.
            var moduleRoot = ModuleRoot.Resolve(configuration);
            // Generations GC — delete only what NO activation entry references and nothing holds
            // open (skip-on-locked). Landing never deletes anything on a shared volume; this is
            // the one reclaim point. See ModuleLandingService.CollectGarbage.
            var collected = ModuleLandingService.CollectGarbage(moduleRoot);
            if (collected > 0)
                Console.WriteLine($"[ModuleActivation] modules GC removed {collected} unreferenced generation(s)");
            var persistedActivation = ModuleActivationSidecar.Read(moduleRoot,
                msg => Console.Error.WriteLine($"[ModuleActivation] {msg}"));
            var effectiveModules = ModuleActivationBoot.ComputeEffectiveModuleEntries(
                moduleAssemblies,
                persistedActivation,
                // The ONE module platform gate (ModulePlatformFloor) — never a second notion of
                // the module platform requirement.
                ModulePlatformFloor.DeclineReason,
                // 🚨 The entry's OWN landed directory SPECIFICALLY — modules/<Directory ?? name>/
                // <name>.dll — never ResolveModulePath, whose BaseDirectory fallback would let a
                // sidecar entry with a lost modules/ folder silently bind a same-named app-closure
                // DLL instead of being skipped. Baseline entries below keep ResolveModulePath (both
                // locations are legitimate for them). Passing the ENTRY rather than the name is
                // what makes this gate agree with the resolver below: landing writes generations
                // (modules/<name>@<gen>/) and moves the entry pointer, so a name-only check found
                // nothing for ANY generation-landed module and boot skipped every store module on
                // the deployment while its bytes sat correctly on disk (#1949).
                entry => ModuleActivationBoot.LandedModuleDllExists(moduleRoot, entry),
                (module, reason) => Console.Error.WriteLine(
                    $"[ModuleActivation] SKIPPED store-installed module '{module}': {reason}"));
            // 🚨 A LISTED-BUT-ABSENT module must never crash boot. `InstallAssemblies` does
            // `Assembly.LoadFrom`, which throws FileNotFoundException, so one stale line in
            // `Modules:Assemblies` takes the whole portal down before anything is serving —
            // observed on 3.0.0-rc5, whose image no longer ships the fourteen extracted modules
            // while appsettings still listed them: every boot died on
            // `Could not load file or assembly '/app/MeshWeaver.AI.OpenAI.dll'`.
            //
            // The sidecar half already skips a missing DLL loudly (LandedModuleDllExists above);
            // the appsettings BASELINE did not, and a baseline entry is exactly the one a platform
            // change can invalidate without touching the deployment. So the same rule applies to
            // both: skip, say so on stderr, and boot. A module that is genuinely required makes
            // itself known as a missing FEATURE, which is diagnosable — a portal that will not
            // start is not.
            var loadableModules = effectiveModules
                // 🚨 ONE resolution, shared with the existence gate above
                // (ModuleActivationBoot.ResolveLoadPath): a store-landed module resolves to the
                // directory ITS activation entry points at — the generation the landing wrote —
                // and a baseline entry keeps ResolveModulePath's probes. The provenance comes from
                // the union itself rather than being re-derived here; the gate and the resolver
                // each deciding for themselves where a module's bytes live is exactly #1949.
                .Select(module => (
                    Module: module,
                    Path: ModuleActivationBoot.ResolveLoadPath(moduleRoot, module)))
                .Where(candidate =>
                {
                    if (File.Exists(candidate.Path))
                        return true;
                    Console.Error.WriteLine(
                        $"[ModuleActivation] SKIPPED module '{candidate.Module.Entry}': no assembly at "
                        + $"'{candidate.Path}'. It is listed in Modules:Assemblies but this image "
                        + "does not ship it — delist it, or install it as a module. Booting without "
                        + "it; whatever it provided is absent.");
                    return false;
                })
                .ToArray();
            var resolvedModules = loadableModules.Select(candidate => candidate.Path).ToArray();

            // 🚨 #2223 — SAY WHICH COPY IS BEING LOADED. A view-pack fix can merge, build, land in
            // the module store and still not run, because a baseline Modules:Assemblies entry
            // resolves to the IMAGE copy and dedupes the store entry away by name. Every lane
            // reported green; the only evidence lived in /proc/1/maps on a prod pod. This reports
            // the paths that are about to be loaded — the SAME array, so the line and the load
            // cannot disagree — and warns when the store holds a newer, different copy. It warns
            // and boots: a pod that refuses to start cannot be given the fix for what is wrong
            // with it.
            ModuleLoadReport.Write(
                ModuleLoadReport.Describe(moduleRoot, loadableModules),
                Console.WriteLine,
                Console.Error.WriteLine);

            if (resolvedModules.Length > 0)
                builder.InstallAssemblies(resolvedModules);
            // Restart-as-activation: this boot IS the restart the sidecar was waiting for —
            // consume the pending flag so the step-10 signal reads current. Best-effort: on a
            // read-only app filesystem the flag simply stays set (cosmetic), and boot proceeds.
            // 🚨 Clears the MARKER, never rewrites the activation record (#2090). Rewriting the
            // whole list here meant every replica's boot read-modify-wrote a file the other
            // replicas were reading — on a rolling restart that is several writers and several
            // readers on one SMB file at once, which is how a boot read came back
            // FileNotFoundException and the pod started with NONE of its store modules (#2189).
            if (persistedActivation.PendingRestart)
                try
                {
                    ModuleActivationSidecar.SetPendingRestart(moduleRoot, false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[ModuleActivation] could not reset PendingRestart ({ex.GetType().Name}: "
                        + $"{ex.Message}) — the flag stays set; activation itself is unaffected.");
                }

            // Read graph storage config
            var graphStorageConfig = configuration.GetSection("Graph:Storage").Get<GraphStorageConfig>();
            if (graphStorageConfig == null)
            {
                throw new InvalidOperationException(
                    "Graph:Storage configuration is required. " +
                    "Configure it in appsettings.json with Type and BasePath/ConnectionString.");
            }

            // Resolve relative BasePath to absolute
            var basePath = graphStorageConfig.BasePath;
            if (!string.IsNullOrEmpty(basePath) && !Path.IsPathRooted(basePath))
            {
                basePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), basePath));
                graphStorageConfig = graphStorageConfig with { BasePath = basePath };
            }

            // In development, format JSON for readability
            if (isDevelopment)
            {
                var settings = graphStorageConfig.Settings != null
                    ? new Dictionary<string, string>(graphStorageConfig.Settings)
                    : new Dictionary<string, string>();
                settings["FormatJson"] = "true";
                graphStorageConfig = graphStorageConfig with { Settings = settings };
            }

            // Read content collection storage config from appsettings
            var contentStorageConfig = configuration.GetSection("Storage").Get<ContentCollectionConfig>();
            // 🚨 Fail fast on the guaranteed-silent-data-loss footgun (issue #435) BEFORE the
            // relative→absolute resolution below would MASK it: a deployed FileSystem content store
            // with an empty/relative BasePath resolves against the ephemeral container CWD (/app),
            // so uploaded collection files vanish on the next pod restart / grain teardown.
            ValidateContentStorageDurability(contentStorageConfig, isDevelopment);
            if (contentStorageConfig != null)
            {
                // Resolve relative path to absolute
                if (!string.IsNullOrEmpty(contentStorageConfig.BasePath) && !Path.IsPathRooted(contentStorageConfig.BasePath))
                {
                    contentStorageConfig = contentStorageConfig with
                    {
                        BasePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), contentStorageConfig.BasePath))
                    };
                }

                // Ensure Settings are populated for AzureBlob source type
                if (contentStorageConfig.SourceType == "AzureBlob")
                {
                    var settings = contentStorageConfig.Settings is { } existing
                        ? new Dictionary<string, string>(existing)
                        : new Dictionary<string, string>();
                    if (!settings.ContainsKey("ContainerName"))
                        settings["ContainerName"] = "content";
                    if (!settings.ContainsKey("ClientName"))
                        settings["ClientName"] = contentStorageConfig.Name;
                    contentStorageConfig = contentStorageConfig with { Settings = settings };
                }
            }

            // Use partitioned persistence for FileSystem to support per-org partitions
            var usePartitioned = string.Equals(graphStorageConfig.Type, "FileSystem", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(graphStorageConfig.BasePath);

            // Deploy-time feature flags (symmetric with ConfigureMemexServices).
            var features = configuration
                .GetSection(MemexFeatureOptions.SectionName)
                .Get<MemexFeatureOptions>() ?? new MemexFeatureOptions();

            // Static-repo → DB sync: partitions to materialize into + serve from the DB. For a
            // synced partition the read-only in-memory static provider is skipped (PG serves it)
            // and the import runs on boot. Empty (default) = in-memory serving everywhere, no
            // import — no regression. Default Helm sets ["Doc","Agent","Provider","Harness","Skill"].
            var syncPartitions = features.StaticRepoSync.Partitions
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // AI content is served as a UNIT: if the config names ANY AI partition, serve them ALL
            // (Agent/Provider/Harness/Skill), so an incomplete list can't leave Skill (or a future AI
            // content type) in-memory while the rest go to the DB — and AddAI's per-type serve-from-DB
            // gating stays consistent with the static-repo import. See MeshWeaver.AI/AiContentSources.
            if (syncPartitions.Overlaps(AiContentSources.ContentPartitions))
                syncPartitions.UnionWith(AiContentSources.ContentPartitions);
            IReadOnlySet<string> serveFromPartition = syncPartitions;

            MeshBuilder mb = builder
                // Configure persistence from Graph:Storage section.
                // Skip if any IPartitionStorageProvider was already registered upstream
                // (e.g., AddPartitionedPostgreSqlPersistence in Memex.Portal.Distributed/Program.cs).
                .ConfigureServices(services =>
                {
                    if (services.Any(sd => sd.ServiceType == typeof(IPartitionStorageProvider)))
                        return services;

                    return usePartitioned
                        ? services.AddPartitionedFileSystemPersistence(graphStorageConfig.BasePath!)
                        : services.AddPersistence(graphStorageConfig);
                })
                // Enable Row-Level Security for access control
                .AddRowLevelSecurity()
                // Configure graph from the same base path
                .AddGraph()
                // Plugin catalog: registers the Package/PluginCatalog content types + (below) the
                // platform-admin "Plugin Catalog" settings tab — NOT a browsable Plugins Space. This
                // instance ALSO acts as the registry: /api/plugins serves its configured source
                // (PluginCatalog:SourceRepoPath — the plugins repo) to other installations, and the
                // admin tab installs from PluginCatalog:RegistryUrl. The options carry the consumer's
                // registry URL/ref (empty RegistryUrl -> the tab shows a "not configured" note).
                .AddPluginCatalog()
                // Red-log ticketing rides the MeshWeaver.Observability MODULE
                // (ObservabilityProviderAttribute → AddLogWatch(); LogWatchOptions binds through
                // the options pipeline). The DETECTOR is not here either way — it is a separate
                // service in the cluster's monitoring namespace that polls Loki and POSTs to
                // /api/log-incidents (Doc/Architecture/LogWatchTriage.md); the compiled endpoint
                // resolves the ILogIncidentIngest Contract seam optionally.
                // Bind the whole section so the multi-registry list (PluginCatalog:Registries:N:*)
                // binds alongside the legacy single RegistryUrl/RegistryRef pair.
                .ConfigureServices(pcs => pcs.AddSingleton(
                    configuration.GetSection(PluginCatalogOptions.SectionName).Get<PluginCatalogOptions>()
                    ?? new PluginCatalogOptions()))
                // Register GitHub-sync content types (GitHubCredential / GitHubSyncConfig)
                // on the mesh + per-node hubs so their config nodes (de)serialize.
                .AddGitHubSyncTypes()
                // Register the instance-sync content type ({space}/_Sync/{sourceId} config
                // nodes) on the mesh + per-node hubs so they (de)serialize.
                .AddInstanceSyncTypes()
                // Register the OAuthCode NodeType + AuthorizationCode content type so the
                // MCP OAuth server (OAuthCodeStore) can persist pending authorization codes
                // as Admin/OAuthCode/{hashPrefix} mesh nodes — the replica-safe store every
                // pod shares (the /token exchange may land on a different replica than the
                // /authorize that minted the code). Without this the create fails with
                // "NodeType 'OAuthCode' is not registered" and no MCP client can connect.
                .AddOAuthCodeType()
                // Seed root-scope Admin AccessAssignments for users listed under
                // `Auth:GlobalAdmins` so configured admins bypass per-partition
                // RLS for cross-partition operations (list Spaces, create
                // a new Space, etc.). Empty / missing section = no-op.
                .AddMeshNodes(Authentication.GlobalAdminSeed.Build(configuration))
                // The platform's settings-tab menu entries (What's New / About / Privacy and the
                // admin tabs Invitations / Inbox / Updates / Published / Token Usage) as seeded
                // UiContribution nodes — the WS7 lane a plugin's own settings tab arrives through.
                .AddPlatformSettingsTabContributions()
                // The AI menu's navigation entries (Threads/Models/Tiers/Providers/Agents/Skills)
                // as seeded UiContribution nodes — same lane a plugin's AI-menu entry (or a whole
                // TopBar-declared menu) arrives through. Only the imperative "New thread" stays
                // compiled (AiMenuItems).
                .AddAiMenuContributions()
                .AddSpaceType()
                // Generic webhook inbox: the WebhookEvent node type behind
                // POST /api/hooks/{target} (allowlisted via WebhookInbox:Targets).
                .AddWebhookInbox()
                // Courses are fully node-native: the Edu pack owns the types (Edu/Lesson,
                // Edu/Module, Edu/Exercise, Edu/Quiz, Edu/CourseInvite, Edu/CourseCatalog) AND
                // the whole-course navigation (EduCourseNavigationProvider, registered per-hub
                // by the type configuration lambdas). The compiled MeshWeaver.Courses types had
                // zero instances in any repo or reachable mesh and are deleted.
                .AddPortalType()
                .AddAI(serveFromPartition);

            // The gRPC mesh transport is a MODULE (MeshWeaver.Hosting.Grpc.dll under
            // Modules:Assemblies — GrpcMeshModuleAttribute folds AddGrpcHub over this builder:
            // the transport services + the py/node stream-routed participant address types; its
            // GrpcModuleAttribute maps the meshweaver.v1.Mesh endpoint via
            // MapMeshModuleEndpoints). 🚨 DEFAULT-ON in every deployment: the endpoint is the
            // React GUI's browser data plane (grpc-web Connect+Deliver at the origin root), not
            // just the foreign-participant (py/*, node/*) transport — delist only where there is
            // no React GUI and no foreign participant. The former Features:Grpc flag is gone;
            // the module listing IS the switch. Only the pipeline-order-bound gRPC-web
            // middleware stays compiled (UseMeshWeaverGrpcWebWhenInstalled, below).

            // Each AI provider self-registers everything (catalog source +
            // IOptions binding + IChatClientFactory) via one builder extension.
            // The Models settings tab + the ModelProviderService read these out
            // of the live LanguageModelCatalogOptions — no central registry.
            // Gated by deploy-time feature flags (symmetric with the services-tier
            // AddCopilot/AddClaudeCode in ConfigureMemexServices). A disabled flag
            // drops the catalog source → the provider vanishes from the model
            // picker and its Model/<id> nodes never seed.
            // Language-model providers + CLI harnesses register via boot-loaded module packs
            // (Modules:Assemblies -> each pack's MeshNodeProviderAttribute). The composition root
            // carries NO provider type references any more; a deployment picks providers by
            // editing its module list. Features:Ai flags remain only for the portal-side blocks
            // that co-host CLI processes (Connect, skills sync).

            // Content → vector index is a MODULE now (MeshWeaver.ContentCollections.Indexing.
            // PostgreSql in Modules:Assemblies — PostgresContentIndexingModuleAttribute). Its
            // activation is decided at RESOLVE time on the same conditions this block used to
            // check at compose time (mesh Postgres connection + Embedding:Endpoint/ApiKey + a
            // registered IEmbeddingProvider); unconfigured deployments stay inert exactly as
            // before. The image describer rides the AI package (AddAgentChatServices TryAdds the
            // optional IImageDescriber off the default multimodal model).

            return (TBuilder)mb
                .AddSelfRegistry()
                .AddDocumentation(serveFromPartition)
                .AddStaticRepoSync(serveFromPartition, features.StaticRepoSync.Modes)
                // Ship compiled releases WHEREVER we ship code NodeTypes — Doc AND the sample
                // partitions (ACME, FutuRe, Northwind, Cornerstone, MeshWeaver). Pre-build every
                // shipped code NodeType's release at boot, as System, so the runtime path is a
                // cache hit and no user navigation ever triggers an on-demand compile (the prod
                // 2026-06-18 phantom _Activity/compile-* storm). Idempotent (skips already-built
                // types); off the thread pool so it never blocks startup.
                .ConfigureServices(services =>
                    services.AddHostedService<ShippedReleaseSeedHostedService>())
                // Markdown export (PDF/DOCX/HTML + share-by-email) rides the
                // MeshWeaver.Markdown.Export MODULE (MarkdownExportProviderAttribute →
                // AddMarkdownExport(); node seeding is IfAbsent so the lane switch is idempotent).
                // Azure Blob support (the stream-provider factory, the blob assembly cache, the
                // blob NuGet cache) RELOCATED to the MeshWeaver.Azure.Blob MODULE — its assembly
                // attribute registers the stream-provider factory when landed, and the
                // Azure-backend branches reach the store types by probe-and-delegate. Nothing to
                // register here: a filesystem deployment carries no Azure SDK at all now.
                // Shared NodeType assembly cache (versioned, cross-replica consistent): the
                // TryAdd below yields to the Distributed app's filesystem store on the
                // self-host branch, exactly as the compiled-in registration always did.
                .ConfigureServices(services =>
                {
                    Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
                        .TryAddSingleton<MeshWeaver.Mesh.Services.IAssemblyStore>(services, sp =>
                    {
                        var type = Type.GetType(
                            "MeshWeaver.Azure.Blob.BlobAssemblyStore, MeshWeaver.Azure.Blob",
                            throwOnError: false)
                            ?? throw new InvalidOperationException(
                                "No IAssemblyStore is registered and the MeshWeaver.Azure.Blob "
                                + "module is not landed — register AddFileSystemAssemblyStore "
                                + "(self-host) or land the AzureBlob package (Azure backend).");
                        var cacheDir = System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(), "meshweaver-assembly-cache");
                        // The client type reflects too — this project no longer references the
                        // Azure SDK; the module's assembly (already probed above) carries it.
                        var clientType = Type.GetType(
                            "Azure.Storage.Blobs.BlobServiceClient, Azure.Storage.Blobs",
                            throwOnError: true)!;
                        return (MeshWeaver.Mesh.Services.IAssemblyStore)Activator.CreateInstance(
                            type,
                            Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions
                                .GetRequiredKeyedService(sp, clientType, "nodetype-cache"),
                            "nodetype-cache",
                            cacheDir,
                            sp.GetRequiredService(
                                typeof(Microsoft.Extensions.Logging.ILogger<>).MakeGenericType(type)))!;
                    });
                    return services;
                })
                // Register the mesh catalog and its public interfaces
                .ConfigureServices(services => services.AddMeshCatalog())
                // Configure default views and content collections for each node hub
                // Each hub gets its own "content" collection pointing to a subdirectory
                .ConfigureDefaultNodeHub(config =>
                {
                    // Declared before the if-block so it's available for both the "content"
                    // collection mapping below and the "attachments" mapping further down.
                    var nodePath = config.Address.ToString();

                    // Content lives ONCE per Space (partition root), NOT on every node. A child-node
                    // path (e.g. "AgenticPension/Dokument") must not get its own content collection —
                    // it inherits the Space's via ExposeInChildren below. Mounting per-child created
                    // overlapping/orphaned collections (content/{space}/{child}/…) and node-level content
                    // refs; indexing is likewise per-Space (one content_chunks table per partition schema).
                    // Gate on the partition root: a single-segment node path (no '/').
                    if (contentStorageConfig != null && !nodePath.Contains('/'))
                    {
                        // Scope static media (SVG, PNG, JPG) to the Space's content subdirectory.
                        var contentSubdir = $"content/{nodePath}";
                        // Combine with original BasePath for FileSystem; for AzureBlob, subdirectory is the blob prefix
                        var basePath = string.IsNullOrEmpty(contentStorageConfig.BasePath)
                            ? contentSubdir
                            : Path.Combine(contentStorageConfig.BasePath, contentSubdir);
                        var nodeContentConfig = contentStorageConfig with
                        {
                            Name = "content",
                            IsEditable = true,
                            ExposeInChildren = true,
                            // isStatic: PUBLISHED on the access-controlled content route — a Space's
                            // images, thumbnails, PDFs and videos are fetched as
                            // /api/content/{node}/{file} and /api/content/{Space}/content/{file}.
                            // Publishing decides REACHABLE, never READABLE: every request is still
                            // gated on Read of the owning node (issue #587).
                            IsStatic = true,
                            BasePath = basePath,
                            Settings = contentStorageConfig.Settings is { } src
                                ? new Dictionary<string, string>(src) { ["BasePath"] = basePath }
                                : new Dictionary<string, string> { ["BasePath"] = basePath }
                        };
                        config = config.AddContentCollection(_ => nodeContentConfig);
                    }

                    // Map "attachments" to "storage" with per-node subdirectory
                    // (needed by FutuRe and other samples that store datacube.csv, etc.).
                    // isStatic: the file browser's download links are
                    // /api/content/{node}/attachments/… — access-controlled, gated on Read of the node.
                    config = config.MapContentCollection(
                        "attachments", "storage", $"attachments/{nodePath}", isStatic: true);

                    // Shared large static assets (e.g. the on-device Whisper models the native client
                    // downloads) live in a FileSystem content collection on the MeshWeaver space, backed
                    // by a read-only AKS file-share mount (StaticAssets:Path). This is the framework-native
                    // way — it gives the upload UI + get/list + content serving for free, and the native
                    // VoiceModelCatalog downloads from the content URL (…/MeshWeaver/static/Speech/…). It's
                    // a no-op when the mount isn't configured (local dev, tests).
                    var staticAssetsMount = configuration["StaticAssets:Path"];
                    if (!string.IsNullOrWhiteSpace(staticAssetsMount) && nodePath == "MeshWeaver")
                        config = config.AddContentCollection(_ => new ContentCollectionConfig
                        {
                            Name = "static",
                            SourceType = "FileSystem",
                            BasePath = staticAssetsMount,
                            Address = config.Address,
                            IsEditable = true,
                            ExposeInChildren = true,
                            // isStatic: the native client downloads these over
                            // /api/content/MeshWeaver/static/Speech/… — a real publication, still
                            // gated on Read of the MeshWeaver node.
                            IsStatic = true,
                            Settings = new Dictionary<string, string> { ["BasePath"] = staticAssetsMount },
                        });

                    return config
                        .WithHeartBeatHandler() // silently ack heartbeats on every per-node hub
                        .AddDefaultLayoutAreas()
                        // The course-shell areas (StartExercise / GoToMyCopy / CourseNav / Learn)
                        // ship as in-mesh source in the Edu plugin (Plugins#481) — no platform
                        // registration remains; only the navigation contributor below is compiled.
                        .AddThreadsLayoutArea()
                        // Scope-tabbed AI catalogs (Agents/Skills/Providers/Models) with per-tab
                        // create buttons — the AI-menu targets below point here. Registered on every
                        // per-node hub so they resolve when anchored on the type roots (/Agent/AiAgents …).
                        .AddAiCatalogLayoutAreas()
                        .AddApiTokensSettingsTab()
                        // Register your own MeshWeaver installation and get it an instance key.
                        .AddInstancesSettingsTab()
                        // Per-user "Notifications" tab: choose bell/email per notification category.
                        .AddNotificationsSettingsTab()
                        // AI menu (top bar) — replaces the retired Models + AI Settings tabs. Each entry
                        // opens mesh search grouped by namespace, so every tier (global / space / user)
                        // where the concern is defined shows as its own section. Per-item configurable
                        // (label / icon / order / tooltip / href); register more under the same AI context.
                        .AddNodeMenuItems(NodeMenuItemsExtensions.AiMenuContext, [.. AiMenuItems])
                        // (The platform-admin Instances overview — live cluster query, Grafana links,
                        // create-instance plan generator — rides the MeshWeaver.SelfUpdate.Aks module,
                        // which registers its own settings tab on the per-node hub.)
                        // The platform's global settings tabs ride the UiContribution lane (WS7):
                        // What's New / About / Privacy (slice 2) plus the Administration tabs —
                        // Invitations + Inbox (invitation-only onboarding, non-user mail), Updates
                        // (the Admin/UpdatePolicy auto-update strategy), Published to the web (the
                        // /sitemap.xml enumeration, rendered) and Token Usage (per-model _Usage
                        // analytics) — all in slice 4. Content stays compiled, exposed here as
                        // layout areas; the menu entries are seeded UiContribution nodes
                        // (AddPlatformSettingsTabContributions on the mesh builder above).
                        .AddPlatformSettingsTabAreas()
                        // GitHub Sync tab — shows only on Space nodes (self-filtered).
                        .AddGitHubSyncSettingsTab()
                        // GitHub Issues & PRs tab — browse/act on the repo's issues + pull requests.
                        .AddGitHubIssuesTab()
                        // NO Plugin Catalog tab. Browsing and provisioning packages is the STORE's
                        // job (/Store → the package card → Provision), and it is the only surface
                        // that runs the install under the System identity via SystemInstall. A
                        // second admin-only page onto the same registry duplicated that flow while
                        // bypassing the funnel it exists to enforce.
                        // Coupons tab (platform admins only) — the Store's typed coupon codes at
                        // Admin/Coupons: live list, redemption tallies, create/open.
                        .AddCouponAdminSettingsTab()
                        // Instance grants (platform admins only) — which plugins each registered
                        // MeshWeaver installation may pull. Registration is self-service; granting
                        // is not, and the grants live in the Admin partition out of the owner's reach.
                        .AddInstanceGrantAdminSettingsTab()
                        // Composition (platform admins only) — WHY this environment carries what it
                        // carries: the deployment's feature flags (Features:Flags:*, which also
                        // decide what it pre-installs) and the parameters its installed packages
                        // declare, with the exact env var to provision for any this environment does
                        // not supply. Read-only: composition arrives through the values file, and a
                        // browser edit would be reverted by the next helm upgrade.
                        .AddCompositionAdminSettingsTab()
                        // Instance Sync lives in the "Synchronizations" NODE-menu item (not a
                        // settings tab) — wired via AddInstanceSyncTypes on the mesh builder.
                        // Code workspace tab — on-disk working-tree editor (checkout/edit/commit/push).
                        .AddWorkingTreeTab()
                        // Git history tab — read-only git browser (commit log + changes + diffs) over the same working tree.
                        .AddGitHistoryTab();
                    // The Content Indexing tab rides the indexing MODULE
                    // (PostgresContentIndexingModuleAttribute's default-node-hub hook) — it
                    // appears exactly when the deployment lists the pipeline.
                })
                // SignalR mesh transport — external participants (native clients) join over a WebSocket.
                .AddSignalRHub()
                // MemexClient node type — per-installation client config under {user}/Client/{id}.
                .AddMemexClientType()
                // Platform self-update: the Admin/UpdatePolicy node + the poller that watches ACR and
                // (on Kubernetes) patches the portal+migration deployments to the newest version per
                // policy. On a non-k8s host it degrades to detect-and-notify. See ReleaseStrategy.md.
                .AddSelfUpdate()
                ;
                // (The platform-admin Instances feature — InstancesOptions plus the live cluster-query
                // service — rides the MeshWeaver.SelfUpdate.Aks module: it is AKS-specific, while the
                // self-update POLLER above stays here because self-update is how a deployment receives
                // new bits, modules included.)
        }

        /// <summary>
        /// Configures the portal with Graph views, Charts, GoogleMaps, and Radzen.
        /// </summary>
        public TBuilder ConfigureMemexPortal(IConfiguration configuration)
        {
            var features = configuration.GetSection(MemexFeatureOptions.SectionName)
                .Get<MemexFeatureOptions>() ?? new MemexFeatureOptions();
            // Without the Blazor shell there are no circuits, hence no per-circuit portal hubs —
            // the whole portal-hub configuration (view packs + AddBlazor's registry/data wiring)
            // is circuit-side and must not be built. The JS shells reach the mesh through the
            // session hubs (REST/gRPC-web), configured elsewhere.
            if (!features.Gui.Blazor)
                return (TBuilder)builder;
            return (TBuilder)builder
            .ConfigureHub(mesh => mesh
                .AddMeshTypes()
                // The optional view packs (Radzen, Analysis, GoogleMaps) are MODULES now: each
                // DLL's MeshNodeProviderAttribute registers its views + DI twin when the pack is
                // listed under Modules:Assemblies — drop a line to drop the pack (the AI-provider
                // pattern; formerly the Features:UiPacks flags). Registration order stopped being
                // load-bearing with the fallback-slot seam, so an absent pack simply leaves its
                // controls to the fallback.
                // The Graph node views (MeshWeaver.Blazor.Graph) are a MODULE now: its
                // GraphViewsViewPackModuleAttribute folds AddGraphViews() when the DLL is listed
                // under Modules:Assemblies; the bits ship via the modules/<Name> lane. No compiled
                // call here — the EntityViews shape.
                .AddChatViews()   // Register ThreadChatView
                .AddUserProfileViews() // Register UserProfilePageView
                // The entity form/edit renderers (MeshWeaver.Blazor.EntityViews) are a MODULE now:
                // the DLL's EntityViewsViewPackModuleAttribute folds AddEntityViews() when it is
                // listed under Modules:Assemblies, and Modules:Required gates a rollout that lost
                // it. No compiled call here — see the csproj note beside the removed reference.
            )
            .AddBlazor(layoutClient => layoutClient
                // 🚨 The portal hub is the per-user sub-hub that hosts the
                // Blazor circuit's chat input, autocomplete, navigation
                // tracking, etc. Without these registrations:
                //   • Chat: AppendUserMessageResponse arrives as RawJson and the
                //     original Observe() hangs forever ("Allocating agent…"
                //     spinner). Need AI types in the portal's TypeRegistry.
                //   • Activity tracking: TrackActivityRequest emits
                //     "No handler found for delivery TrackActivityRequest in
                //     portal/<userId>" on every login + navigation. Need the
                //     graph-types handler chain (which includes
                //     HandleTrackActivity) registered on the portal.
                //   • Data layer: layout areas hosted in the portal (e.g. chat
                //     view) hold remote streams that depend on workspace +
                //     EntityStore serialisation; .AddData() wires that.
                //
                // Lives here in MemexConfiguration (not in MeshWeaver.Blazor's
                // PortalApplication.DefaultPortalConfig) so the base portal
                // library doesn't take a hard dependency on MeshWeaver.AI /
                // MeshWeaver.Graph.
                .WithPortalConfiguration(c =>
                {
                    c.TypeRegistry.AddAITypes();
                    return c.AddData().WithGraphTypes();
                })
            );
        }
    }

    /// <summary>
    /// Starts the Memex portal application with the specified App component type.
    /// Pattern taken from MeshWeaver.Portal's StartPortalApplication.
    /// </summary>
    public static void StartMemexApplication<TApp>(this WebApplication app) where TApp : IComponent
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MemexConfiguration));
#pragma warning disable CA1416
        logger.LogInformation("Starting Memex portal on PID: {PID}", Environment.ProcessId);
#pragma warning restore CA1416

        // Startup capability guard: if every AI provider AND every co-hosted CLI is
        // disabled via Features:Ai, the model picker is empty unless users bring
        // their own keys. Warn (not fail) — a pure data portal is a valid config.
        var features = app.Configuration
            .GetSection(MemexFeatureOptions.SectionName)
            .Get<MemexFeatureOptions>() ?? new MemexFeatureOptions();
        if (!features.HasAnyChatCapability)
            logger.LogWarning(
                "No AI chat capability is enabled (Features:Ai has all providers and CLIs disabled). " +
                "The model picker will be empty unless users add their own provider keys via ModelProviders.");

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler(Pages.ErrorRoutes.Path, createScopeForErrors: true);
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        // Forward headers from reverse proxy (Azure Container Apps) so OIDC
        // middleware constructs redirect URIs with the correct scheme and host.
        // Always enabled: in production it reads X-Forwarded-* from the ACA proxy;
        // in local dev it's a no-op since no proxy sets those headers.
        app.UseForwardedHeaders();

        // 🚨 /healthz MUST short-circuit before the identity pipeline and before
        // any Blazor page rendering. Kubernetes probes used to hit "/" — every
        // probe request carries no cookies, so VirtualUserMiddleware minted a
        // fresh guest VUser (mesh node + per-node hub graph) AND the probe
        // forced a full server-side page prerender (layout-area sync hubs that
        // no circuit ever disposes). At readiness-probe cadence (5 s) the portal
        // accumulated 10,000+ leaked MessageHubs in ~25 minutes, the hosted-hub
        // collection lock became the hot path of every routed stream message,
        // and the instance wedged at 100% CPU — the 2026-06-12 prod outage.
        // Point ALL probes here; the endpoint answers without touching identity,
        // the mesh, or the renderer.
        app.Use((ctx, next) =>
        {
            if (ctx.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return ctx.Response.WriteAsync("ok");
            }
            return next();
        });

        // Security response headers — set once, early, for every real response (health
        // probes already short-circuited above, so they stay header-free and minimal).
        // These harden the browser surface WITHOUT changing what the app may load: the CSP
        // is deliberately permissive — it keeps 'unsafe-inline'/'unsafe-eval', blob:/data:
        // and https:/wss:, so the Blazor Server circuit, the Monaco editor (eval + blob
        // workers) and embedded https content keep working. It only adds the structural
        // directives an OWASP ZAP scan flagged as missing (a default-src fallback so every
        // fetch directive is defined, object-src 'none', base-uri 'self', frame-ancestors
        // 'self'). Tightening it further (per-response nonces, dropping 'unsafe-inline') is
        // a separate hardening pass, not this change.
        //
        // The CSP is ENFORCED. It shipped Content-Security-Policy-Report-Only first (#1988);
        // enforcing it here was validated by driving real Chrome over the live public pages
        // against that Report-Only header and observing ZERO violations, so the enforced policy
        // blocks nothing the app legitimately loads.
        app.Use((ctx, next) =>
        {
            var headers = ctx.Response.Headers;
            ctx.Response.OnStarting(() =>
            {
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Cross-Origin-Resource-Policy"] = "same-site";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Permissions-Policy"] =
                    "accelerometer=(), camera=(), geolocation=(), gyroscope=(), " +
                    "magnetometer=(), microphone=(), payment=(), usb=()";
                // SET, never defer. Something earlier in the pipeline emits a bare
                // `frame-ancestors 'self'` on HTML responses (API responses get the full
                // policy), and a ContainsKey guard here silently yielded to it — so every
                // page shipped an anti-clickjacking directive with no fetch-directive
                // fallback (ZAP 10055) while /api/* was correctly covered. This callback is
                // registered earliest, so it runs LAST and its value wins. The policy below
                // is a strict superset: it includes frame-ancestors 'self', so nothing the
                // shorter header expressed is lost.
                headers["Content-Security-Policy"] =
                        "default-src 'self'; " +
                        "base-uri 'self'; " +
                        "object-src 'none'; " +
                        "frame-ancestors 'self'; " +
                        "img-src 'self' data: blob: https:; " +
                        "media-src 'self' data: blob: https:; " +
                        "font-src 'self' data: https:; " +
                        "style-src 'self' 'unsafe-inline' https:; " +
                        "script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:; " +
                        "worker-src 'self' blob:; " +
                        "connect-src 'self' https: wss:; " +
                        "frame-src 'self' https:; " +
                        "form-action 'self' https:";
                return Task.CompletedTask;
            });
            return next();
        });

        // `@/` is a markdown-authoring / autocomplete prefix — not a URL segment.
        // Authors occasionally leak `@/` into raw HTML hrefs or users paste broken links.
        // Permanent-redirect `/@/X` → `/X` so those never 404.
        app.Use((ctx, next) =>
        {
            var path = ctx.Request.Path.Value;
            if (path != null && path.StartsWith("/@/", StringComparison.Ordinal))
            {
                var target = path.Substring(2) + ctx.Request.QueryString;
                ctx.Response.Redirect(target, permanent: true);
                return Task.CompletedTask;
            }
            return next();
        });

        // Frontend selection (Portal:Frontend / Portal:ReactAppUrl + the mw-frontend override
        // cookie): redirect interactive page navigations to the React app when the effective
        // frontend is React. Inert unless Portal:ReactAppUrl is configured. Must run before
        // static files/routing so it sees every navigation; assets/transport paths pass through.
        app.UseFrontendSelection();

        // React GUI SPA: rewrite extension-less /app paths to the SPA entry BEFORE static files,
        // so the bundle's index.html wins over Blazor's page catch-all (endpoint FALLBACKS lose
        // to page routes regardless of literal precedence — the rewrite sidesteps routing).
        app.Use((ctx, next) =>
        {
            var p = ctx.Request.Path.Value;
            if (p is not null
                && (p.Equals("/app", StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith("/app/", StringComparison.OrdinalIgnoreCase))
                && !System.IO.Path.HasExtension(p))
                ctx.Request.Path = "/app/index.html";
            return next();
        });

        app.UseRouting();

        // ── GUI shells (Features:Gui) — middleware placement is load-bearing ────────────────────
        // Both the per-browser shell switch and the next-only Blazor-prefix gate MUST sit here,
        // directly after UseRouting: this app's empirical rule (see the UserContextMiddleware
        // ordering comment below) is that middleware registered after a Map* call never sees a
        // request that call's endpoint matched, and the raw-wwwroot UseStaticFiles below would
        // likewise answer /_framework files before a later gate. Registered at the bottom of this
        // method (2026-08-24, first cut) the 404 gate never ran: the static-asset endpoint for
        // blazor.web.js executed first and 500'd in dev on the missing file.
        var guiShells = (app.Configuration
            .GetSection(MemexFeatureOptions.SectionName)
            .Get<MemexFeatureOptions>() ?? new MemexFeatureOptions()).Gui;
        if (guiShells is { Blazor: true, Next: true })
            // Both shells on one host: the per-browser switch (?gui=next|blazor + cookie).
            app.UseGuiShellSwitch(guiShells);
        if (!guiShells.Blazor)
            // NEXT-ONLY: this portal HAS no Blazor surface, so Blazor's two URL prefixes answer
            // 404 outright. Without this gate they answer worse: the static-asset manifest still
            // lists /_framework/blazor.web.js (the assemblies stay referenced), and with
            // MapRazorComponents absent its endpoint 500s in dev on the missing file.
            app.Use((http, nextMiddleware) =>
            {
                if (http.Request.Path.StartsWithSegments("/_blazor")
                    || http.Request.Path.StartsWithSegments("/_framework"))
                {
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                }
                return nextMiddleware(http);
            });

        // 🚨 The static-file middleware runs AFTER UseRouting, and that ordering is the whole
        // point: StaticFileMiddleware skips a request whose ENDPOINT has already been selected, so
        // everything in the build-time static-asset manifest is answered by MapStaticAssets below
        // — pre-compressed (brotli) off a per-encoding endpoint, fingerprinted, with
        // `Cache-Control: immutable`. Registered before routing (as it was until 2026-08-24) this
        // middleware short-circuits FIRST and serves those same files raw: measured on prod,
        // `_framework/blazor.web.js` came back 200,645 bytes with NO content-encoding and NO
        // cache-control, and `_content/MeshWeaver.Blazor/*.css` likewise — every asset at full
        // size, revalidated on every load, while the pre-compressed copies sat unused in the
        // image. The old comment here claimed the early registration was needed to serve RCL
        // `_content/*` paths; it is not — those are IN the manifest, which is exactly why
        // MeshModuleStaticAssetExtensions has to hand-roll its own encoding negotiation for the
        // modules that are NOT.
        //
        // What still needs the middleware: anything with no endpoint — the React SPA under /app,
        // and files that reach wwwroot outside the manifest. Those match no endpoint, so the
        // middleware serves them exactly as before.
        app.UseStaticFiles();

        // …and the same for modules that ship via modules/<Name>/ rather than a ProjectReference,
        // whose assets are in no build-time manifest of this host (#1724). Registered AFTER the
        // host's own UseStaticFiles so the platform copy of any shared dependency answers first —
        // the module lane never shadows a platform asset.
        app.UseMeshModuleStaticAssets();

        // gRPC-web middleware — lets browsers / React Native reach the mesh gRPC service
        // (Connect+Deliver split) without HTTP/2 bidi. Must sit between UseRouting and the
        // endpoint maps — the one gRPC piece that CANNOT ride the module's endpoint hook — so
        // this compiled line stays and self-gates on the MeshWeaver.Hosting.Grpc module being
        // listed under Modules:Assemblies (the endpoint itself maps via MapMeshModuleEndpoints).
        // Inert for non-grpc-web requests.
        app.UseMeshWeaverGrpcWebWhenInstalled();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.UseCookiePolicy();

        // User-context middleware MUST run BEFORE the terminal endpoint maps
        // (MapMeshMcp / MapMeshWeaver / MapGitHubConnect). Once a request
        // matches a terminal endpoint, no further `app.UseMiddleware<…>()`
        // registered AFTER the Map* call ever sees it. With UserContextMiddleware
        // after MapMeshMcp, MCP-Bearer requests skipped it entirely →
        // accessService.Context stayed null → PostPipeline fell through to its
        // hub-address fallback and stamped the message identity as
        // `mesh/<guid>`. SecurityService then matched accessObject="mesh/<guid>"
        // (no match) instead of accessObject="rbuergi" (Admin) → cross-partition
        // writes denied while same-partition self-rule writes still passed.
        //
        // Order: UserContext → VirtualUser → Onboarding. UserContext extracts
        // the real-user identity from OAuth claims / Bearer token first. Only
        // if AccessService.Context is still null afterwards (no auth on the
        // request) does VirtualUserMiddleware fall through to the cookie-backed
        // guest identity. Before this swap, VirtualUserMiddleware ran first
        // and bypassed VUser only on HttpContext.User.IsAuthenticated — but
        // some flows (Bearer-token resolution inside UserContext) set the
        // identity later in the pipeline, so VirtualUserMiddleware was
        // wastefully creating a guest VUser node on legitimately-authed
        // requests and the page crashed on
        // "No handler found for CreateNodeRequest in portal/anonymous"
        // when the create-request was posted to the portal hub instead of
        // the mesh hub. See VUserHelper.EnsureVUserNode for the matching
        // mesh-hub target fix.
        app.UseMiddleware<UserContextMiddleware>();
        app.UseMiddleware<VirtualUserMiddleware>();
        app.UseMiddleware<OnboardingMiddleware>();

        // SignalR mesh transport endpoint (/signalr) — external participants join the mesh.
        // Gated by Features:SignalR (on by default); routes: signalr client ⇒ portal hub ⇒ rest of mesh.
        if (features.SignalR)
            app.MapMeshWeaverSignalRHubs();

        // The gRPC mesh endpoint (meshweaver.v1.Mesh, grpc-web enabled — foreign-language
        // workers AND the React GUI) rides MapMeshModuleEndpoints below: the
        // MeshWeaver.Hosting.Grpc module's GrpcModuleAttribute maps it, AllowAnonymous by
        // explicit opt-out (the transport authenticates connections itself — Bearer token in
        // gRPC metadata / trusted loopback port).

        // The MCP endpoint (/mcp) rides MapMeshModuleEndpoints below: the MeshWeaver.Mcp module's
        // McpEndpointModuleAttribute maps it with the same RequireAuthorization("McpAuth") policy
        // this line carried. The POLICY itself stays here (AddMcpAuthentication above) — the REST
        // mirror /api/mesh/* is gated by the same one and is not part of that module.

        // REST surface that mirrors MCP — POST /api/mesh/* (1:1 with MCP tools).
        // Same Bearer auth policy as /mcp; multipart upload at /api/mesh/upload.
        app.MapMeshApi();

        // PUBLIC plugin registry — GET /api/plugins + POST /api/plugins/files. This instance is the
        // distribution point; consumers pull the catalog + packages without their own git credentials
        // (only curated packages, addressed by plugin id, are exposed; the registry's credential stays here).
        app.MapPluginRegistry();

        // NuGet v3 feed over this instance's plugins — /api/plugins/nuget/v3. Same instance-key
        // gate as the registry above (Bearer or Basic, since a NuGet client cannot send Bearer),
        // but with NO anonymous mode: it hands out compiled assemblies for paid modules.
        app.MapPluginBundles();
        // The release gate (#1754) as a readable verdict — the same answer the self-update poller
        // acts on, so CD's post-promote assertion and a manual roll consult the rule instead of
        // re-deriving it.
        app.MapReleaseGate();

        // Module endpoint contributions (design #1655): every Modules:Assemblies DLL carrying a
        // MeshEndpointProviderAttribute maps its routes here — authenticated by default, loud
        // startup failure on route collisions. Delisting a module removes its routes wholesale.
        app.MapMeshModuleEndpoints();
        // 🚨 …and SAY SO when an activated module contributed nothing because it never host-loaded
        // (#2093). MapMeshModuleEndpoints can only scan assemblies that ARE loaded, so a module the
        // activation record says is ON but whose bytes never reached this process contributes zero
        // routes — and its whole HTTP surface 404s for the pod's entire lifetime with no error
        // anywhere. That is exactly how /mcp went dark on memex.systemorph while the portal was
        // otherwise healthy and two clean restarts changed nothing. The report is the same one the
        // health check renders, so the pod log and /health never tell different stories.
        ReportUnloadedActivatedModules(app);

        // First-startup auto-registration — POST /api/instances/register. A new deployment presents
        // an admin-minted bootstrap key (mwr_) and receives its own instance key (mwi_) once;
        // PluginCatalog:DefaultGrants seeding applies. The bootstrap key in the body IS the auth.
        app.MapInstanceRegistration();

        // Short-lived credential exchange — POST /api/instances/token. A registered instance trades
        // its durable mwi_ key for a scoped, minutes-long mwa_ token, so a consumer (a build agent,
        // a disposable mesh) holds nothing long-lived. Only the durable key may mint; a token can
        // never mint its successor.
        app.MapInstanceTokenExchange();

        // Crawler plumbing — a real /robots.txt + /sitemap.xml (the Blazor catch-all otherwise
        // serves the SPA shell on both). The sitemap lists exactly the anonymous surface: every
        // top-level node passing the AnonymousGate plus store plugins' public segments.
        app.MapSeo();

        // Generic webhook inbox — POST /api/hooks/{target} stores the raw delivery as a
        // WebhookEvent node at {target}/_Inbox/{id} for allowlisted targets (WebhookInbox:Targets).
        // The consuming plugin verifies signatures itself; no integration-specific code here.
        app.MapWebhookInbox();

        // Red-log ingest — POST /api/log-incidents. The in-cluster log watcher reports one
        // fingerprinted burst per call; new fingerprints are triaged by an agent and ticketed.
        // Token-gated, and NOT mapped at all when LogWatch:IngestToken is unset.
        app.MapLogIncidents();

        // Centralized speech-to-text — POST /api/speech/transcribe (multipart audio → text),
        // behind the same Bearer policy; forwards to the Whisper container via ISpeechTranscriber.
        app.MapSpeechApi();

        app.MapMeshWeaver();

        // Frontend toggle endpoint: GET /frontend/{react|blazor|clear} sets/clears the per-user
        // override cookie and redirects — the reversible switch both shells link to.
        app.MapFrontendSelection();

        // (LinkedIn connect/publish/page-sync endpoints ride the MeshWeaver.Social module —
        // contributed through app.MapMeshModuleEndpoints() above via SocialModuleAttribute.)

        // GitHub Sync — OAuth authorization-code connect endpoints (same ordering
        // requirement: needs HttpContext.User). Stores the per-user token at
        // {userId}/_Provider/GitHub. See Doc/Architecture/GitHubSync.
        app.MapGitHubConnect();

        // (GET /assets/{Space}/{path…} — the entitlement-gated course-asset route — is
        // contributed by the MeshWeaver.Courses module through app.MapMeshModuleEndpoints()
        // above, mapped AllowAnonymous because CourseAssetGate is the guard.)

        // Instance Sync — OAuth+PKCE "connect to a remote MeshWeaver instance" endpoints
        // (/connect/instance[/callback]). Redirects to the remote's own login and stores the
        // returned mw_ token as the sync party's RemoteToken. See InstanceConnectEndpoints.
        app.MapInstanceConnect();

        // "Sign in with GitHub" — an authentication provider (distinct from the connect endpoints
        // above). Reuses the same GitHub:OAuth creds; issues the Entra-shaped cookie so a GitHub
        // login resolves to the same mesh user as an Entra login with that verified email.
        app.MapGitHubLogin();

        // GitHub webhooks — POST /webhooks/github. HMAC-verified (GitHub:Webhook:Secret), no
        // browser session, so it does NOT need HttpContext.User. Refreshes synced issue nodes
        // live. Register one webhook per repo (Issues + Issue comments) with the shared secret.
        app.MapGitHubWebhook();

        // Use HTTPS redirection only for non-MCP paths (MCP needs HTTP for Claude Code)
        app.UseWhen(
            context => !context.Request.Path.StartsWithSegments("/mcp"),
            appBuilder => appBuilder.UseHttpsRedirection()
        );
        app.MapStaticAssets();
        app.MapControllers();
        // The shell-switch and next-only middleware live directly after UseRouting above — a
        // gate registered down here never sees a request an earlier Map* already matched.
        if (guiShells.Blazor)
            app.MapRazorComponents<TApp>()
                .AddMeshViews()
                .AddInteractiveServerRenderMode();
        else
        {
            // The host serves no pages of its own — send browser navigations to the Next shell's
            // base path. In k8s the ingress usually routes /next to the Next service before this
            // fires; locally (PORTAL_ORIGIN dev / e2e) this is what makes the portal origin usable
            // in a browser at all. APIs and assets are mapped ABOVE and never hit this fallback.
            // Gated on the Next shell being ON: with BOTH shells off (a headless mesh API
            // deployment) there is nothing to redirect to, and unmatched paths 404 naturally.
            if (guiShells.Next)
            app.MapFallback("/{**path}", (HttpContext http) =>
            {
                var accept = http.Request.Headers.Accept.ToString();
                if (!HttpMethods.IsGet(http.Request.Method)
                    || !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                    return Results.NotFound();
                var path = http.Request.Path.HasValue ? http.Request.Path.Value! : "/";
                if (path.StartsWith(GuiShellSwitch.NextBasePath, StringComparison.OrdinalIgnoreCase))
                    return Results.NotFound(); // /next is the Next service's — never loop
                return Results.Redirect(GuiShellSwitch.NextBasePath + (path == "/" ? "" : path));
            });
        }

        // Deploy-timing lifecycle markers, measurable in Loki (grep "PortalReady" /
        // "PortalShutdown"). A dedicated Information-level category (the same surfaced-channel
        // pattern as MeshWeaver.Blazor.Circuit) — the general Memex.* namespace stays at Warning
        // in prod, so these lines must NOT ride on the class logger or they never reach stdout.
        var lifecycleLogger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Memex.Portal.Lifecycle");
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        var shutdownWatch = new System.Diagnostics.Stopwatch();
        lifetime.ApplicationStarted.Register(() =>
        {
            // PID 1 in the container ⇒ process start ≈ container start; TickCount64 would be
            // time-since-OS-boot, which is wrong here.
#pragma warning disable CA1416
            var elapsed = DateTime.UtcNow
                          - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            lifecycleLogger.LogInformation(
                "PortalReady in {ElapsedMs} ms (PID {PID})",
                (long)elapsed.TotalMilliseconds, Environment.ProcessId);
#pragma warning restore CA1416
        });
#pragma warning disable CA1416
        lifetime.ApplicationStopping.Register(() =>
        {
            shutdownWatch.Restart();
            // This line IS the SIGTERM timestamp in Loki — the shutdown window opens here.
            lifecycleLogger.LogInformation("PortalShutdown starting (PID {PID})", Environment.ProcessId);
        });
        lifetime.ApplicationStopped.Register(() =>
            // Registered callback, not code after Run(): the console logger still flushes here.
            // Its ABSENCE in Loki for a pod means the kubelet SIGKILLed mid-drain (grace too low).
            lifecycleLogger.LogInformation(
                "PortalShutdown complete in {ElapsedMs} ms (PID {PID})",
                shutdownWatch.ElapsedMilliseconds, Environment.ProcessId));
#pragma warning restore CA1416

        app.Run();
#pragma warning disable CA1416
        // After Run() returns the host has already stopped — this is an EXIT marker, not a start.
        logger.LogInformation("Memex portal exited (PID {PID})", Environment.ProcessId);
#pragma warning restore CA1416
    }

    /// <summary>
    /// Adds the MeshWeaver view assemblies that carry ROUTABLE pages (Blazor, Graph) to the Razor
    /// components endpoint, and excludes static-asset/infrastructure prefixes (_framework, _content,
    /// favicon.ico, auth, mcp, ...) from ApplicationPage's root catch-all endpoint so asset misses
    /// fall through to 404 instead of the HTML shell. The page templates themselves carry NO inline
    /// constraint — the Blazor Router would interpret ":nonfile" as the built-in dot-rejecting
    /// constraint and break every mesh path ending in a file extension (Document nodes).
    /// View packs (Radzen, GoogleMaps) do NOT belong here: this list is ROUTING discovery only, and
    /// packs have no @page components — their views register through the WithView seam.
    /// </summary>
    public static RazorComponentsEndpointConventionBuilder AddMeshViews(
        this RazorComponentsEndpointConventionBuilder builder)
        => builder.AddAdditionalAssemblies(
                typeof(ApplicationPage).Assembly               // MeshWeaver.Blazor (includes ApplicationPage with catch-all route)
            )
            .ExcludeStaticAssetPaths();
}

public class StylesConfiguration
{
    public string? StylesheetName { get; set; }
}
