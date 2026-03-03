using Microsoft.Extensions.Hosting;
using MeshWeaver.Hosting.PostgreSql;

var builder = Host.CreateApplicationBuilder(args);

builder.AddNpgsqlDataSource("meshweaver");
builder.Services.Configure<PostgreSqlStorageOptions>(_ => { });
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
