using System;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that vector embedding parameters work correctly on a fresh database
/// where the vector extension is created during schema initialization.
/// </summary>
public class VectorEmbeddingTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlStorageAdapter _adapter = null!;
    private const int Dimensions = 3;

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithDatabase("vector_test")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _container.StartAsync();

        // Build data source with UseVector() BEFORE the extension exists in the DB
        // (this is the real-world scenario that was failing)
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(_container.GetConnectionString());
        dataSourceBuilder.UseVector();
        _dataSource = dataSourceBuilder.Build();

        var options = new PostgreSqlStorageOptions { VectorDimensions = Dimensions };
        await PostgreSqlSchemaInitializer.InitializeAsync(_dataSource, options);

        _adapter = new PostgreSqlStorageAdapter(_dataSource, new FakeEmbeddingProvider(Dimensions));
    }

    public async ValueTask DisposeAsync()
    {
        _dataSource?.Dispose();
        if (_container != null)
            await _container.DisposeAsync();
    }

    [Fact]
    public async Task WriteNodeWithEmbedding_OnFreshDatabase_Succeeds()
    {
        var node = new MeshNode("vec1", "test")
        {
            Name = "Vector Node",
            NodeType = "Document"
        };

        var act = () => _adapter.WriteAsync(node, new JsonSerializerOptions(), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();

        var result = await _adapter.ReadAsync("test/vec1", new JsonSerializerOptions(), TestContext.Current.CancellationToken);
        result.Should().NotBeNull();
        result!.Name.Should().Be("Vector Node");
    }

    [Fact]
    public async Task WriteNodeWithNullEmbedding_OnFreshDatabase_Succeeds()
    {
        // Use adapter without embedding provider (null embeddings)
        var adapterNoEmbed = new PostgreSqlStorageAdapter(_dataSource);

        var node = new MeshNode("vec2", "test")
        {
            Name = "No Embedding",
            NodeType = "Note"
        };

        var act = () => adapterNoEmbed.WriteAsync(node, new JsonSerializerOptions(), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    private class FakeEmbeddingProvider(int dimensions) : IEmbeddingProvider
    {
        public int Dimensions => dimensions;

        public Task<float[]?> GenerateEmbeddingAsync(string text)
        {
            var vector = new float[dimensions];
            for (int i = 0; i < dimensions; i++)
                vector[i] = (float)(i + 1) / dimensions;
            return Task.FromResult<float[]?>(vector);
        }
    }
}
