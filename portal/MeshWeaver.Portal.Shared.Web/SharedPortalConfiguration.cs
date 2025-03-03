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
using MeshWeaver.Portal.Shared.Web.Resize;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

namespace MeshWeaver.Portal.Shared.Web;

public static class SharedPortalConfiguration
{
    public static void ConfigureWebPortalServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApp(options =>
            {
                builder.Configuration.GetSection("AzureAdB2C").Bind(options);
                // Increase token handling parameters to avoid 431 errors
                options.ResponseType = "code";  // Use authorization code flow instead of implicit
                options.UseTokenLifetime = true;
            });

        // Increase Kestrel limits to handle larger request headers
        builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestHeadersTotalSize = 64 * 1024; // Increase from default 32KB to 64KB
        });

        builder.Services.AddControllersWithViews()
            .AddMicrosoftIdentityUI();

        builder.Services.AddAuthorization(options =>
        {
            //options.FallbackPolicy = options.DefaultPolicy;
        });
        
        builder.Services.AddSignalR();
        builder.Services.Configure<List<ArticleSourceConfig>>(builder.Configuration.GetSection("ArticleCollections"));
        builder.Services.Configure<StylesConfiguration>(
            builder.Configuration.GetSection("Styles"));
    }

    public static TBuilder ConfigureWebPortal<TBuilder>(this TBuilder builder)
        where TBuilder:MeshBuilder
        =>
        (TBuilder)builder.ConfigureServices(services =>
            {
                services.AddRazorPages()
                    .AddMicrosoftIdentityUI();
                services.AddRazorComponents().AddInteractiveServerComponents();
                services.AddSingleton<CacheStorageAccessor>();
                services.AddSingleton<IAppVersionService, AppVersionService>();
                services.AddSingleton<DimensionManager>();
                return services;
            })
            .AddBlazor(layoutClient => layoutClient
                    .AddChartJs()
                    .AddAgGrid()
                    .WithPortalConfiguration(c => 
                        c.AddLayout(layout => layout
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
        app.UseAuthentication();
        app.UseAuthorization();

        app.UseAntiforgery();
        app.MapMeshWeaverSignalRHubs();

        app.MapMeshWeaver();
        app.UseHttpsRedirection();


        app.MapStaticAssets();
        app.MapControllers();
        app.MapRazorComponents<App>()
            .AddAdditionalAssemblies(typeof(ApplicationPage).Assembly)
            .AddInteractiveServerRenderMode();

        app.Run();
#pragma warning disable CA1416
        logger.LogInformation("Started blazor server on PID: {PID}", Process.GetCurrentProcess().Id);
#pragma warning restore CA1416
    }


}
