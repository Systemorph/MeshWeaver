using MeshWeaver.Blazor.GoogleMaps;
using MeshWeaver.Blazor.Graph;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Blazor.Pages;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Blazor.Portal.Infrastructure;
using MeshWeaver.Blazor.Radzen;
using MeshWeaver.GoogleMaps;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Blazor;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        // Configure GoogleMaps
        services.Configure<GoogleMapsConfiguration>(builder.Configuration.GetSection("GoogleMaps"));

        services.AddHttpContextAccessor();
        services.AddSignalR();
        services.AddControllers();

        services.Configure<StylesConfiguration>(
            builder.Configuration.GetSection("Styles"));
    }

    /// <summary>
    /// Configures the mesh with Graph domain only.
    /// Reads configuration from Graph section:
    ///
    /// For FileSystem (development):
    /// - Graph:StorageProvider = "FileSystem"
    /// - Graph:DataDirectory - path to the data directory for persistence
    /// - Graph:personsPath - path to persons content collection (avatars)
    /// - Graph:logosPath - path to logos content collection
    ///
    /// For AzureBlob (production):
    /// - Graph:StorageProvider = "AzureBlob"
    /// - Graph:ConnectionString - Azure Storage connection string
    /// - Graph:ContainerName - Azure Blob container name
    ///
    /// Can be overridden by Aspire via environment variables:
    /// - Graph__StorageProvider
    /// - Graph__DataDirectory / Graph__ConnectionString
    /// - Graph__personsPath / Graph__ContainerName
    /// </summary>
    public static TBuilder ConfigureLoomMesh<TBuilder>(this TBuilder builder, IConfiguration configuration)
        where TBuilder : MeshBuilder
    {
        var graphSection = configuration.GetSection("Graph");
        var storageProvider = graphSection["StorageProvider"] ?? "FileSystem";

        if (storageProvider.Equals("AzureBlob", StringComparison.OrdinalIgnoreCase))
        {
            // Azure Blob Storage for production
            var connectionString = graphSection["ConnectionString"]
                ?? throw new InvalidOperationException("Graph:ConnectionString is required for AzureBlob storage provider");
            var containerName = graphSection["ContainerName"] ?? "graph-data";

            // TODO: Add Azure Blob persistence when implemented
            // return (TBuilder)builder
            //     .AddAzureBlobPersistence(connectionString, containerName)
            //     .InstallAssemblies(typeof(GraphDomainAttribute).Assembly.Location);

            throw new NotImplementedException("AzureBlob storage provider is not yet implemented. Use FileSystem for now.");
        }
        else
        {
            // FileSystem for development
            var baseDir = Directory.GetCurrentDirectory();
            var dataDirectoryConfig = graphSection["DataDirectory"] ?? "Data";
            var dataDirectory = Path.IsPathRooted(dataDirectoryConfig)
                ? dataDirectoryConfig
                : Path.GetFullPath(Path.Combine(baseDir, dataDirectoryConfig));

            return (TBuilder)builder
                .AddFileSystemPersistence(dataDirectory)
                .AddJsonGraphConfiguration(dataDirectory, configuration)
                ;
        }
    }

    /// <summary>
    /// Configures the portal with Graph views, Charts, GoogleMaps, and Radzen.
    /// </summary>
    public static TBuilder ConfigureLoomPortal<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
        => (TBuilder)builder
            .ConfigureHub(mesh => mesh
                .AddRadzenDataGrid()
                .AddRadzenCharts()
                .AddGoogleMaps()
                .AddGraphViews()
            )
            .AddBlazor(layoutClient => layoutClient
                .WithPortalConfiguration(c => c)
            );

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
