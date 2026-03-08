using Microsoft.Extensions.Hosting;
using MeshWeaver.Hosting.PostgreSql;

var builder = Host.CreateApplicationBuilder(args);

builder.AddNpgsqlDataSource("meshweaver");

// Read embedding dimensions from configuration (passed by AppHost via Embedding__Dimensions)
var embeddingOptions = builder.Configuration.GetSection("Embedding").Get<EmbeddingOptions>() ?? new EmbeddingOptions();
builder.Services.Configure<PostgreSqlStorageOptions>(o => o.VectorDimensions = embeddingOptions.Dimensions);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
