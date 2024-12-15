using System.Diagnostics;
using MeshWeaver.Blazor.AgGrid;
using MeshWeaver.Blazor.ChartJs;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Blazor;
using Microsoft.Extensions.Logging.Console;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Northwind.ViewModel;
using MeshWeaver.Portal;
using MeshWeaver.Blazor.Notebooks;
using MeshWeaver.Hosting.SignalR;
using MeshWeaver.Mesh;
using MeshWeaver.Kernel.Hub;
using Microsoft.AspNetCore.ResponseCompression;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddSingleton<ConsoleFormatter, CsvConsoleFormatter>();
builder.Services.Configure<CsvConsoleFormatterOptions>(options =>
{
    options.TimestampFormat = "hh:mm:ss:fff";
    options.IncludeTimestamp = true;
});

builder.Services.AddSingleton<KernelHub>();
builder.Services.AddLogging(config => config.AddConsole(
    options =>
    {
        options.FormatterName = nameof(CsvConsoleFormatter);
    }).AddDebug());

builder.UseMeshWeaver(
    new UiAddress(),
    config => config
        .ConfigureMesh(
            mesh => mesh
            .InstallAssemblies(typeof(NorthwindViewModels).Assembly.Location)
        )
        .AddKernel()
        .AddBlazor(x =>
            x
                .AddChartJs()
                .AddAgGrid()
                .AddNotebooks()
        )
        .AddSignalRHubs()
        .UseMonolithMesh()
);

builder.Services.AddSignalR();
builder.Services.AddResponseCompression(opts =>
{
    opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        new[] { "application/octet-stream" });
});

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
app.MapMeshWeaverHubs();
app.MapFallbackToPage("/_Host"); 
app.Run();

