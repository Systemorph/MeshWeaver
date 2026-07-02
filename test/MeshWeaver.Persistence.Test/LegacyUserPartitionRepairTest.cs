using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Pins the automatic legacy-user-partition repair (<see cref="LegacyUserPartitionRepair"/> via
/// <see cref="PersistenceService.Read"/>): a store carrying only the pre-v10 <c>User/{id}</c>
/// layout (the samples/Graph/Data shape) heals on first partition-root read — the post-v10 root
/// node plus the self-admin grant are written durably, so the Activity home, DevLogin's
/// authoritative read, and partition writes all work without a manual migration. Runs against a
/// REAL <see cref="FileSystemStorageAdapter"/> over a temp directory — the exact "partition
/// imported from a file system" case.
/// </summary>
public class LegacyUserPartitionRepairTest : IDisposable
{
    private sealed record TestProvider(IStorageAdapter Adapter) : IPartitionStorageProvider
    {
        public string Name => "test-fs";
        public bool IsReadOnly => false;
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("legacy-user-repair").FullName;

    // String enums like the hub options (state: "Active" must parse the same way it does live).
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private PersistenceService CreateService()
        => new([new TestProvider(new FileSystemStorageAdapter(_dir))]);

    private void SeedLegacyFile(string relativePath, string json)
    {
        var file = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, json);
    }

    /// <summary>The samples/Graph/Data/User/Alice.json shape, verbatim.</summary>
    private const string LegacyAliceJson = """
        {
          "id": "Alice",
          "namespace": "User",
          "name": "Alice Chen",
          "nodeType": "User",
          "icon": "/static/NodeTypeIcons/person.svg",
          "content": {
            "$type": "User",
            "email": "alice.chen@meshweaver.io",
            "bio": "Software engineer and project contributor.",
            "pinnedPaths": ["Doc"]
          }
        }
        """;

    [Fact]
    public async Task PartitionRootReadRepairsLegacyUserDurably()
    {
        SeedLegacyFile("User/Alice.json", LegacyAliceJson);
        var persistence = CreateService();

        var root = await persistence.Read("Alice", _options).FirstAsync().ToTask();

        root.Should().NotBeNull();
        root!.Path.Should().Be("Alice");
        root.NodeType.Should().Be("User");
        root.Name.Should().Be("Alice Chen");
        root.Icon.Should().Be("/static/NodeTypeIcons/person.svg");
        root.Content.Should().NotBeNull();

        // Durable: the post-v10 root and the self-admin grant landed on disk.
        File.Exists(Path.Combine(_dir, "Alice.json")).Should().BeTrue("the repair materializes the partition root");
        File.Exists(Path.Combine(_dir, "Alice", "_Access", "Alice_Access.json"))
            .Should().BeTrue("a repaired user must be able to write their own partition");

        var grant = await persistence
            .Read(LegacyUserPartitionRepair.SelfAdminGrantPath("Alice"), _options).FirstAsync().ToTask();
        grant.Should().NotBeNull();
        grant!.NodeType.Should().Be("AccessAssignment");
        grant.MainNode.Should().Be("Alice");

        // Idempotent: the next read serves the durable root without re-repairing.
        var again = await persistence.Read("Alice", _options).FirstAsync().ToTask();
        again.Should().NotBeNull();
        again!.Path.Should().Be("Alice");
    }

    [Fact]
    public async Task StubRootUpgradesFromTheLegacyTwin()
    {
        SeedLegacyFile("User/Alice.json", LegacyAliceJson);
        // The auto-anchored placeholder a satellite write (ApiToken, UserActivity) leaves behind:
        // the root EXISTS but is typeless and content-less — the exact state that made the
        // missing-root check never fire while the home still rendered empty.
        SeedLegacyFile(
            "Alice.json",
            """{ "$type": "MeshNode", "id": "Alice", "path": "Alice", "mainNode": "Alice", "name": "Alice", "version": 1, "state": "Active" }""");
        var persistence = CreateService();

        var root = await persistence.Read("Alice", _options).FirstAsync().ToTask();

        root.Should().NotBeNull();
        root!.NodeType.Should().Be("User", "the stub upgrades from the legacy twin");
        root.Name.Should().Be("Alice Chen");
        root.Content.Should().NotBeNull();
        root.Version.Should().Be(2, "the upgrade is an update of the stub, not a recreate");

        File.Exists(Path.Combine(_dir, "Alice", "_Access", "Alice_Access.json")).Should().BeTrue();
    }

    [Fact]
    public async Task StubRootWithoutALegacyTwinStaysAStub()
    {
        SeedLegacyFile(
            "Space.json",
            """{ "$type": "MeshNode", "id": "Space", "path": "Space", "mainNode": "Space", "name": "Space", "version": 1, "state": "Active" }""");
        var persistence = CreateService();

        var node = await persistence.Read("Space", _options).FirstAsync().ToTask();

        node.Should().NotBeNull("a stub with no legacy user twin is returned untouched");
        node!.NodeType.Should().BeNull();
    }

    [Fact]
    public async Task RepairPreservesAnExistingGrant()
    {
        SeedLegacyFile("User/Alice.json", LegacyAliceJson);
        SeedLegacyFile(
            "Alice/_Access/Alice_Access.json",
            """
            {
              "id": "Alice_Access",
              "namespace": "Alice/_Access",
              "name": "Custom Grant",
              "nodeType": "AccessAssignment",
              "content": { "$type": "AccessAssignment", "accessObject": "Alice", "roles": [{ "role": "Viewer" }] }
            }
            """);
        var persistence = CreateService();

        var root = await persistence.Read("Alice", _options).FirstAsync().ToTask();
        root.Should().NotBeNull();

        var grant = await persistence
            .Read(LegacyUserPartitionRepair.SelfAdminGrantPath("Alice"), _options).FirstAsync().ToTask();
        grant!.Name.Should().Be("Custom Grant", "the repair must never clobber an existing assignment");
    }

    [Fact]
    public async Task NonUserLegacyNodesDoNotRepair()
    {
        SeedLegacyFile(
            "User/Config.json",
            """{ "id": "Config", "namespace": "User", "nodeType": "Markdown", "content": "not a user" }""");
        var persistence = CreateService();

        var node = await persistence.Read("Config", _options).FirstAsync().ToTask();

        node.Should().BeNull("only nodeType User under the legacy namespace is a user partition");
        File.Exists(Path.Combine(_dir, "Config.json")).Should().BeFalse();
    }

    [Fact]
    public async Task MissingPartitionsStayNull()
    {
        var persistence = CreateService();
        var node = await persistence.Read("Nobody", _options).FirstAsync().ToTask();
        node.Should().BeNull();
    }

    [Fact]
    public async Task NestedPathsNeverTriggerRepair()
    {
        SeedLegacyFile("User/Alice.json", LegacyAliceJson);
        var persistence = CreateService();

        var node = await persistence.Read("Some/Nested/Path", _options).FirstAsync().ToTask();

        node.Should().BeNull();
        File.Exists(Path.Combine(_dir, "Alice.json")).Should().BeFalse("only a bare partition-root read repairs");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }
}
