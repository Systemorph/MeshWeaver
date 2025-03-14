using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Portal.MigrationService;
using MeshWeaver.Portal.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults from your Aspire setup
builder.AddServiceDefaults();

// Add the migration service
builder.Services.AddHostedService<PostgreSqlMigrationService>();

// Add Npgsql data source using Aspire's connection string
builder.AddNpgsqlDataSource("meshweaverdb");

// Register the DbContext using the Aspire connection string
builder.Services.AddDbContextPool<MeshWeaverDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("meshweaverdb"), npgsqlOptions =>
    {
        npgsqlOptions.MigrationsAssembly(typeof(MeshWeaverDbContext).Assembly.GetName().Name);
        npgsqlOptions.EnableRetryOnFailure(); // Add retry capability
    });
});

var app = builder.Build();
app.Run();
