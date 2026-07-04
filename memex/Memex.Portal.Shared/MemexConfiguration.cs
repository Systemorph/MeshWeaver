using System.IdentityModel.Tokens.Jwt;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using Memex.Portal.Shared.Email;
using Memex.Portal.Shared.SelfUpdate;
using Memex.Portal.Shared.Settings;
using Memex.Portal.Shared.Social;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MeshWeaver.AI;
using MeshWeaver.AI.AzureFoundry;
using MeshWeaver.AI.OpenAI;
using MeshWeaver.AI.ClaudeCode;
using MeshWeaver.AI.Copilot;
using MeshWeaver.Blazor.AI;
using MeshWeaver.Blazor.GoogleMaps;
using MeshWeaver.Blazor.Graph;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Hosting.Grpc;
using MeshWeaver.Blazor.Pages;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Blazor.Portal.Authentication;
using MeshWeaver.Blazor.Portal.Chat;
using MeshWeaver.Blazor.Portal.Components;
using MeshWeaver.Blazor.Portal.Layout;
using MeshWeaver.Blazor.Radzen;
using MeshWeaver.ContentCollections;
using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.ContentCollections.Indexing.Graph;
using MeshWeaver.ContentCollections.Indexing.PostgreSql;
using MeshWeaver.Courses;
using MeshWeaver.Documentation;
using MeshWeaver.GoogleMaps;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.InstanceSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Hosting.Activity;
using MeshWeaver.Hosting.AzureBlob;
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
using PortalAuthOptions = MeshWeaver.Blazor.Portal.Authentication.AuthenticationOptions;

namespace Memex.Portal.Shared;

public static class MemexConfiguration
{
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

        // Configure Radzen
        services.AddRadzenServices();

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
        if (emailOptions.Enabled)
        {
            services.AddSingleton<IEmailSender, GraphEmailSender>();
            // Executive Assistant: per-user JUST-IN-TIME delegated Graph access (the user consents to the
            // EA touching THEIR OWN mailbox/calendar only when they first use the tool — no standing app
            // permission). EaGraphAuth drives the consent/token flow; the plugin uses the per-user token.
            services.AddHttpClient<Authentication.IEaGraphAuth, Authentication.EaGraphAuth>();
            services.AddSingleton<MeshWeaver.AI.Plugins.IAgentPlugin, ExecutiveAssistantPlugin>();
            // Notification triage runner — escalates in-app notifications to email/Teams per each
            // recipient's NotificationRules, via the cheap triage agent (only fires for users with rules).
            services.AddHostedService<Memex.Portal.Shared.Notifications.NotificationTriageService>();
        }
        else
            services.AddSingleton<IEmailSender, NoOpEmailSender>();

        // Inbound email→agent channel (intake). Mail is treated as a chat device: each inbound email
        // finds-or-creates a conversation thread and appends its latest message (referencing the email
        // by path). The Graph subscription self-skips unless Email:Enabled && Email:InboundEnabled.
        services.AddSingleton<GraphMail>(sp => new GraphMail(
            sp.GetRequiredService<EmailOptions>()));
        services.AddSingleton<EmailInboundProcessor>(sp => new EmailInboundProcessor(
            sp.GetRequiredService<PortalApplication>().Hub,
            sp.GetRequiredService<GraphMail>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<EmailInboundProcessor>>()));
        services.AddHostedService<GraphSubscriptionService>();
        // Mesh-driven reply sender: drains agent-emitted Outbound Email nodes (Status=New) via Graph.
        services.AddHostedService<OutboundEmailSender>();
        // Mesh-driven invitation emailer: emails any Pending Invitation node not yet emailed
        // (EmailSentAt==null), from ANY entry point (Invitations tab, MCP, REST). Self-skips
        // unless Email:Enabled. Decouples the invite email from the UI handler.
        services.AddHostedService<Email.InvitationEmailSender>();

        // Microsoft Teams bot channel (bidirectional). Registered always but INERT unless Teams:Enabled
        // and Bot credentials are set (TeamsClient.IsConfigured gates the endpoint + sender). Activate by
        // provisioning an Azure Bot resource + Teams app and setting the Teams config.
        var teamsOptions = builder.Configuration.GetSection(MeshWeaver.Mesh.TeamsOptions.SectionName)
            .Get<MeshWeaver.Mesh.TeamsOptions>() ?? new MeshWeaver.Mesh.TeamsOptions();
        services.AddSingleton(teamsOptions);
        services.AddHttpClient<Teams.ITeamsClient, Teams.TeamsClient>();
        services.AddSingleton<Teams.TeamsInboundProcessor>(sp => new Teams.TeamsInboundProcessor(
            sp.GetRequiredService<PortalApplication>().Hub,
            sp.GetRequiredService<Teams.ITeamsClient>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<Teams.TeamsInboundProcessor>>()));
        if (teamsOptions.Enabled)
            // Delivers agent replies back into Teams, reading them via the shared
            // ThreadFlow.ObserveResponses abstraction (same read-side primitive the GUI uses).
            // Only the hosted service is feature-gated; the client + inbound processor stay registered
            // so the messaging endpoint can resolve them and return NotFound when disabled.
            services.AddHostedService<Teams.TeamsReplySender>();

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

        if (features.Ai.Clis.Copilot)
            services.AddCopilot(config =>
            {
                builder.Configuration.GetSection("Copilot").Bind(config);
                config.SkillsDirectory = skillsDir;
            });

        if (features.Ai.Clis.ClaudeCode)
            services.AddClaudeCode(config =>
            {
                builder.Configuration.GetSection("ClaudeCode").Bind(config);
                config.SkillsDirectory = skillsDir;
            });

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

        // Register WebSearch plugin (agents declare it in frontmatter; gracefully degrades without Bing API key)
        services.AddWebSearchPlugin(config =>
            builder.Configuration.GetSection("WebSearch").Bind(config));

        // Configure GoogleMaps
        services.Configure<GoogleMapsConfiguration>(builder.Configuration.GetSection("GoogleMaps"));

        services.AddHttpContextAccessor();
        services.AddHttpClient();
        services.AddSignalR();
        services.AddControllers()
                .AddApplicationPart(typeof(MemexConfiguration).Assembly);
        services.Configure<StylesConfiguration>(
            builder.Configuration.GetSection("Styles"));

        // Register API token service for MCP bearer auth and OAuth code store
        services.AddSingleton<ApiTokenService>();
        services.AddSingleton<OAuthCodeStore>();
        // Automatic, token-based MCP back-connection for the co-hosted Claude Code / Copilot CLIs.
        // The chat clients resolve this at spawn to mint/reuse the per-user MCP ApiToken + URL.
        services.AddSingleton<MeshWeaver.AI.Connect.IMcpBackConnection, McpBackConnectionService>();
        // ModelProviderService backs the Models settings tab — users store
        // their own AI provider credentials as MeshNodes in their namespace.
        services.AddSingleton<Memex.Portal.Shared.Models.ModelProviderService>();
        // ProviderModelLister fetches a provider's live model list (HTTP /models via
        // the I/O pool) so the add-provider flow lets users pick which models to bring.
        services.AddSingleton<Memex.Portal.Shared.Models.ProviderModelLister>();

        // GitHub sync — per-user OAuth credential (device flow) + bidirectional
        // Space ↔ GitHub sync (export = "sync back"; import = create / re-import a
        // Space at any commit). The OAuth client id is bound from GitHub:OAuth;
        // absent a client id the Connect flow is gracefully disabled.
        services.AddGitHubSyncServices();
        services.Configure<GitHubOAuthOptions>(builder.Configuration.GetSection("GitHub:OAuth"));

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
        if (features.Ai.Clis.Copilot)
            services.AddSingleton<MeshWeaver.AI.Connect.IConnectStrategy, MeshWeaver.AI.Copilot.CopilotConnectStrategy>();

        // Social publishing — minimal registration for the LinkedIn connect + pull endpoints.
        // (The full hosted-service pipeline is gated behind AddSocialPublishing which needs
        // IApprovalPublishBridge / IStatsRefreshSource / IPastPostIngestSource — those come
        // in Phase 4. For now the publisher is enough for /connect/linkedin/pull to work.)
        var linkedInClientId = builder.Configuration["Social:LinkedIn:ClientId"];
        if (!string.IsNullOrEmpty(linkedInClientId))
        {
            services.AddHttpClient<MeshWeaver.Social.LinkedInPublisher>();
            services.AddSingleton(new MeshWeaver.Social.LinkedInOptions
            {
                ClientId = linkedInClientId!,
                ClientSecret = builder.Configuration["Social:LinkedIn:ClientSecret"] ?? ""
            });

            // Add the menu provider so "Connect LinkedIn" + "Pull LinkedIn posts"
            // appear on the viewer's own user page.
            services.TryAddEnumerable(
                Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Scoped<
                    MeshWeaver.Mesh.INodeMenuProvider,
                    Memex.Portal.Shared.Social.LinkedInCredentialMenuProvider>());

            // (Removed: SocialMediaUserMenuProvider — hardcoded a NodeType
            // ("Systemorph/SocialMediaHub") that isn't registered anywhere in
            // the codebase. NodeTypes belong in the database (NodeTypeDefinition
            // MeshNodes), not as DLL-side string constants. The SocialMedia
            // hub feature should be added back when its NodeType is defined
            // through the regular mesh node creation flow rather than wired
            // through a DLL-time CreateNode that fails on the receiver.)
        }

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
    }

    extension<TBuilder>(TBuilder builder) where TBuilder : MeshBuilder
    {
        /// <summary>
        /// Configures the mesh with Graph domain only.
        ///
        /// Configuration is read from appsettings:
        /// - Graph:Storage:Type - Storage type: "FileSystem", "AzureBlob", "PostgreSql", or "Cosmos"
        /// - Graph:Storage:BasePath - Base path for FileSystem storage
        /// - Graph:Storage:ConnectionString - Connection string for AzureBlob/Cosmos
        /// - storage - Content collection configuration (Name, SourceType, BasePath)
        /// </summary>
        public TBuilder ConfigureMemexMesh(IConfiguration configuration, bool isDevelopment = false)
        {
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
                // Register GitHub-sync content types (GitHubCredential / GitHubSyncConfig)
                // on the mesh + per-node hubs so their config nodes (de)serialize.
                .AddGitHubSyncTypes()
                // Register the instance-sync content type ({space}/_Sync/{sourceId} config
                // nodes) on the mesh + per-node hubs so they (de)serialize.
                .AddInstanceSyncTypes()
                // Seed root-scope Admin AccessAssignments for users listed under
                // `Auth:GlobalAdmins` so configured admins bypass per-partition
                // RLS for cross-partition operations (list Spaces, create
                // a new Space, etc.). Empty / missing section = no-op.
                .AddMeshNodes(Authentication.GlobalAdminSeed.Build(configuration))
                .AddSpaceType()
                // Interactive courses: Course/Module/Exercise/ExerciseAttempt
                // node types + the stream-update validation control plane.
                .AddCourses()
                .AddPortalType()
                .AddAI(serveFromPartition);

            // gRPC mesh transport (foreign participants py/*, node/*, and the React GUI's
            // browser Connect+Deliver split). Registers the service + declares the
            // participant address types stream-routed. Symmetric with Features:SignalR.
            if (features.Grpc)
                mb = mb.AddGrpcHub();

            // Each AI provider self-registers everything (catalog source +
            // IOptions binding + IChatClientFactory) via one builder extension.
            // The Models settings tab + the ModelProviderService read these out
            // of the live LanguageModelCatalogOptions — no central registry.
            // Gated by deploy-time feature flags (symmetric with the services-tier
            // AddCopilot/AddClaudeCode in ConfigureMemexServices). A disabled flag
            // drops the catalog source → the provider vanishes from the model
            // picker and its Model/<id> nodes never seed.
            if (features.Ai.Providers.Anthropic) mb = mb.AddAnthropic();
            if (features.Ai.Providers.AzureFoundry) mb = mb.AddAzureFoundry();
            if (features.Ai.Providers.AzureOpenAI) mb = mb.AddAzureOpenAI();
            if (features.Ai.Providers.OpenAI) mb = mb.AddOpenAI();
            if (features.Ai.Providers.OpenAICompatible) mb = mb.AddOpenAICompatible();
            if (features.Ai.Providers.OpenRouter) mb = mb.AddOpenRouter();
            if (features.Ai.Clis.ClaudeCode) mb = mb.AddClaudeCode();   // catalog source (factory + config via services.AddClaudeCode)
            if (features.Ai.Clis.Copilot) mb = mb.AddCopilot();         // catalog source (factory + config via services.AddCopilot)

            // Content → vector index (core tech). When embeddings are configured, wire the
            // upload→Activity indexing pipeline (extract→chunk→embed→store), per-file Document nodes
            // (extractive summary by default — swap in a chat client for AI summaries), and chunk-search
            // @-autocomplete. The vector store lives IN THE MESH DATABASE, in each partition's OWN schema
            // (content_chunks/content_files alongside that partition's mesh_nodes) — no separate database.
            // Inert when there's no mesh Postgres connection (e.g. the FileSystem monolith) or embeddings
            // aren't set: it compiles in but never activates.
            var meshConnectionString = configuration.GetConnectionString("memex");
            var embeddingsConfigured = !string.IsNullOrWhiteSpace(configuration["Embedding:Endpoint"])
                && !string.IsNullOrWhiteSpace(configuration["Embedding:ApiKey"]);
            if (!string.IsNullOrWhiteSpace(meshConnectionString) && embeddingsConfigured)
            {
                mb = mb
                    .AddContentIndexingPipeline(
                        storeFactory: sp => new PostgreSqlChunkedContentVectorStore(
                            meshConnectionString,
                            sp.GetService<IoPoolRegistry>(),
                            sp.GetRequiredService<IEmbeddingProvider>().Dimensions),
                        embedderFactory: sp => new EmbeddingProviderChunkEmbedder(
                            sp.GetRequiredService<IEmbeddingProvider>(),
                            sp.GetService<IoPoolRegistry>()),
                        summarizerFactory: _ => new ExtractiveSummarizer())
                    .AddContentSearch();
            }

            return (TBuilder)mb
                .AddSelfRegistry()
                .AddDocumentation(serveFromPartition)
                .AddStaticRepoSync(serveFromPartition)
                // Ship compiled releases WHEREVER we ship code NodeTypes — Doc AND the sample
                // partitions (ACME, FutuRe, Northwind, Cornerstone, MeshWeaver). Pre-build every
                // shipped code NodeType's release at boot, as System, so the runtime path is a
                // cache hit and no user navigation ever triggers an on-demand compile (the atioz
                // 2026-06-18 phantom _Activity/compile-* storm). Idempotent (skips already-built
                // types); off the thread pool so it never blocks startup.
                .ConfigureServices(services =>
                    services.AddHostedService<ShippedReleaseSeedHostedService>())
                .AddMarkdownExport()
                // Register Azure Blob support for content collections.
                .ConfigureServices(services => services.AddAzureBlob())
                // Shared NodeType assembly cache (versioned, cross-replica consistent).
                // Requires `AddKeyedAzureBlobServiceClient("nodetype-cache")` to have
                // registered a keyed BlobServiceClient — Aspire wires this via the
                // `nodetype-cache` container reference on the portal resource.
                .ConfigureServices(services => services.AddBlobAssemblyStore())
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
                            BasePath = basePath,
                            Settings = contentStorageConfig.Settings is { } src
                                ? new Dictionary<string, string>(src) { ["BasePath"] = basePath }
                                : new Dictionary<string, string> { ["BasePath"] = basePath }
                        };
                        config = config.AddContentCollection(_ => nodeContentConfig);
                    }

                    // Map "attachments" to "storage" with per-node subdirectory
                    // (needed by FutuRe and other samples that store datacube.csv, etc.)
                    config = config.MapContentCollection("attachments", "storage", $"attachments/{nodePath}");

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
                            Settings = new Dictionary<string, string> { ["BasePath"] = staticAssetsMount },
                        });

                    return config
                        .WithHeartBeatHandler() // silently ack heartbeats on every per-node hub
                        .AddDefaultLayoutAreas()
                        .AddThreadsLayoutArea()
                        .AddApiTokensSettingsTab()
                        // AI menu (top bar) — replaces the retired Models + AI Settings tabs. Each entry
                        // opens mesh search grouped by namespace, so every tier (global / space / user)
                        // where the concern is defined shows as its own section. Per-item configurable
                        // (label / icon / order / tooltip / href); register more under the same AI context.
                        .AddNodeMenuItems(NodeMenuItemsExtensions.AiMenuContext,
                            // "New thread" — opens the chat side panel ready for a brand-new conversation.
                            // The Area is a sentinel handled imperatively in PortalLayoutBase.HandleMenuItemClick
                            // (no Href → no navigation): it opens the panel + signals new-thread mode.
                            new NodeMenuItemDefinition("New thread", PortalLayoutBase.AiNewThreadAction,
                                Icon: "/static/NodeTypeIcons/chat.svg", Order: 0,
                                Tooltip: "Start a new conversation in the chat panel"),
                            new NodeMenuItemDefinition("Threads", "AiThreads", Icon: "/static/NodeTypeIcons/chat.svg", Order: 10,
                                Href: "/search?q=nodeType%3AThread&groupBy=Namespace",
                                Tooltip: "Conversation threads across every namespace"),
                            new NodeMenuItemDefinition("Models", "AiModels", Icon: "/static/NodeTypeIcons/sparkle.svg", Order: 20,
                                Href: "/search?q=nodeType%3ALanguageModel&groupBy=Namespace",
                                Tooltip: "Language models, grouped by provider"),
                            new NodeMenuItemDefinition("Agents", "AiAgents", Icon: "/static/NodeTypeIcons/bot.svg", Order: 30,
                                Href: "/search?q=nodeType%3AAgent&groupBy=Namespace",
                                Tooltip: "AI agents — global, space, and user"),
                            new NodeMenuItemDefinition("Skills", "AiSkills", Icon: "/static/NodeTypeIcons/rocket.svg", Order: 40,
                                Href: "/search?q=nodeType%3ASkill&groupBy=Namespace",
                                Tooltip: "Reusable skills"),
                            new NodeMenuItemDefinition("Providers", "AiProviders", Icon: "/static/NodeTypeIcons/key.svg", Order: 25,
                                Href: "/search?q=nodeType%3AModelProvider&groupBy=Namespace",
                                Tooltip: "AI providers — endpoints + keys"))
                        // Dedicated Admin menu (platform-wide GlobalSettings area), gated on root
                        // Permission.All: Invitations + Inbox.
                        .AddInvitationsSettingsTab()
                        .AddInboxSettingsTab()
                        // Platform auto-update strategy (Admin/UpdatePolicy) — stable/continuous/none.
                        .AddUpdatePolicySettingsTab()
                        // Token-usage analytics (per-model _Usage satellites): filter by period,
                        // group by model / person / thread, cost from ModelPricing.
                        .AddTokenUsageSettingsTab()
                        // GitHub Sync tab — shows only on Space nodes (self-filtered).
                        .AddGitHubSyncSettingsTab()
                        // Instance Sync tab — replicate the Space to another MeshWeaver
                        // instance (self-filtered to Spaces, like the GitHub Sync tab).
                        .AddInstanceSyncSettingsTab()
                        // Code workspace tab — on-disk working-tree editor (checkout/edit/commit/push).
                        .AddWorkingTreeTab()
                        // Git history tab — read-only git browser (commit log + changes + diffs) over the same working tree.
                        .AddGitHistoryTab()
                        // Content Indexing tab — Space nodes, only when the indexing pipeline is active.
                        .AddContentIndexSettingsTab();
                })
                // Add activity tracking to record user access patterns via ActivityLogBundler
                .AddActivityTracking()
                // SignalR mesh transport — external participants (native clients) join over a WebSocket.
                .AddSignalRHub()
                // MemexClient node type — per-installation client config under {user}/Client/{id}.
                .AddMemexClientType()
                // Platform self-update: the Admin/UpdatePolicy node + the poller that watches ACR and
                // (on Kubernetes) patches the portal+migration deployments to the newest version per
                // policy. On a non-k8s host it degrades to detect-and-notify. See ReleaseStrategy.md.
                .AddSelfUpdate();
        }

        /// <summary>
        /// Configures the portal with Graph views, Charts, GoogleMaps, and Radzen.
        /// </summary>
        public TBuilder ConfigureMemexPortal() => (TBuilder)builder
            .ConfigureHub(mesh => mesh
                .AddMeshTypes()
                .AddRadzenDataGrid()
                .AddRadzenCharts()
                .AddGoogleMaps()
                .AddGraphViews()  // Also enables @ autocomplete in markdown editors
                .AddChatViews()   // Register ThreadChatView
                .AddUserProfileViews() // Register UserProfilePageView
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
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
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
        // and the instance wedged at 100% CPU — the 2026-06-12 atioz outage.
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

        // Static files middleware must run before routing to serve _content/* paths from RCLs
        app.UseStaticFiles();

        app.UseRouting();

        // gRPC-web middleware — lets browsers / React Native reach the mesh gRPC service
        // (Connect+Deliver split) without HTTP/2 bidi. Must sit between UseRouting and the
        // endpoint maps. Inert for non-grpc-web requests.
        if (features.Grpc)
            app.UseMeshWeaverGrpcWeb();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.UseCookiePolicy();

        // User-context middleware MUST run BEFORE the terminal endpoint maps
        // (MapMeshMcp / MapMeshWeaver / MapLinkedInConnect). Once a request
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

        // gRPC mesh endpoint (meshweaver.v1.Mesh/Open, grpc-web enabled) — foreign-language
        // workers and the React GUI connect here.
        if (features.Grpc)
            app.MapMeshWeaverGrpc();

        // Map MCP endpoint
        app.MapMeshMcp();

        // REST surface that mirrors MCP — POST /api/mesh/* (1:1 with MCP tools).
        // Same Bearer auth policy as /mcp; multipart upload at /api/mesh/upload.
        app.MapMeshApi();

        app.MapMeshWeaver();

        // Frontend toggle endpoint: GET /frontend/{react|blazor|clear} sets/clears the per-user
        // override cookie and redirects — the reversible switch both shells link to.
        app.MapFrontendSelection();


        // Social publishing — LinkedIn connect/pull endpoints. Must be AFTER
        // UseAuthentication so HttpContext.User is populated.
        app.MapLinkedInConnect();

        // GitHub Sync — OAuth authorization-code connect endpoints (same ordering
        // requirement: needs HttpContext.User). Stores the per-user token at
        // {userId}/_Provider/GitHub. See Doc/Architecture/GitHubSync.
        app.MapGitHubConnect();

        // Use HTTPS redirection only for non-MCP paths (MCP needs HTTP for Claude Code)
        app.UseWhen(
            context => !context.Request.Path.StartsWithSegments("/mcp"),
            appBuilder => appBuilder.UseHttpsRedirection()
        );
        app.MapStaticAssets();
        app.MapControllers();
        app.MapRazorComponents<TApp>()
            .AddMeshViews()
            .AddInteractiveServerRenderMode();

        app.Run();
#pragma warning disable CA1416
        logger.LogInformation("Started Memex portal on PID: {PID}", Environment.ProcessId);
#pragma warning restore CA1416
    }

    /// <summary>
    /// Adds all MeshWeaver view assemblies (Blazor, Graph, Radzen, GoogleMaps) to the Razor components endpoint,
    /// and excludes static-asset/infrastructure prefixes (_framework, _content, favicon.ico, auth, mcp, ...)
    /// from ApplicationPage's root catch-all endpoint so asset misses fall through to 404 instead of the
    /// HTML shell. The page templates themselves carry NO inline constraint — the Blazor Router would
    /// interpret ":nonfile" as the built-in dot-rejecting constraint and break every mesh path ending
    /// in a file extension (Document nodes).
    /// </summary>
    public static RazorComponentsEndpointConventionBuilder AddMeshViews(
        this RazorComponentsEndpointConventionBuilder builder)
        => builder.AddAdditionalAssemblies(
                typeof(ApplicationPage).Assembly,              // MeshWeaver.Blazor (includes ApplicationPage with catch-all route)
                typeof(MeshNodeEditorView).Assembly,           // MeshWeaver.Blazor.Graph
                typeof(RadzenChartView).Assembly,              // MeshWeaver.Blazor.Radzen
                typeof(GoogleMapView).Assembly                 // MeshWeaver.Blazor.GoogleMaps
            )
            .ExcludeStaticAssetPaths();
}

public class StylesConfiguration
{
    public string? StylesheetName { get; set; }
}
