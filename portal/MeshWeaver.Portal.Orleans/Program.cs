using MeshWeaver.Hosting;
using Orleans.Runtime;
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.UseOrleans();

var app = builder.Build();

app.MapGet("/", () => "OK");

await app.RunAsync();

