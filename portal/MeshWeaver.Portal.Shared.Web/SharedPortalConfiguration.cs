using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
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
        // This is required to be instantiated before the OpenIdConnectOptions starts getting configured.
        // By default, the claims mapping will map claim names in the old format to accommodate older SAML applications.
        // 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role' instead of 'roles'
        // This flag ensures that the ClaimsIdentity claims collection will be built from the claims in the token.
        JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

        var services = builder.Services;

        services.AddHttpContextAccessor();

        builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("EntraId"));

        builder.Services.AddControllersWithViews()
            .AddMicrosoftIdentityUI();

        builder.Services.AddAuthorization();

        // Configure Antiforgery
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "MeshWeaver.AntiforgeryCookie";
            options.FormFieldName = "AntiforgeryField";
            options.HeaderName = "X-CSRF-TOKEN";
        });

        builder.Services.AddSignalR();
        builder.Services.Configure<List<ArticleSourceConfig>>(builder.Configuration.GetSection("ArticleCollections"));
        builder.Services.Configure<StylesConfiguration>(
            builder.Configuration.GetSection("Styles"));
    }

    public static TBuilder ConfigureWebPortal<TBuilder>(this TBuilder builder)
            where TBuilder : MeshBuilder
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
        app.UseAntiforgery();
        app.UseCookiePolicy();

        app.UseAuthentication();
        app.UseAuthorization();

        //app.MapMeshWeaverSignalRHubs();

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
