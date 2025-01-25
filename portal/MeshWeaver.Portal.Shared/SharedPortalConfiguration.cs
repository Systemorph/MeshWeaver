using System.Diagnostics;
using MeshWeaver.Articles;
using MeshWeaver.Blazor.AgGrid;
using MeshWeaver.Blazor.ChartJs;
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
using Microsoft.FluentUI.AspNetCore.Components.Icons.Filled;

namespace MeshWeaver.Portal.Shared;

public static class SharedPortalConfiguration
{
    public static void ConfigurePortalApplication(this WebApplicationBuilder builder)
    {
        builder.AddServiceDefaults();
        // Add services to the container.
        builder.Services.AddSingleton<ConsoleFormatter, CsvConsoleFormatter>();
        builder.Services.Configure<CsvConsoleFormatterOptions>(options =>
        {
            options.TimestampFormat = "hh:mm:ss:fff";
            options.IncludeTimestamp = true;
        });

        builder.Services.AddLogging(config => config.AddConsole(
            options =>
            {
                options.FormatterName = nameof(CsvConsoleFormatter);
            }).AddDebug());
        builder.Services.AddSignalR();
        builder.Services.AddResponseCompression(opts =>
        {
            opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
                new[] { "application/octet-stream" });
        });


    }

    public static MeshBuilder ConfigurePortalMesh(this MeshBuilder builder)
    {
        return builder.ConfigureMesh(
                mesh => mesh
                    .InstallAssemblies(typeof(NorthwindViewModels).Assembly.Location)
            )
            .AddKernel()
            .AddBlazor(x =>
                x
                    .AddChartJs()
                    .AddAgGrid()
            )
            .AddArticles(articles => articles
                .WithCollection(new FileSystemArticleCollection(
                    "Northwind", 
                    GetBaseDirectory()
                    ).WithDefaultAddress(new ApplicationAddress("Northwind")))
            )
            .AddSignalRHubs();
    }

    private static string GetBaseDirectory()
    {
#if DEBUG
        return Path.Combine(
            Path.GetDirectoryName(typeof(NorthwindViewModels).Assembly.Location)!,
            "../../../../../modules/Northwind/MeshWeaver.Northwind.ViewModel/Markdown");
#else
        return Path.Combine(
            Path.GetDirectoryName(typeof(NorthwindViewModels).Assembly.Location)!,
            "Markdown");
#endif
    }

    public static void StartPortalApplication(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SharedPortalConfiguration));
#pragma warning disable CA1416
        logger.LogInformation("Starting blazor server on PID: {PID}", Process.GetCurrentProcess().Id);
#pragma warning restore CA1416

        app.MapDefaultEndpoints();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();
        app.MapBlazorHub();
        app.MapMeshWeaverHubs();
        app.MapFallbackToPage("/_Host");
        app.MapStaticContent(app.Services.GetRequiredService<IArticleService>());
        app.Run();
#pragma warning disable CA1416
        logger.LogInformation("Started blazor server on PID: {PID}", Process.GetCurrentProcess().Id);
#pragma warning restore CA1416
    }
}
