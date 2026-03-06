using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

[Collection("PostgreSql")]
public class PartitionedSchemaTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public PartitionedSchemaTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private PostgreSqlPartitionedStoreFactory CreateFactory()
    {
        return new PostgreSqlPartitionedStoreFactory(
            _fixture.DataSource,
            _fixture.ConnectionString,
            new PostgreSqlStorageOptions());
    }

    [Fact]
    public async Task CreateStore_CreatesSchemaTables()
    {
        var factory = CreateFactory();

        var store = await factory.CreateStoreAsync("TestDomain", TestContext.Current.CancellationToken);

        // Verify schema exists
        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT 1 FROM information_schema.schemata WHERE schema_name = 'testdomain'");
        var exists = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        exists.Should().NotBeNull("schema 'testdomain' should exist");

        // Verify tables exist in the schema
        await using var cmd2 = _fixture.DataSource.CreateCommand(
            """
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'testdomain' AND table_name = 'mesh_nodes'
            """);
        var table = await cmd2.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        table.Should().NotBeNull("mesh_nodes table should exist in testdomain schema");
    }

    [Fact]
    public async Task SaveAndRead_AcrossSchemas_DataIsolated()
    {
        var factory = CreateFactory();

        var storeA = await factory.CreateStoreAsync("Alpha", TestContext.Current.CancellationToken);
        var storeB = await factory.CreateStoreAsync("Beta", TestContext.Current.CancellationToken);

        // Save to Alpha partition
        var nodeA = MeshNode.FromPath("Alpha/Reports") with { Name = "Alpha Reports" };
        await storeA.StorageAdapter.WriteAsync(nodeA, _options, TestContext.Current.CancellationToken);

        // Save to Beta partition
        var nodeB = MeshNode.FromPath("Beta/Reports") with { Name = "Beta Reports" };
        await storeB.StorageAdapter.WriteAsync(nodeB, _options, TestContext.Current.CancellationToken);

        // Read back from Alpha — should only find Alpha data
        var readA = await storeA.StorageAdapter.ReadAsync("Alpha/Reports", _options, TestContext.Current.CancellationToken);
        readA.Should().NotBeNull();
        readA!.Name.Should().Be("Alpha Reports");

        // Alpha should not have Beta data
        var readB = await storeA.StorageAdapter.ReadAsync("Beta/Reports", _options, TestContext.Current.CancellationToken);
        readB.Should().BeNull("Beta data should not be in Alpha schema");

        // Read back from Beta
        var readBeta = await storeB.StorageAdapter.ReadAsync("Beta/Reports", _options, TestContext.Current.CancellationToken);
        readBeta.Should().NotBeNull();
        readBeta!.Name.Should().Be("Beta Reports");
    }

    [Fact]
    public async Task RoutingCore_SaveRoutes_ByFirstSegment()
    {
        var factory = CreateFactory();
        var router = new RoutingPersistenceServiceCore(factory);

        var nodeA = MeshNode.FromPath("Gamma/Doc1") with { Name = "Gamma Doc" };
        var nodeB = MeshNode.FromPath("Delta/Doc1") with { Name = "Delta Doc" };

        await router.SaveNodeAsync(nodeA, _options, TestContext.Current.CancellationToken);
        await router.SaveNodeAsync(nodeB, _options, TestContext.Current.CancellationToken);

        // Read should route correctly
        var readA = await router.GetNodeAsync("Gamma/Doc1", _options, TestContext.Current.CancellationToken);
        readA.Should().NotBeNull();
        readA!.Name.Should().Be("Gamma Doc");

        var readB = await router.GetNodeAsync("Delta/Doc1", _options, TestContext.Current.CancellationToken);
        readB.Should().NotBeNull();
        readB!.Name.Should().Be("Delta Doc");
    }

    [Fact]
    public async Task RoutingCore_GetChildren_RootLevel_ReturnsFromAllPartitions()
    {
        var factory = CreateFactory();
        var router = new RoutingPersistenceServiceCore(factory);

        var nodeA = MeshNode.FromPath("Epsilon") with { Name = "Epsilon Root" };
        var nodeB = MeshNode.FromPath("Zeta") with { Name = "Zeta Root" };

        await router.SaveNodeAsync(nodeA, _options, TestContext.Current.CancellationToken);
        await router.SaveNodeAsync(nodeB, _options, TestContext.Current.CancellationToken);

        var children = new List<MeshNode>();
        await foreach (var child in router.GetChildrenAsync(null, _options))
            children.Add(child);

        children.Should().Contain(n => n.Path == "Epsilon");
        children.Should().Contain(n => n.Path == "Zeta");
    }

    [Fact]
    public async Task DiscoverPartitions_FindsExistingSchemas()
    {
        var factory = CreateFactory();

        // Create some partitions
        await factory.CreateStoreAsync("Discover1", TestContext.Current.CancellationToken);
        await factory.CreateStoreAsync("Discover2", TestContext.Current.CancellationToken);

        // Create a fresh factory and discover
        var freshFactory = CreateFactory();
        var partitions = await freshFactory.DiscoverPartitionsAsync(TestContext.Current.CancellationToken);

        partitions.Should().Contain("discover1");
        partitions.Should().Contain("discover2");
    }

    [Fact]
    public async Task SanitizeSchemaName_VariousInputs()
    {
        PostgreSqlPartitionedStoreFactory.SanitizeSchemaName("ACME")
            .Should().Be("acme");

        PostgreSqlPartitionedStoreFactory.SanitizeSchemaName("My-Company")
            .Should().Be("my_company");

        PostgreSqlPartitionedStoreFactory.SanitizeSchemaName("123Start")
            .Should().Be("_123start");

        PostgreSqlPartitionedStoreFactory.SanitizeSchemaName("Valid_Name_99")
            .Should().Be("valid_name_99");
    }

    [Fact]
    public async Task Delete_InOnePartition_DoesNotAffectOther()
    {
        var factory = CreateFactory();
        var router = new RoutingPersistenceServiceCore(factory);

        var nodeA = MeshNode.FromPath("Eta/Item1") with { Name = "Eta Item" };
        var nodeB = MeshNode.FromPath("Theta/Item1") with { Name = "Theta Item" };

        await router.SaveNodeAsync(nodeA, _options, TestContext.Current.CancellationToken);
        await router.SaveNodeAsync(nodeB, _options, TestContext.Current.CancellationToken);

        // Delete from Eta
        await router.DeleteNodeAsync("Eta/Item1", ct: TestContext.Current.CancellationToken);

        // Eta item should be gone
        var readA = await router.GetNodeAsync("Eta/Item1", _options, TestContext.Current.CancellationToken);
        readA.Should().BeNull();

        // Theta item should still exist
        var readB = await router.GetNodeAsync("Theta/Item1", _options, TestContext.Current.CancellationToken);
        readB.Should().NotBeNull();
        readB!.Name.Should().Be("Theta Item");
    }
}
