using System.Diagnostics;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Hosted service that seeds Cosmos DB from file system data at startup.
/// Idempotent — skips seeding if data already exists (unless ForceReseed is set).
/// </summary>
public class CosmosDataSeeder : IHostedService
{
    private readonly IStorageAdapter _cosmosAdapter;
    private readonly CosmosSeederOptions _options;
    private readonly ILogger<CosmosDataSeeder> _logger;
    private int _nodeCount;
    private int _partitionCount;

    public CosmosDataSeeder(
        IStorageAdapter cosmosAdapter,
        IOptions<CosmosSeederOptions> options,
        ILogger<CosmosDataSeeder> logger)
    {
        _cosmosAdapter = cosmosAdapter;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("Cosmos DB seeding is disabled");
            return;
        }

        if (string.IsNullOrEmpty(_options.SeedDataPath))
        {
            _logger.LogWarning("Cosmos DB seeding enabled but SeedDataPath is not configured");
            return;
        }

        if (!Directory.Exists(_options.SeedDataPath))
        {
            _logger.LogWarning("Seed data path does not exist: {Path}", _options.SeedDataPath);
            return;
        }

        if (!_options.ForceReseed)
        {
            var exists = await _cosmosAdapter.ExistsAsync("Organization", cancellationToken);
            if (exists)
            {
                _logger.LogInformation("Cosmos DB already contains data, skipping seeding");
                return;
            }
        }

        _logger.LogInformation("Starting Cosmos DB seeding from {Path}", _options.SeedDataPath);
        var sw = Stopwatch.StartNew();

        var fileSystemAdapter = new FileSystemStorageAdapter(_options.SeedDataPath);
        var jsonOptions = new JsonSerializerOptions();
        _nodeCount = 0;
        _partitionCount = 0;

        await SeedNodesRecursivelyAsync(fileSystemAdapter, null, jsonOptions, cancellationToken);

        sw.Stop();
        _logger.LogInformation(
            "Cosmos DB seeding complete: {NodeCount} nodes, {PartitionCount} partitions in {Elapsed}ms",
            _nodeCount, _partitionCount, sw.ElapsedMilliseconds);
    }

    private async Task SeedNodesRecursivelyAsync(
        FileSystemStorageAdapter source,
        string? parentPath,
        JsonSerializerOptions options,
        CancellationToken ct)
    {
        var (nodePaths, directoryPaths) = await source.ListChildPathsAsync(parentPath, ct);

        foreach (var nodePath in nodePaths)
        {
            var node = await source.ReadAsync(nodePath, options, ct);
            if (node != null)
            {
                await _cosmosAdapter.WriteAsync(node, options, ct);
                _nodeCount++;

                if (_nodeCount % 50 == 0)
                    _logger.LogDebug("Seeded {Count} nodes so far...", _nodeCount);

                // Seed partition data for this node
                await SeedPartitionsAsync(source, nodePath, options, ct);

                // Recurse into children
                await SeedNodesRecursivelyAsync(source, nodePath, options, ct);
            }
        }

        // Scan directories that aren't nodes
        foreach (var dirPath in directoryPaths)
        {
            await SeedNodesRecursivelyAsync(source, dirPath, options, ct);
        }
    }

    private async Task SeedPartitionsAsync(
        FileSystemStorageAdapter source,
        string nodePath,
        JsonSerializerOptions options,
        CancellationToken ct)
    {
        // Discover partition subdirectories by looking at the file system
        var nodeDir = Path.Combine(_options.SeedDataPath!, nodePath.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(nodeDir))
            return;

        foreach (var subDir in Directory.GetDirectories(nodeDir))
        {
            var subDirName = Path.GetFileName(subDir);

            // Skip if this subdirectory corresponds to a child node
            // (i.e., there's a sibling file like "SubDir.json" or "SubDir.md")
            if (IsChildNodeDirectory(subDirName, nodeDir))
                continue;

            // This is a partition directory — seed its objects
            var objects = new List<object>();
            await foreach (var obj in source.GetPartitionObjectsAsync(nodePath, subDirName, options, ct))
            {
                objects.Add(obj);
            }

            if (objects.Count > 0)
            {
                await _cosmosAdapter.SavePartitionObjectsAsync(nodePath, subDirName, objects, options, ct);
                _partitionCount++;
                _logger.LogDebug("Seeded partition {NodePath}/{SubPath} with {Count} objects",
                    nodePath, subDirName, objects.Count);
            }
        }
    }

    private static bool IsChildNodeDirectory(string dirName, string parentDir)
    {
        // Check if there's a sibling file with the same name (any supported extension)
        string[] extensions = [".md", ".cs", ".json"];
        return extensions.Any(ext => File.Exists(Path.Combine(parentDir, dirName + ext)));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
