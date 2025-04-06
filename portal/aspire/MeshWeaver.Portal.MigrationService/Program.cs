using MeshWeaver.Portal.MigrationService;
using MeshWeaver.Portal.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults from your Aspire setup
builder.AddServiceDefaults();

// Add the migration service
builder.Services.AddHostedService<PostgreSqlMigrationService>();

builder.ConfigurePostgreSqlContext("meshweaverdb");

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
