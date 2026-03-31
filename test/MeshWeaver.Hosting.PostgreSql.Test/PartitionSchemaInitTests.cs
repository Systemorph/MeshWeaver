using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests for default partition initialization:
/// - Default schemas (admin, user, portal, kernel) are created during init
/// - Satellite tables are created within schemas that have TableMappings
/// - node_type_permissions synced to all schemas
/// - Organization-created partitions get satellite tables
/// </summary>
[Collection("PostgreSql")]
public class PartitionSchemaInitTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public PartitionSchemaInitTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private PostgreSqlPartitionedStoreFactory CreateFactory(
        IEnumerable<NodeTypePermission>? permissions = null)
    {
        return new PostgreSqlPartitionedStoreFactory(
            _fixture.DataSource,
            _fixture.ConnectionString,
            new PostgreSqlStorageOptions(),
            nodeTypePermissions: permissions);
    }

    private static IEnumerable<PartitionDefinition> DefaultPartitions()
    {
        yield return new PartitionDefinition
        {
            Namespace = "Admin",
            DataSource = "default",
            Schema = "admin",
            TableMappings = PartitionDefinition.StandardTableMappings,
            Description = "System administration"
        };
        yield return new PartitionDefinition
        {
            Namespace = "User",
            DataSource = "default",
            Schema = "user",
            TableMappings = PartitionDefinition.StandardTableMappings,
            Description = "User profiles and settings"
        };
        yield return new PartitionDefinition
        {
            Namespace = "Portal",
            DataSource = "default",
            Schema = "portal",
            TableMappings = PartitionDefinition.StandardTableMappings,
            Description = "Portal sessions"
        };
        yield return new PartitionDefinition
        {
            Namespace = "Kernel",
            DataSource = "default",
            Schema = "kernel",
            TableMappings = PartitionDefinition.StandardTableMappings,
            Description = "Kernel sessions"
        };
    }

    [Fact(Timeout = 60000)]
    public async Task DefaultSchemas_CreatedDuringInit()
    {
        var factory = CreateFactory();
        await factory.InitializeDefaultPartitionsAsync(DefaultPartitions(),
            TestContext.Current.CancellationToken);

        // Verify all 4 default schemas exist
        foreach (var schema in new[] { "admin", "user", "portal", "kernel" })
        {
            await using var cmd = _fixture.DataSource.CreateCommand(
                $"SELECT 1 FROM information_schema.schemata WHERE schema_name = '{schema}'");
            var exists = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            exists.Should().NotBeNull($"schema '{schema}' should exist after init");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task DefaultSchemas_HaveVersionsSchemas()
    {
        var factory = CreateFactory();
        await factory.InitializeDefaultPartitionsAsync(DefaultPartitions(),
            TestContext.Current.CancellationToken);

        foreach (var schema in new[] { "admin_versions", "user_versions", "portal_versions", "kernel_versions" })
        {
            await using var cmd = _fixture.DataSource.CreateCommand(
                $"SELECT 1 FROM information_schema.schemata WHERE schema_name = '{schema}'");
            var exists = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            exists.Should().NotBeNull($"versions schema '{schema}' should exist after init");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task EachDefaultSchema_HasMeshNodesTable()
    {
        var factory = CreateFactory();
        await factory.InitializeDefaultPartitionsAsync(DefaultPartitions(),
            TestContext.Current.CancellationToken);

        foreach (var schema in new[] { "admin", "user", "portal", "kernel" })
        {
            await using var cmd = _fixture.DataSource.CreateCommand(
                $"SELECT 1 FROM information_schema.tables WHERE table_schema = '{schema}' AND table_name = 'mesh_nodes'");
            var exists = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            exists.Should().NotBeNull($"mesh_nodes table should exist in '{schema}' schema");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task UserSchema_HasSatelliteTables()
    {
        var factory = CreateFactory();
        await factory.InitializeDefaultPartitionsAsync(DefaultPartitions(),
            TestContext.Current.CancellationToken);

        var expectedTables = PartitionDefinition.StandardTableMappings.Values.ToList();

        foreach (var table in expectedTables)
        {
            await using var cmd = _fixture.DataSource.CreateCommand(
                $"SELECT 1 FROM information_schema.tables WHERE table_schema = 'user' AND table_name = '{table}'");
            var exists = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            exists.Should().NotBeNull($"satellite table '{table}' should exist in 'user' schema");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task AdminSchema_HasSatelliteTables()
    {
        var factory = CreateFactory();
        await factory.InitializeDefaultPartitionsAsync(DefaultPartitions(),
            TestContext.Current.CancellationToken);

        // All partitions now have StandardTableMappings — same schema everywhere
        var satelliteTables = PartitionDefinition.StandardTableMappings.Values.Distinct().ToList();

        foreach (var table in satelliteTables)
        {
            await using var cmd = _fixture.DataSource.CreateCommand(
                $"SELECT 1 FROM information_schema.tables WHERE table_schema = 'admin' AND table_name = '{table}'");
            var exists = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            exists.Should().NotBeNull($"satellite table '{table}' should exist in 'admin' schema");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task SatelliteTables_HaveCorrectColumns()
    {
        var factory = CreateFactory();
        await factory.InitializeDefaultPartitionsAsync(DefaultPartitions(),
            TestContext.Current.CancellationToken);

        // Check that the activities table has the expected columns (same as mesh_nodes)
        var expectedColumns = new HashSet<string>
        {
            "namespace", "id", "path", "name", "node_type", "description",
            "category", "icon", "display_order", "last_modified", "version",
            "state", "content", "desired_id", "main_node", "embedding"
        };

        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'user' AND table_name = 'activities'");
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        var actualColumns = new HashSet<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            actualColumns.Add(reader.GetString(0));

        actualColumns.Should().BeEquivalentTo(expectedColumns);
    }

    [Fact(Timeout = 60000)]
    public async Task SatelliteTables_HaveMainNodeIndex()
    {
        var factory = CreateFactory();
        await factory.InitializeDefaultPartitionsAsync(DefaultPartitions(),
            TestContext.Current.CancellationToken);

        // Check that the activities table has an index on main_node
        await using var cmd = _fixture.DataSource.CreateCommand(
            """
            SELECT 1 FROM pg_indexes
            WHERE schemaname = 'user' AND tablename = 'activities' AND indexname = 'idx_activities_main_node'
            """);
        var exists = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        exists.Should().NotBeNull("activities table should have main_node index");
    }

    [Fact(Timeout = 60000)]
    public async Task NodeTypePermissions_SyncedToAllSchemas()
    {
        var permissions = new[]
        {
            new NodeTypePermission("Partition", PublicRead: true),
            new NodeTypePermission("Organization", PublicRead: true),
            new NodeTypePermission("User", PublicRead: true),
        };

        var factory = CreateFactory(permissions);
        await factory.InitializeDefaultPartitionsAsync(DefaultPartitions(),
            TestContext.Current.CancellationToken);

        foreach (var schema in new[] { "admin", "user", "portal", "kernel" })
        {
            await using var cmd = _fixture.DataSource.CreateCommand(
                $"SELECT COUNT(*) FROM \"{schema}\".node_type_permissions WHERE public_read = true");
            var count = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            count.Should().BeGreaterThanOrEqualTo(3, $"at least the 3 explicit public-read permissions should be synced to '{schema}' schema");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task OrgCreation_CreatesSchemaWithSatelliteTables()
    {
        // Simulate what happens when an organization is created:
        // A partition with StandardTableMappings is initialized
        var orgPartition = new PartitionDefinition
        {
            Namespace = "GlobexCorp",
            DataSource = "default",
            Schema = "globexcorp",
            TableMappings = PartitionDefinition.StandardTableMappings,
            Description = "Partition for organization GlobexCorp"
        };

        var factory = CreateFactory();
        await factory.InitializeDefaultPartitionsAsync([orgPartition],
            TestContext.Current.CancellationToken);

        // Verify schema exists
        await using var schemaCmd = _fixture.DataSource.CreateCommand(
            "SELECT 1 FROM information_schema.schemata WHERE schema_name = 'globexcorp'");
        var schemaExists = await schemaCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        schemaExists.Should().NotBeNull("org schema should exist");

        // Verify mesh_nodes table
        await using var mnCmd = _fixture.DataSource.CreateCommand(
            "SELECT 1 FROM information_schema.tables WHERE table_schema = 'globexcorp' AND table_name = 'mesh_nodes'");
        var mnExists = await mnCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        mnExists.Should().NotBeNull("mesh_nodes should exist in org schema");

        // Verify satellite tables
        foreach (var table in new[] { "activities", "user_activities", "threads",
            "access", "annotations", "code" })
        {
            await using var cmd = _fixture.DataSource.CreateCommand(
                $"SELECT 1 FROM information_schema.tables WHERE table_schema = 'globexcorp' AND table_name = '{table}'");
            var exists = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            exists.Should().NotBeNull($"satellite table '{table}' should exist in org schema");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task DiscoverPartitions_FindsDefaultSchemas()
    {
        var factory = CreateFactory();
        await factory.InitializeDefaultPartitionsAsync(DefaultPartitions(),
            TestContext.Current.CancellationToken);

        // Verify schemas were created (admin, portal, kernel are excluded from DiscoverPartitionsAsync
        // because they are infrastructure partitions, not searchable content).
        foreach (var schema in new[] { "admin", "user", "portal", "kernel" })
        {
            await using var cmd = _fixture.DataSource.CreateCommand(
                "SELECT 1 FROM information_schema.schemata WHERE schema_name = $1");
            cmd.Parameters.AddWithValue(schema);
            var exists = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            exists.Should().NotBeNull($"schema '{schema}' should have been created");
        }

        // DiscoverPartitionsAsync returns only content partitions (excludes admin, portal, kernel)
        var freshFactory = CreateFactory();
        var partitions = await freshFactory.DiscoverPartitionsAsync(TestContext.Current.CancellationToken);
        partitions.Should().Contain("user");
    }

    /// <summary>
    /// Verifies that PostgreSqlSchemaInitializer.InitializeAsync (called by the migration project)
    /// creates node_type_permissions in the public schema, so per-partition queries have a fallback.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task MigrationInitialize_CreatesNodeTypePermissionsInPublicSchema()
    {
        // InitializeAsync is called by the fixture, simulating the migration.
        // Verify node_type_permissions exists in the public schema.
        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'node_type_permissions'");
        var exists = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        exists.Should().NotBeNull("node_type_permissions must exist in public schema after migration");
    }
}
