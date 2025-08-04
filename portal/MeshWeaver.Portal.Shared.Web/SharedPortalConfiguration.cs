using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MeshWeaver.AI.AzureOpenAI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Blazor.AgGrid;
using MeshWeaver.Blazor.ChartJs;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Blazor.Pages;
using MeshWeaver.ContentCollections;
using MeshWeaver.Hosting.Blazor;
using MeshWeaver.Hosting.SignalR;
using MeshWeaver.Mesh;
using MeshWeaver.Portal.AI;
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
        // This is required to be instantiated before the OpenIdConnectOptions starts getting configured.
        // By default, the claims mapping will map claim names in the old format to accommodate older SAML applications.
        // 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role' instead of 'roles'
        // This flag ensures that the ClaimsIdentity claims collection will be built from the claims in the token.
        JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true,
                reloadOnChange: true)
            .AddEnvironmentVariables();

        var services = builder.Services;
        services.AddRazorPages()

            .AddMicrosoftIdentityUI();
        services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddHubOptions(opt =>
            {
                opt.DisableImplicitFromServicesParameters = true;
            }); services.AddPortalAI();
        services.AddMemoryChatPersistence();

        // configure AzureOpenAI chat
        services.Configure<AzureOpenAIConfiguration>(
            builder.Configuration.GetSection("AzureOpenAIS")
            );
        services.AddAzureOpenAI();

        // configure Azure Foundry chat
        //services.Configure<AzureAIFoundryConfiguration>(builder.Configuration.GetSection("AzureAIS"));
        //services.AddAzureAIFoundry();


        services.AddScoped<CacheStorageAccessor>();
        services.AddSingleton<IAppVersionService, AppVersionService>();
        services.AddSingleton<DimensionManager>();

        services.AddHttpContextAccessor();


        var entraIdConfig = builder.Configuration.GetSection("EntraId");
        if (entraIdConfig.GetChildren().Any())
        {
            builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApp(entraIdConfig);       // In ConfigureWebPortalServices in SharedPortalConfiguration.cs
            builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                var roleMappings = builder.Configuration
                    .GetSection("EntraId:RoleMappings")
                    .GetChildren()
                    .ToDictionary(x => x.Value!, x => x.Key);

                options.Events.OnTokenValidated = async context =>
                {
                    var identity = context.Principal?.Identity as ClaimsIdentity;
                    if (identity?.IsAuthenticated == true)
                    {
                        var groupClaims = identity.FindAll("groups").ToList();
                        foreach (var groupClaim in groupClaims)
                        {
                            if (roleMappings.TryGetValue(groupClaim.Value, out var roleName))
                            {
                                identity.AddClaim(new Claim(ClaimTypes.Role, roleName));
                            }
                        }
                    }
                    await Task.CompletedTask;
                };
            });

            builder.Services.AddControllersWithViews()
                .AddMicrosoftIdentityUI();

            builder.Services.AddAuthorization();
        }



        builder.Services.AddSignalR();
        builder.Services.Configure<List<ContentSourceConfig>>(builder.Configuration.GetSection("ArticleCollections"));
        builder.Services.Configure<StylesConfiguration>(
            builder.Configuration.GetSection("Styles"));
    }

    public static TBuilder ConfigureWebPortal<TBuilder>(this TBuilder builder)
            where TBuilder : MeshBuilder
            =>
            (TBuilder)builder
                .ConfigureHub(mesh => mesh.AddAgGrid().AddChartJs())
                .AddBlazor(layoutClient => layoutClient
                        .WithPortalConfiguration(c =>
                            c.AddArticles()
                        )
                )
                .AddSignalRHubs();


    public static void StartPortalApplication(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SharedPortalConfiguration));
#pragma warning disable CA1416
        logger.LogInformation("Starting blazor server on PID: {PID}", Environment.ProcessId);
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

        if (app.Configuration.GetSection("EntraId").GetChildren().Any())
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        //app.MapMeshWeaverSignalRHubs();

        app.MapMeshWeaver();
        app.UseMiddleware<UserContextMiddleware>();
        app.UseHttpsRedirection();


        app.MapStaticAssets();
        app.MapControllers();
        app.MapRazorComponents<App>()
            .AddAdditionalAssemblies(typeof(ApplicationPage).Assembly, typeof(MeshWeaver.Blazor.Chat.AgentChatView).Assembly)
            .AddInteractiveServerRenderMode();

        app.Run();
#pragma warning disable CA1416
        logger.LogInformation("Started blazor server on PID: {PID}", Environment.ProcessId);
#pragma warning restore CA1416
    }
}
