﻿using System.Diagnostics;
using MeshWeaver.Articles;
using MeshWeaver.Blazor.AgGrid;
using MeshWeaver.Blazor.ChartJs;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Blazor;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Portal.ServiceDefaults;
using Microsoft.Extensions.Logging.Console;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddKeyedRedisClient(StorageProviders.Redis);

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

// Add services to the container.
var blazorAddress = new UiAddress();

builder.UseMeshWeaver(blazorAddress,
        config => config
            .UseOrleansMeshClient()
            .AddBlazor(x =>
                x.AddChartJs()
                    .AddAgGrid()
            )
    )
    ;


if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Starting blazor server on PID: {PID}", Process.GetCurrentProcess().Id);

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapStaticContent(app.Services.GetRequiredService<IArticleService>());
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();

