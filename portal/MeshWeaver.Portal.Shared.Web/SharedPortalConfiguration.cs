using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MeshWeaver.AI;
using MeshWeaver.AI.AzureFoundry;
using MeshWeaver.AI.AzureOpenAI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Blazor.Chat;
using MeshWeaver.Blazor.GoogleMaps;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Blazor.Pages;
using MeshWeaver.Blazor.Radzen;
using MeshWeaver.ContentCollections;
using MeshWeaver.GoogleMaps;
using MeshWeaver.Hosting.Blazor;
using MeshWeaver.Messaging;
using MeshWeaver.Insurance.Domain;
using MeshWeaver.Insurance.Domain.Services;
using MeshWeaver.Mesh;
using MeshWeaver.Portal.AI;
using MeshWeaver.Blazor.Portal.Infrastructure;
using MeshWeaver.Blazor.Portal.Resize;
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
            })
            .AddChatWindowState();

        // Configure Radzen
        services.AddRadzenServices();
        services.AddPortalAI();
        services.AddMemoryChatPersistence();

        // Configure AI factories (ordered by DisplayOrder - Anthropic first)
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

        // setting up google maps configuration
        services.Configure<GoogleMapsConfiguration>(builder.Configuration.GetSection("GoogleMaps"));

        services.AddScoped<CacheStorageAccessor>();
        services.AddSingleton<IAppVersionService, AppVersionService>();
        services.AddSingleton<DimensionManager>();
        services.AddSingleton<IPricingService, InMemoryPricingService>();

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


        builder.Services.Configure<StylesConfiguration>(
            builder.Configuration.GetSection("Styles"));
    }

    public static TBuilder ConfigureWebPortal<TBuilder>(this TBuilder builder, IConfiguration _)
        where TBuilder : MeshBuilder
        =>
            (TBuilder)builder
                .ConfigureHub(mesh => mesh
                    .AddContentCollections()
                    .AddRadzenDataGrid()
                    .AddRadzenCharts()
                    .AddGoogleMaps()
                )
                .AddBlazor(layoutClient => layoutClient
                    .WithPortalConfiguration(c =>
                        c.AddArticles(
                            new ContentCollectionConfig()
                            {
                                SourceType = HubStreamProviderFactory.SourceType,
                                Name = "Blog",
                                Address = AddressExtensions.CreateAppAddress("Documentation")
                            },
                            new ContentCollectionConfig()
                            {
                                SourceType = HubStreamProviderFactory.SourceType,
                                Name = "Documentation",
                                Address = AddressExtensions.CreateAppAddress("Documentation")
                            },
                            new ContentCollectionConfig()
                            {
                                SourceType = HubStreamProviderFactory.SourceType,
                                Name = "Todo",
                                Address = AddressExtensions.CreateAppAddress("Todo")
                            }
                        )
                    )
                );


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

        // Static files middleware must run before routing to serve _content/* paths from RCLs
        app.UseStaticFiles();

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
            .AddAdditionalAssemblies(
                typeof(ApplicationPage).Assembly,
                typeof(Blazor.Chat.AgentChatView).Assembly,
                typeof(RadzenPivotGridView).Assembly)
            .AddInteractiveServerRenderMode();

        app.Run();
#pragma warning disable CA1416
        logger.LogInformation("Started blazor server on PID: {PID}", Environment.ProcessId);
#pragma warning restore CA1416
    }
}
