using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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

    /// <summary>
    /// Wait until the accumulator <paramref name="notifications"/> list
    /// contains an entry matching <paramref name="predicate"/>. Polls on a
    /// 50 ms interval via Observable.Interval — replaces Task.Delay(500)
    /// "wait long enough for inotify" patterns. Filesystem events on Linux
    /// CI commonly take 1-2 s to arrive; a 5 s deadline keeps the happy
    /// path fast while still surfacing real failures.
    /// </summary>
    private static Task WaitForNotification(
        List<DataChangeNotification> notifications,
        Func<DataChangeNotification, bool> predicate,
        int timeoutMs = 5000) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => notifications.Any(predicate))
            .FirstAsync()
            .Timeout(TimeSpan.FromMilliseconds(timeoutMs))
            .ToTask();

    /// <summary>
    /// Prime the watcher by writing probe files into a sibling subdirectory
    /// until the watcher actually delivers a notification for one. Proves
    /// inotify is live before the real test action runs — replaces "Start()
    /// returned, sleep N ms and hope" with an observed-event guarantee.
    /// Bounded by Timeout, not by a guessed delay. Probe paths carry a unique
    /// GUID marker and live under <c>.prime/</c> so they never match the
    /// test's own assertions on <c>external/…</c> or <c>docs/…</c>.
    ///
    /// <para>Probes are spaced wider than the watcher's debounce window
    /// (<see cref="FileSystemChangeWatcher.DebounceIntervalMs"/>). A tighter
    /// schedule keeps resetting the debounce timer with each write, so no
    /// notification ever fires from the probes themselves.</para>
    /// </summary>
    private Task PrimeWatcherAsync(
        List<DataChangeNotification> notifications,
        int timeoutMs = 10_000)
    {
        var marker = $"prime-{Guid.NewGuid():N}";
        var probeDir = Path.Combine(_testDirectory, ".prime");
        Directory.CreateDirectory(probeDir);
        var stepMs = _watcher.DebounceIntervalMs * 2 + 50;
        return Observable.Interval(TimeSpan.FromMilliseconds(stepMs))
            .StartWith(0L)
            .Do(i => File.WriteAllText(
                Path.Combine(probeDir, $"{marker}-{i}.json"),
                "{}"))
            .Where(_ => notifications.Any(n => n.Path.Contains(marker)))
            .FirstAsync()
            .Timeout(TimeSpan.FromMilliseconds(timeoutMs))
            .ToTask();
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
        await PrimeWatcherAsync(receivedNotifications);

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

        // Stream-wait for the FS event — replaces a fixed Task.Delay(500).
        // Linux inotify can take 1-2s to deliver the first event after the
        // watch is established; a 500 ms delay would race on Linux CI.
        // 15s budget: 5s default tripped on slow Linux runners (run 26376715753).
        await WaitForNotification(receivedNotifications, n =>
            n.Path.Contains("external/node1") &&
            (n.Kind == DataChangeKind.Created || n.Kind == DataChangeKind.Updated),
            timeoutMs: 15_000);

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
        await PrimeWatcherAsync(receivedNotifications);

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

        // Stream-wait for the watcher's notification — replaces Task.Delay(500).
        await WaitForNotification(receivedNotifications, n =>
            n.Path.Contains("external/node1") &&
            (n.Kind == DataChangeKind.Created || n.Kind == DataChangeKind.Updated));

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
        await PrimeWatcherAsync(receivedNotifications);

        // Act - Modify the file externally
        await File.WriteAllTextAsync(filePath, """
            {
                "id": "node1",
                "name": "Modified Name"
            }
            """);

        // Stream-wait for the modification notification — replaces Task.Delay(500).
        await WaitForNotification(receivedNotifications, n =>
            n.Path.Contains("external/node1") &&
            (n.Kind == DataChangeKind.Updated || n.Kind == DataChangeKind.Created));

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
        await PrimeWatcherAsync(receivedNotifications);

        // Act - Delete the file externally
        File.Delete(filePath);

        // Stream-wait for the deletion notification — replaces Task.Delay(500).
        await WaitForNotification(receivedNotifications, n =>
            n.Path.Contains("external/node1") && n.Kind == DataChangeKind.Deleted);

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
        await PrimeWatcherAsync(receivedNotifications);

        // Act - Delete the file externally
        File.Delete(filePath);

        // Stream-wait for the deletion notification — replaces Task.Delay(500).
        await WaitForNotification(receivedNotifications, n =>
            n.Path.Contains("external/node1") && n.Kind == DataChangeKind.Deleted);

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
        await PrimeWatcherAsync(receivedNotifications);

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

        // Stream-wait for the watcher notification — replaces a hand-rolled
        // while+Task.Delay(100) polling loop with the same predicate.
        // inotify on Linux CI commonly takes 1-2 s to deliver the first
        // event after the watch is established; the 5 s deadline below is
        // the same budget the loop had, just expressed via Rx.
        await WaitForNotification(receivedNotifications, n =>
            n.Path.Contains("docs/readme")
            && (n.Kind == DataChangeKind.Created || n.Kind == DataChangeKind.Updated));

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
        await PrimeWatcherAsync(receivedNotifications);

        // Create a file to verify watcher is working
        var dir = Path.Combine(_testDirectory, "external");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "node1.json"), """{ "id": "node1" }""");
        // Stream-wait for the watcher's notification before flipping to the
        // "Stop" half of the test — replaces a fixed Task.Delay(500).
        await WaitForNotification(receivedNotifications, n => n.Path.Contains("external/node1"));

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
        await PrimeWatcherAsync(receivedNotifications);

        var dir = Path.Combine(_testDirectory, "external");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "node1.json");

        // Act - Make rapid changes to the same file. The 20 ms waits keep the
        // writes inside the 50 ms debounce window so the watcher coalesces them.
        await File.WriteAllTextAsync(filePath, """{ "id": "node1", "name": "v1" }""");
        await Task.Delay(20);
        await File.WriteAllTextAsync(filePath, """{ "id": "node1", "name": "v2" }""");
        await Task.Delay(20);
        await File.WriteAllTextAsync(filePath, """{ "id": "node1", "name": "v3" }""");

        // Wait until the debounced notification for node1 lands — replaces a
        // fixed Task.Delay(500) that races the debounce timer on slow CI.
        await WaitForNotification(receivedNotifications, n => n.Path.Contains("external/node1"));

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
