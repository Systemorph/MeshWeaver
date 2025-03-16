using Azure.Core;
using Azure.Identity;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Portal.MigrationService;
using MeshWeaver.Portal.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults from your Aspire setup
builder.AddServiceDefaults();

// Add the migration service
builder.Services.AddHostedService<PostgreSqlMigrationService>();

builder.ConfigurePostgreSqlContext();

var app = builder.Build();
app.Run();
