using System.IdentityModel.Tokens.Jwt;
using MeshWeaver.AI;
using MeshWeaver.AI.AzureFoundry;
using MeshWeaver.Blazor.AI;
using MeshWeaver.AI.AzureOpenAI;
using MeshWeaver.AI.ClaudeCode;
using MeshWeaver.AI.Copilot;
using MeshWeaver.AI.Layout;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Blazor.Portal.Chat;
using MeshWeaver.Blazor.GoogleMaps;
using MeshWeaver.Blazor.Graph;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Blazor.Pages;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Blazor.Portal.Authentication;
using MeshWeaver.Blazor.Radzen;
using Memex.Portal.Shared.Admin;
using Memex.Portal.Shared.Authentication;
using PortalAuthOptions = MeshWeaver.Blazor.Portal.Authentication.AuthenticationOptions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Documentation;
using MeshWeaver.GoogleMaps;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Activity;
using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Hosting.Blazor;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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

        services.AddRazorPages();

        services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddHubOptions(opt =>
            {
                opt.DisableImplicitFromServicesParameters = true;
            })
            .AddBlazorPortalServices();

        // Configure Radzen
        services.AddRadzenServices();

        // Configure AI services
        services.AddMemoryChatPersistence();

        // Configure AI factories (read from appsettings, including Order)
        services.AddAzureFoundryClaude(config =>
            builder.Configuration.GetSection("Anthropic").Bind(config));

        services.AddAzureFoundry(config =>
            builder.Configuration.GetSection("AzureAIS").Bind(config));

        services.AddAzureOpenAI(config =>
            builder.Configuration.GetSection("AzureOpenAIS").Bind(config));

        services.AddCopilot(config =>
            builder.Configuration.GetSection("Copilot").Bind(config));

        services.AddClaudeCode(config =>
            builder.Configuration.GetSection("ClaudeCode").Bind(config));

        // Register the AI chat services (must be after all factory registrations)
        services.AddAgentChatServices();

        // Configure GoogleMaps
        services.Configure<GoogleMapsConfiguration>(builder.Configuration.GetSection("GoogleMaps"));

        services.AddHttpContextAccessor();
        services.AddHttpClient();
        services.AddSignalR();
        services.AddControllers();

        services.Configure<StylesConfiguration>(
            builder.Configuration.GetSection("Styles"));

        // Register API token service for MCP bearer auth
        services.AddSingleton<ApiTokenService>();

        // Configure authentication
        var authSection = builder.Configuration.GetSection(PortalAuthOptions.SectionName);
        var entraIdConfig = builder.Configuration.GetSection("EntraId");

        // Determine provider: explicit config > EntraId presence > Dev default
        var provider = authSection["Provider"]
            ?? (entraIdConfig.GetChildren().Any() ? AuthenticationProviders.MicrosoftIdentity : AuthenticationProviders.Dev);

        // Bind providers list from configuration (fallback)
        var externalProviders = authSection.GetSection("Providers").Get<List<ExternalProviderConfig>>()
                                ?? new List<ExternalProviderConfig>();

        // Dev login is enabled explicitly or when provider is Dev (backward compat)
        var enableDevLogin = authSection.GetValue<bool?>("EnableDevLogin")
                             ?? (provider == AuthenticationProviders.Dev);

        // Try to read auth providers from Admin nodes (graph storage)
        // This overrides appsettings-based provider config when Admin nodes exist
#pragma warning disable CA1416
        var startupLogger = LoggerFactory.Create(lb => lb.AddConsole()).CreateLogger("AdminStartup");
#pragma warning restore CA1416
        var adminAuthProviders = AdminStartupReader.ReadAuthProviders(builder.Configuration, startupLogger);
        if (adminAuthProviders != null)
        {
            enableDevLogin = adminAuthProviders.EnableDevLogin;

            // Resolve KeyVault secrets into ExternalProviderConfig list
            var keyVaultUri = builder.Configuration["KeyVault:Uri"];
            externalProviders = AdminStartupReader.ResolveProviders(adminAuthProviders, keyVaultUri, startupLogger);

            if (externalProviders.Count > 0)
                provider = AuthenticationProviders.Custom; // Force unified cookie-based auth
        }

        // Register authentication navigation service
        services.AddAuthenticationNavigation(options =>
        {
            options.Provider = provider;
            options.Providers = externalProviders;
            options.EnableDevLogin = enableDevLogin;

            // Allow custom paths from config
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

        if (provider == AuthenticationProviders.MicrosoftIdentity && externalProviders.Count == 0)
        {
            // Legacy single-provider MicrosoftIdentity mode (OIDC via EntraId section)
            JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
            var legacyAuthBuilder = services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApp(entraIdConfig);
            // Add API token auth scheme (must use the underlying services, not the MSAL builder)
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
                    ApiTokenAuthenticationHandler.SchemeName, _ => { });
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
                options.LogoutPath = externalProviders.Count > 0 ? "/auth/logout" : "/dev/logout";
                options.ExpireTimeSpan = TimeSpan.FromDays(14);
                options.SlidingExpiration = true;
                options.Cookie.Name = "MemexAuth";
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
            });

            // Register each configured external provider
            foreach (var ep in externalProviders)
            {
                switch (ep.Name.ToLowerInvariant())
                {
                    case "microsoft":
                        JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
                        authBuilder.AddMicrosoftIdentityWebApp(options =>
                        {
                            options.ClientId = ep.ClientId;
                            options.ClientSecret = ep.ClientSecret;
                            options.TenantId = ep.TenantId ?? "common";
                            options.Instance = "https://login.microsoftonline.com/";
                            options.CallbackPath = "/signin-microsoft";
                        }, cookieOptions => { }, "Microsoft");
                        services.AddControllersWithViews().AddMicrosoftIdentityUI();
                        break;

                    case "google":
                        authBuilder.AddOAuth("Google", options =>
                        {
                            options.ClientId = ep.ClientId;
                            options.ClientSecret = ep.ClientSecret;
                            options.CallbackPath = "/signin-google";
                            options.AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
                            options.TokenEndpoint = "https://oauth2.googleapis.com/token";
                            options.UserInformationEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo";
                            options.Scope.Add("openid");
                            options.Scope.Add("profile");
                            options.Scope.Add("email");
                            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.NameIdentifier, "sub");
                            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Name, "name");
                            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Email, "email");
                        });
                        break;

                    case "linkedin":
                        authBuilder.AddOAuth("LinkedIn", options =>
                        {
                            options.ClientId = ep.ClientId;
                            options.ClientSecret = ep.ClientSecret;
                            options.CallbackPath = "/signin-linkedin";
                            options.AuthorizationEndpoint = "https://www.linkedin.com/oauth/v2/authorization";
                            options.TokenEndpoint = "https://www.linkedin.com/oauth/v2/accessToken";
                            options.UserInformationEndpoint = "https://api.linkedin.com/v2/userinfo";
                            options.Scope.Add("openid");
                            options.Scope.Add("profile");
                            options.Scope.Add("email");
                            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.NameIdentifier, "sub");
                            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Name, "name");
                            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Email, "email");
                        });
                        break;

                    case "apple":
                        authBuilder.AddOAuth("Apple", options =>
                        {
                            options.ClientId = ep.ClientId;
                            options.ClientSecret = ep.ClientSecret;
                            options.CallbackPath = "/signin-apple";
                            options.AuthorizationEndpoint = "https://appleid.apple.com/auth/authorize";
                            options.TokenEndpoint = "https://appleid.apple.com/auth/token";
                            options.Scope.Add("name");
                            options.Scope.Add("email");
                            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.NameIdentifier, "sub");
                            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Name, "name");
                            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Email, "email");
                        });
                        break;
                }
            }

            // Add API token auth scheme for MCP bearer authentication
            authBuilder.AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
                ApiTokenAuthenticationHandler.SchemeName, _ => { });
        }

        // Add authorization with McpAuth policy (ApiToken scheme only — no cookie redirects for API clients)
        services.AddAuthorization(options =>
        {
            options.AddPolicy("McpAuth", policy =>
            {
                policy.AddAuthenticationSchemes(ApiTokenAuthenticationHandler.SchemeName);
                policy.RequireAuthenticatedUser();
            });
        });
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
                    var settings = contentStorageConfig.Settings ?? new Dictionary<string, string>();
                    if (!settings.ContainsKey("ContainerName"))
                        settings["ContainerName"] = "content";
                    if (!settings.ContainsKey("ClientName"))
                        settings["ClientName"] = contentStorageConfig.Name;
                    contentStorageConfig = contentStorageConfig with { Settings = settings };
                }
            }

            return (TBuilder)builder
                // Configure persistence from Graph:Storage section
                .ConfigureServices(services => services.AddPersistence(graphStorageConfig))
                // Enable Row-Level Security for access control
                .AddRowLevelSecurity()
                // Configure graph from the same base path
                .AddGraph()
                .AddDocumentation()
                .AddPlatformType()
                // Register Admin namespace content types for polymorphic deserialization
                .ConfigureServices(services =>
                {
                    var typeRegistry = services.BuildServiceProvider().GetService<ITypeRegistry>();
                    if (typeRegistry != null)
                    {
                        typeRegistry.WithType(typeof(InitializationContent), nameof(InitializationContent));
                        typeRegistry.WithType(typeof(AuthProviderSettings), nameof(AuthProviderSettings));
                        typeRegistry.WithType(typeof(AuthProviderEntry), nameof(AuthProviderEntry));
                        typeRegistry.WithType(typeof(AdminSettings), nameof(AdminSettings));
                    }
                    return services;
                })
                // Add kernel for interactive markdown code execution
                .AddKernel()
                // Register Azure Blob support for content collections.
                .ConfigureServices(services => services.AddAzureBlob())
                // Register the mesh catalog
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IMeshCatalog, MeshCatalog>();
                    return services;
                })
                // Add content collections at mesh level with storage config
                // The storage collection is registered as a source for node hub mappings
                .ConfigureHub(hub =>
                {
                    if (contentStorageConfig == null)
                        return hub;
                    // Storage collection is not editable (managed by the system)
                    contentStorageConfig = contentStorageConfig with { IsEditable = false };
                    return hub.AddContentCollection(_ => contentStorageConfig);
                })
                // Configure default views and content collections for each node hub
                // Order matters: AddContentCollections registers $Content area first,
                // then AddDefaultLayoutAreas sets DetailsArea as default (can be overridden by node type config)
                .ConfigureDefaultNodeHub(config =>
                {
                    if (contentStorageConfig != null)
                    {
                        var nodePath = config.Address.ToString();
                        config = config
                            .MapContentCollection("content", contentStorageConfig.Name, $"content/{nodePath}")
                            .MapContentCollection("attachments", contentStorageConfig.Name, $"attachments/{nodePath}");
                    }

                    return config.AddDefaultLayoutAreas().AddThreadsLayoutArea();
                })
                // Register in-memory activity store for non-database scenarios
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IActivityStore, InMemoryActivityStore>();
                    return services;
                })
                // Add activity tracking to record user access patterns
                .AddActivityTracking();
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
            )
            .AddBlazor(layoutClient => layoutClient
                .WithPortalConfiguration(c => c)
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

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        // Static files middleware must run before routing to serve _content/* paths from RCLs
        app.UseStaticFiles();

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.UseCookiePolicy();

        //app.MapMeshWeaverSignalRHubs();

        // Map MCP endpoint
        app.MapMeshMcp();

        app.MapMeshWeaver();
        app.UseMiddleware<VirtualUserMiddleware>();
        app.UseMiddleware<UserContextMiddleware>();
        app.UseMiddleware<InitializationMiddleware>();
        app.UseMiddleware<OnboardingMiddleware>();

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
    /// Adds all MeshWeaver view assemblies (Blazor, Graph, Radzen, GoogleMaps) to the Razor components endpoint.
    /// </summary>
    public static RazorComponentsEndpointConventionBuilder AddMeshViews(
        this RazorComponentsEndpointConventionBuilder builder)
        => builder.AddAdditionalAssemblies(
            typeof(ApplicationPage).Assembly,              // MeshWeaver.Blazor (includes ApplicationPage with catch-all route)
            typeof(MeshNodeEditorView).Assembly,           // MeshWeaver.Blazor.Graph
            typeof(RadzenChartView).Assembly,              // MeshWeaver.Blazor.Radzen
            typeof(GoogleMapView).Assembly                 // MeshWeaver.Blazor.GoogleMaps
        );
}

public class StylesConfiguration
{
    public string? StylesheetName { get; set; }
}
