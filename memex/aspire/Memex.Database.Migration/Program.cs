using Microsoft.Extensions.Hosting;
using MeshWeaver.Hosting.PostgreSql;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("memex") ?? "";
if (connectionString.Contains("database.azure.com"))
    builder.AddAzureNpgsqlDataSource("memex");
else
    builder.AddNpgsqlDataSource("memex");

// Derive vector dimensions from embedding model (passed by AppHost via Embedding__Model)
var embeddingOptions = builder.Configuration.GetSection("Embedding").Get<EmbeddingOptions>() ?? new EmbeddingOptions();
builder.Services.Configure<PostgreSqlStorageOptions>(o => o.VectorDimensions = 1536);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
