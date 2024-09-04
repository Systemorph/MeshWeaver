using System.Diagnostics;
using MeshWeaver.Application;
using MeshWeaver.Assistant;
using MeshWeaver.Blazor.AgGrid;
using MeshWeaver.Blazor.Assistant;
using MeshWeaver.Blazor.ChartJs;
using MeshWeaver.Catalog.ViewModel;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Blazor;
using Microsoft.Extensions.Logging.Console;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Northwind.Application;
using MeshWeaver.Northwind.ViewModel;

var builder = WebApplication.CreateBuilder(args);

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


builder.UseMeshWeaver(
    new UiAddress(),
    config => config
        .ConfigureMesh(mesh => mesh.InstallAssemblies(typeof(NorthwindApplicationAttribute).Assembly.Location))
        .ConfigureMesh(mesh => mesh.InstallAssemblies(typeof(CatalogApplicationAttribute).Assembly.Location))
        .AddBlazor(x =>
            x
                .AddChartJs()
                .AddAgGrid()
                .AddAssistant()
        )
        .AddMonolithMesh()
);

builder.AddAssistantService();

if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Starting blazor server on PID: {PID}", Process.GetCurrentProcess().Id);

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
app.MapFallbackToPage("/_Host");

app.Run();

