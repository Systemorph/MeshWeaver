using System.IdentityModel.Tokens.Jwt;
using Memex.Portal.Shared.Authentication;
using Memex.Portal.Shared.Settings;
using MeshWeaver.AI;
using MeshWeaver.AI.AzureFoundry;
using MeshWeaver.AI.AzureOpenAI;
using MeshWeaver.AI.ClaudeCode;
using MeshWeaver.AI.Copilot;
using MeshWeaver.AI.Layout;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Blazor.AI;
using MeshWeaver.Blazor.GoogleMaps;
using MeshWeaver.Blazor.Graph;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Blazor.Pages;
using MeshWeaver.Blazor.Portal.Authentication;
using MeshWeaver.Blazor.Portal.Chat;
using MeshWeaver.Blazor.Portal.Components;
using MeshWeaver.Blazor.Radzen;
using MeshWeaver.ContentCollections;
using MeshWeaver.Documentation;
using MeshWeaver.GoogleMaps;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Activity;
using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Hosting.Blazor;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
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
        services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
        {
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
        services.AddControllers()
                .AddApplicationPart(typeof(MemexConfiguration).Assembly);
        services.Configure<StylesConfiguration>(
            builder.Configuration.GetSection("Styles"));

        // Register API token service for MCP bearer auth
        services.AddSingleton<ApiTokenService>();

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

            // Use partitioned persistence for FileSystem to support per-org partitions
            var usePartitioned = string.Equals(graphStorageConfig.Type, "FileSystem", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(graphStorageConfig.BasePath);

            return (TBuilder)builder
                // Configure persistence from Graph:Storage section.
                // Skip if IPartitionedStoreFactory already registered (e.g., PostgreSQL from Program.cs)
                .ConfigureServices(services =>
                {
                    if (services.Any(sd => sd.ServiceType == typeof(IPartitionedStoreFactory)))
                        return services;

                    return usePartitioned
                        ? services.AddPartitionedFileSystemPersistence(graphStorageConfig.BasePath!)
                        : services.AddPersistence(graphStorageConfig);
                })
                // Enable Row-Level Security for access control
                .AddRowLevelSecurity()
                // Configure graph from the same base path
                .AddGraph()
                .AddOrganizationType()
                .AddPortalType()
                .AddAI()
                .AddSelfRegistry()
                .AddDocumentation()
                // Register Azure Blob support for content collections.
                .ConfigureServices(services => services.AddAzureBlob())
                // Register the mesh catalog and its public interfaces
                .ConfigureServices(services => services.AddMeshCatalog())
                // Configure default views and content collections for each node hub
                // Each hub gets its own "content" collection pointing to a subdirectory
                .ConfigureDefaultNodeHub(config =>
                {
                    // Declared before the if-block so it's available for both the "content"
                    // collection mapping below and the "attachments" mapping further down.
                    var nodePath = config.Address.ToString();

                    if (contentStorageConfig != null)
                    {
                        // Scope static media (SVG, PNG, JPG) to a per-node subdirectory
                        // so each hub serves only its own content files.
                        var contentSubdir = $"content/{nodePath}";
                        // Combine with original BasePath for FileSystem; for AzureBlob, subdirectory is the blob prefix
                        var basePath = string.IsNullOrEmpty(contentStorageConfig.BasePath)
                            ? contentSubdir
                            : Path.Combine(contentStorageConfig.BasePath, contentSubdir);
                        var nodeContentConfig = contentStorageConfig with
                        {
                            Name = "content",
                            IsEditable = true,
                            BasePath = basePath,
                            Settings = new Dictionary<string, string>(contentStorageConfig.Settings ?? new())
                            {
                                ["BasePath"] = basePath
                            }
                        };
                        config = config.AddContentCollection(_ => nodeContentConfig);
                    }

                    // Map "attachments" to "storage" with per-node subdirectory
                    // (needed by FutuRe and other samples that store datacube.csv, etc.)
                    config = config.MapContentCollection("attachments", "storage", $"attachments/{nodePath}");

                    return config.AddDefaultLayoutAreas().AddThreadsLayoutArea().AddApiTokensSettingsTab();
                })
                // Add activity tracking to record user access patterns via ActivityLogBundler
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
                .AddUserProfileViews() // Register UserProfilePageView
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

        // Forward headers from reverse proxy (Azure Container Apps) so OIDC
        // middleware constructs redirect URIs with the correct scheme and host.
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                             | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
        });

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
