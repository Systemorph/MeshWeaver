using Microsoft.Extensions.Hosting;
using MeshWeaver.Hosting.PostgreSql;

var builder = Host.CreateApplicationBuilder(args);

builder.AddNpgsqlDataSource("meshweaver");

// Derive vector dimensions from embedding model (passed by AppHost via Embedding__Model)
var embeddingOptions = builder.Configuration.GetSection("Embedding").Get<EmbeddingOptions>() ?? new EmbeddingOptions();
builder.Services.Configure<PostgreSqlStorageOptions>(o => o.VectorDimensions = embeddingOptions.Dimensions);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
