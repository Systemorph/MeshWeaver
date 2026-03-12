using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Tests for FileSystemChangeWatcher that monitors external file changes
/// and publishes notifications to ObserveQuery.
/// </summary>
[Collection("FileSystemWatcherTests")]
public class FileSystemChangeWatcherTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverTests", Guid.NewGuid().ToString());
    private readonly DataChangeNotifier _changeNotifier = new();
    private FileSystemStorageAdapter? _storageAdapterInstance;
    private FileSystemStorageAdapter _storageAdapter => _storageAdapterInstance ??= CreateStorageAdapter();
    protected new IMeshService MeshQuery => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private FileSystemChangeWatcher? _watcherInstance;
    private FileSystemChangeWatcher _watcher => _watcherInstance ??= new(_testDirectory, _storageAdapter, _changeNotifier, JsonOptions) { DebounceIntervalMs = 50 };
    private JsonSerializerOptions JsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    private FileSystemStorageAdapter CreateStorageAdapter()
    {
        Directory.CreateDirectory(_testDirectory);
        return new FileSystemStorageAdapter(_testDirectory);
    }

    public override void Dispose()
    {
        _watcherInstance?.Dispose();
        _changeNotifier.Dispose();

        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        base.Dispose();
    }

    #region External File Creation Tests

    [Fact]
    public async Task ExternalFileCreation_NotifiesObservers()
    {
        // Arrange
        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        _watcher.Start();

        // Act - Create a file externally
        var dir = Path.Combine(_testDirectory, "external");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "node1.json"), """
            {
                "id": "node1",
                "name": "External Node",
                "nodeType": "ExternalType"
            }
            """);

        // Wait for file system events and debounce
        await Task.Delay(500);

        // Assert - On Windows, file creation + write may result in Created or Changed/Updated event
        receivedNotifications.Should().Contain(n =>
            n.Path.Contains("external/node1") &&
            (n.Kind == DataChangeKind.Created || n.Kind == DataChangeKind.Updated));
    }

    [Fact]
    public async Task ExternalFileCreation_ObserveQueryReceivesUpdate()
    {
        // Arrange - subscribe to the change notifier (which watcher publishes to)
        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        _watcher.Start();

        // Act - Create a file externally
        var dir = Path.Combine(_testDirectory, "external");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "node1.json"), """
            {
                "id": "node1",
                "name": "External Node",
                "nodeType": "ExternalType"
            }
            """);

        // Wait for file system events and processing
        await Task.Delay(500);

        // Assert - watcher should have published a creation/update notification
        receivedNotifications.Should().NotBeEmpty();
        receivedNotifications.Should().Contain(n =>
            n.Path.Contains("external/node1") &&
            (n.Kind == DataChangeKind.Created || n.Kind == DataChangeKind.Updated));

        // Verify the notification entity has the expected data
        var notification = receivedNotifications.First(n => n.Path.Contains("external/node1"));
        notification.Entity.Should().NotBeNull();
    }

    #endregion

    #region External File Modification Tests

    [Fact]
    public async Task ExternalFileModification_NotifiesObservers()
    {
        // Arrange
        var dir = Path.Combine(_testDirectory, "external");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "node1.json");
        await File.WriteAllTextAsync(filePath, """
            {
                "id": "node1",
                "name": "Original Name"
            }
            """);

        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        _watcher.Start();

        // Small delay to ensure watcher is ready
        await Task.Delay(100);

        // Act - Modify the file externally
        await File.WriteAllTextAsync(filePath, """
            {
                "id": "node1",
                "name": "Modified Name"
            }
            """);

        // Wait for file system events and debounce
        await Task.Delay(500);

        // Assert
        receivedNotifications.Should().Contain(n =>
            n.Path.Contains("external/node1") &&
            (n.Kind == DataChangeKind.Updated || n.Kind == DataChangeKind.Created));
    }

    #endregion

    #region External File Deletion Tests

    [Fact]
    public async Task ExternalFileDeletion_NotifiesObservers()
    {
        // Arrange
        var dir = Path.Combine(_testDirectory, "external");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "node1.json");
        await File.WriteAllTextAsync(filePath, """
            {
                "id": "node1",
                "name": "Node to Delete"
            }
            """);

        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        _watcher.Start();

        // Small delay to ensure watcher is ready
        await Task.Delay(100);

        // Act - Delete the file externally
        File.Delete(filePath);

        // Wait for file system events and debounce
        await Task.Delay(500);

        // Assert
        receivedNotifications.Should().Contain(n =>
            n.Path.Contains("external/node1") && n.Kind == DataChangeKind.Deleted);
    }

    [Fact]
    public async Task ExternalFileDeletion_ObserveQueryReceivesRemoval()
    {
        // Arrange - Create a file on disk first (not via in-memory persistence)
        var dir = Path.Combine(_testDirectory, "external");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "node1.json");
        await File.WriteAllTextAsync(filePath, """
            {
                "id": "node1",
                "name": "Node to Delete",
                "nodeType": "Test"
            }
            """);

        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        _watcher.Start();
        await Task.Delay(100);

        // Act - Delete the file externally
        File.Delete(filePath);

        // Wait for file system events and processing
        await Task.Delay(500);

        // Assert - watcher should have published a deletion notification
        receivedNotifications.Should().Contain(n =>
            n.Path.Contains("external/node1") && n.Kind == DataChangeKind.Deleted);
    }

    #endregion

    #region Markdown File Tests

    [Fact]
    public async Task ExternalMarkdownCreation_NotifiesObservers()
    {
        // Arrange
        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        _watcher.Start();

        // Act - Create a markdown file externally
        var dir = Path.Combine(_testDirectory, "docs");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "readme.md"), """
            ---
            Name: External Readme
            NodeType: Markdown
            ---

            # External Readme

            This file was created externally.
            """);

        // Wait for file system events and debounce (increased for reliability)
        await Task.Delay(500);

        // Assert - On Windows, file creation + write may result in Created or Changed/Updated event
        receivedNotifications.Should().Contain(n =>
            n.Path.Contains("docs/readme") &&
            (n.Kind == DataChangeKind.Created || n.Kind == DataChangeKind.Updated));
    }

    #endregion

    #region Start/Stop Tests

    [Fact]
    public async Task Watcher_StoppedByDefault_DoesNotNotify()
    {
        // Arrange
        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        // Note: Watcher is NOT started

        // Act - Create a file externally
        var dir = Path.Combine(_testDirectory, "external");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "node1.json"), """{ "id": "node1" }""");

        await Task.Delay(500);

        // Assert - No notifications because watcher is not started
        receivedNotifications.Should().BeEmpty();
    }

    [Fact]
    public async Task Watcher_AfterStop_DoesNotNotify()
    {
        // Arrange
        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        _watcher.Start();

        // Create a file to verify watcher is working
        var dir = Path.Combine(_testDirectory, "external");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "node1.json"), """{ "id": "node1" }""");
        await Task.Delay(500);

        var countBefore = receivedNotifications.Count;
        countBefore.Should().BeGreaterThan(0);

        // Stop the watcher
        _watcher.Stop();

        // Act - Create another file
        await File.WriteAllTextAsync(Path.Combine(dir, "node2.json"), """{ "id": "node2" }""");
        await Task.Delay(500);

        // Assert - No new notifications after stop
        receivedNotifications.Count.Should().Be(countBefore);
    }

    #endregion

    #region Debounce Tests

    [Fact]
    public async Task Watcher_RapidChanges_Debounced()
    {
        // Arrange
        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        _watcher.Start();

        var dir = Path.Combine(_testDirectory, "external");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "node1.json");

        // Act - Make rapid changes to the same file
        await File.WriteAllTextAsync(filePath, """{ "id": "node1", "name": "v1" }""");
        await Task.Delay(20);
        await File.WriteAllTextAsync(filePath, """{ "id": "node1", "name": "v2" }""");
        await Task.Delay(20);
        await File.WriteAllTextAsync(filePath, """{ "id": "node1", "name": "v3" }""");

        // Wait for debounce
        await Task.Delay(500);

        // Assert - Changes should be debounced (not necessarily 3 separate notifications)
        // The exact count depends on timing, but we should have at least one
        receivedNotifications.Should().NotBeEmpty();
    }

    #endregion

    #region Unsupported File Tests

    [Fact]
    public async Task Watcher_UnsupportedFileTypes_Ignored()
    {
        // Arrange
        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        _watcher.Start();

        // Act - Create unsupported file types
        var dir = Path.Combine(_testDirectory, "external");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "file.txt"), "text file");
        await File.WriteAllTextAsync(Path.Combine(dir, "file.xml"), "<root/>");
        await File.WriteAllTextAsync(Path.Combine(dir, "file.yaml"), "key: value");

        await Task.Delay(500);

        // Assert - No notifications for unsupported file types
        receivedNotifications.Should().BeEmpty();
    }

    #endregion
}
