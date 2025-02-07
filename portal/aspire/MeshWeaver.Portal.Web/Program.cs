using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

var builder = WebApplication.CreateBuilder(args);

builder.AddAspireServiceDefaults();
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
builder.ConfigurePortalApplication();
builder.UseMeshWeaver(blazorAddress,
        configuration: config => config
            .UseOrleansMeshClient()
            .ConfigureWebPortalMesh()
    );


var app = builder.Build();
app.StartPortalApplication();
