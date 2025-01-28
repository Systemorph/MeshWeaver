using System.Diagnostics;
using MeshWeaver.Articles;
using MeshWeaver.Blazor.AgGrid;
using MeshWeaver.Blazor.ChartJs;
using MeshWeaver.Documentation;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Blazor;
using MeshWeaver.Hosting.SignalR;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Northwind.ViewModel;
using MeshWeaver.Portal.Shared.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace MeshWeaver.Portal.Shared;

public static class SharedPortalConfiguration
{
    public static void ConfigurePortalApplication(this WebApplicationBuilder builder)
    {
        builder.AddServiceDefaults();
        // Add services to the container.
        builder.Services.AddSingleton<ConsoleFormatter, CsvConsoleFormatter>();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddSignalR();
        builder.Services.Configure<List<ArticleSourceConfig>>(builder.Configuration.GetSection("ArticleCollections"));

    }

    public static MeshBuilder ConfigurePortalMesh(this MeshBuilder builder)
    {
        return builder.ConfigureMesh(
                mesh => mesh
                    .InstallAssemblies(typeof(DocumentationViewModels).Assembly.Location)
                    .InstallAssemblies(typeof(NorthwindViewModels).Assembly.Location)
            )
            .AddKernel()
            .AddBlazor(x =>
                x
                    .AddChartJs()
                    .AddAgGrid()
            )
            .AddArticles(articles 
                => articles.FromAppSettings()
                )
            .AddSignalRHubs();
    }


    public static void StartPortalApplication(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SharedPortalConfiguration));
#pragma warning disable CA1416
        logger.LogInformation("Starting blazor server on PID: {PID}", Process.GetCurrentProcess().Id);
#pragma warning restore CA1416


        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }
        app.UseRouting();
        app.UseAntiforgery();
        app.MapMeshWeaverSignalRHubs();

        app.MapMeshWeaver();
        app.UseHttpsRedirection();



        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.Run();
#pragma warning disable CA1416
        logger.LogInformation("Started blazor server on PID: {PID}", Process.GetCurrentProcess().Id);
#pragma warning restore CA1416
    }
}
