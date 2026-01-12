using System.IdentityModel.Tokens.Jwt;
using MeshWeaver.AI;
using MeshWeaver.AI.AzureFoundry;
using MeshWeaver.AI.AzureOpenAI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Blazor.GoogleMaps;
using MeshWeaver.Blazor.Graph;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Blazor.Monaco;
using MeshWeaver.Blazor.Pages;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Blazor.Portal.Authentication;
using MeshWeaver.Blazor.Radzen;
using MeshWeaver.ContentCollections;
using MeshWeaver.GoogleMaps;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Hosting.Activity;
using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Hosting.Blazor;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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

namespace Loom.Portal.Shared;

public static class LoomConfiguration
{
    /// <summary>
    /// Configures web portal services for Loom.
    /// Pattern taken from MeshWeaver.Portal's SharedPortalConfiguration.
    /// </summary>
    public static void ConfigureLoomServices(this WebApplicationBuilder builder)
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

        // Configure AI factories (read from appsettings)
        services.AddAzureFoundryClaude(config =>
        {
            builder.Configuration.GetSection("Anthropic").Bind(config);
            config.DisplayOrder = 0;  // Anthropic first
        });

        services.AddAzureFoundry(config =>
        {
            builder.Configuration.GetSection("AzureAIS").Bind(config);
            config.DisplayOrder = 10;  // Azure Foundry second
        });

        services.AddAzureOpenAI(config =>
        {
            builder.Configuration.GetSection("AzureOpenAIS").Bind(config);
            config.DisplayOrder = 20;  // Azure OpenAI last
        });

        // Register the factory provider (must be after all factory registrations)
        services.AddAgentChatFactoryProvider();

        // Configure GoogleMaps
        services.Configure<GoogleMapsConfiguration>(builder.Configuration.GetSection("GoogleMaps"));

        services.AddHttpContextAccessor();
        services.AddHttpClient();
        services.AddSignalR();
        services.AddControllers();

        services.Configure<StylesConfiguration>(
            builder.Configuration.GetSection("Styles"));

        // Configure authentication - based on Authentication:Provider or EntraId configuration
        var authSection = builder.Configuration.GetSection(AuthenticationOptions.SectionName);
        var entraIdConfig = builder.Configuration.GetSection("EntraId");

        // Determine provider: explicit config > EntraId presence > Dev default
        var provider = authSection["Provider"]
            ?? (entraIdConfig.GetChildren().Any() ? AuthenticationProviders.MicrosoftIdentity : AuthenticationProviders.Dev);

        // Register authentication navigation service
        services.AddAuthenticationNavigation(options =>
        {
            options.Provider = provider;

            // Allow custom paths from config
            if (authSection["LoginPath"] is { } loginPath)
                options.LoginPath = loginPath;
            if (authSection["LogoutPath"] is { } logoutPath)
                options.LogoutPath = logoutPath;
        });

        // Configure authentication middleware based on provider
        switch (provider)
        {
            case AuthenticationProviders.MicrosoftIdentity:
                JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
                services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                    .AddMicrosoftIdentityWebApp(entraIdConfig);
                services.AddControllersWithViews()
                    .AddMicrosoftIdentityUI();
                break;

            default:
                // Persist data protection keys so cookies survive app restarts
                var keysPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Loom", "DataProtection-Keys");
                Directory.CreateDirectory(keysPath);
                services.AddDataProtection()
                    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
                    .SetApplicationName("LoomPortal");

                services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                    .AddCookie(options =>
                    {
                        options.LoginPath = "/dev/login";
                        options.LogoutPath = "/dev/logout";
                        options.ExpireTimeSpan = TimeSpan.FromDays(7);
                        options.SlidingExpiration = true;
                        options.Cookie.Name = "LoomDevAuth";
                        options.Cookie.HttpOnly = true;
                        options.Cookie.IsEssential = true;
                        options.Cookie.SameSite = SameSiteMode.Lax;
                    });
                break;
        }

        services.AddAuthorization();
    }

    extension<TBuilder>(TBuilder builder) where TBuilder : MeshBuilder
    {
        /// <summary>
        /// Configures the mesh with Graph domain only.
        ///
        /// Configuration is read from appsettings:
        /// - Graph:Storage:Type - Storage type: "FileSystem", "AzureBlob", or "Cosmos"
        /// - Graph:Storage:BasePath - Base path for FileSystem storage
        /// - Graph:Storage:ConnectionString - Connection string for AzureBlob/Cosmos
        /// - storage - Content collection configuration (Name, SourceType, BasePath)
        /// </summary>
        public TBuilder ConfigureLoomMesh(IConfiguration configuration)
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
            }

            return (TBuilder)builder
                // Configure persistence from Graph:Storage section
                .ConfigureServices(services => services.AddPersistence(graphStorageConfig))
                // Enable Row-Level Security for access control
                .AddRowLevelSecurity()
                // Configure graph from the same base path
                .AddJsonGraphConfiguration(basePath ?? Directory.GetCurrentDirectory())
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
                // Add content collections at mesh level (infrastructure + storage config, no layout areas)
                // The storage collection is registered as a source for node hub mappings
                .ConfigureHub(hub =>
                {
                    if (contentStorageConfig == null)
                        return hub;
                    return hub.AddContentCollections([contentStorageConfig]);
                })
                // Configure default views and content collections for each node hub
                // Order matters: AddContentCollections registers $Content area first,
                // then AddDefaultViews sets CatalogArea as default (can be overridden by node type config)
                .ConfigureDefaultNodeHub(config =>
                {
                    if (contentStorageConfig != null)
                    {
                        var nodePath = config.Address.ToString();
                        config = config
                            .MapContentCollection("content", contentStorageConfig.Name, $"content/{nodePath}");
                    }

                    return config.AddDefaultViews();
                })
                // Add activity tracking to record user access patterns
                .AddActivityTracking();
        }

        /// <summary>
        /// Configures the portal with Graph views, Charts, GoogleMaps, and Radzen.
        /// </summary>
        public TBuilder ConfigureLoomPortal() => (TBuilder)builder
            .ConfigureHub(mesh => mesh
                .AddRadzenDataGrid()
                .AddRadzenCharts()
                .AddGoogleMaps()
                .AddGraphViews()  // Also enables @ autocomplete in markdown editors
                .AddMonacoViews()
            )
            .AddBlazor(layoutClient => layoutClient
                .WithPortalConfiguration(c => c)
            );
    }

    /// <summary>
    /// Starts the Loom portal application with the specified App component type.
    /// Pattern taken from MeshWeaver.Portal's StartPortalApplication.
    /// </summary>
    public static void StartLoomApplication<TApp>(this WebApplication app) where TApp : IComponent
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LoomConfiguration));
#pragma warning disable CA1416
        logger.LogInformation("Starting Loom portal on PID: {PID}", Environment.ProcessId);
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

        app.MapMeshWeaver();
        app.UseMiddleware<UserContextMiddleware>();
        app.UseHttpsRedirection();
        app.MapStaticAssets();
        app.MapControllers();
        app.MapRazorComponents<TApp>()
            .AddMeshViews()
            .AddInteractiveServerRenderMode();

        app.Run();
#pragma warning disable CA1416
        logger.LogInformation("Started Loom portal on PID: {PID}", Environment.ProcessId);
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
