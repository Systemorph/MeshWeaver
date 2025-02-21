using System.Diagnostics;
using MeshWeaver.Articles;
using MeshWeaver.Blazor.AgGrid;
using MeshWeaver.Blazor.ChartJs;
using MeshWeaver.Blazor.Pages;
using MeshWeaver.Hosting.Blazor;
using MeshWeaver.Hosting.SignalR;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Portal.Shared.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Portal.Shared.Web;

public static class SharedPortalConfiguration
{
    public static void ConfigureWebPortalServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSignalR();
        builder.Services.Configure<List<ArticleSourceConfig>>(builder.Configuration.GetSection("ArticleCollections"));
        builder.Services.Configure<StylesConfiguration>(
            builder.Configuration.GetSection("Styles"));
    }

    public static TBuilder ConfigureWebPortalMesh<TBuilder>(this TBuilder builder)
        where TBuilder:MeshBuilder
        =>
        (TBuilder)builder.ConfigureServices(services =>
            {
                services.AddRazorComponents().AddInteractiveServerComponents();
                services.AddSingleton<CacheStorageAccessor>();
                services.AddSingleton<IAppVersionService, AppVersionService>();
                return services;
            })
            .AddBlazor(layoutClient => layoutClient
                    .AddChartJs()
                    .AddAgGrid()
                    .WithPortalConfiguration(c => 
                        c.AddLayout(layout => layout
                            .AddNavMenu()
                            .AddArticleLayouts()))
            )
            .AddSignalRHubs();


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
            .AddAdditionalAssemblies(typeof(ApplicationPage).Assembly)
            .AddInteractiveServerRenderMode();

        app.Run();
#pragma warning disable CA1416
        logger.LogInformation("Started blazor server on PID: {PID}", Process.GetCurrentProcess().Id);
#pragma warning restore CA1416
    }


}
