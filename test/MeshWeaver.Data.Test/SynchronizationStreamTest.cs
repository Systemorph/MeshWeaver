using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;
using System;
using System.Threading;
using FluentAssertions.Extensions;
using System.Collections.Immutable;
using MeshWeaver.Data.Serialization;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Tests for synchronization stream operations and data change management
/// </summary>
public class SynchronizationStreamTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Instance = nameof(Instance);

    /// <summary>
    /// Configures the host with MyData and object types for synchronization testing
    /// </summary>
    /// <param name="configuration">The configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(
                    dataSource =>
                        dataSource.WithType<MyData>(type =>
                            type.WithKey(instance => instance.Id)
                        ).WithType<object>(type => type.WithKey(i => i))
                )
            );
    }

    /// <summary>
    /// Tests parallel updates to the synchronization stream with concurrent modifications
    /// </summary>
    [Fact]
    public async Task ParallelUpdate()
    {
        List<MyData> tracker = new();
        var workspace = GetHost().GetWorkspace();
        var collectionName = workspace.DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var stream = workspace.GetStream(new CollectionsReference(collectionName));
        stream.Should().NotBeNull();
        stream.Reduce(new EntityReference(collectionName, Instance))!
            .Select(i => i.Value!)
            .OfType<MyData>()
            .Subscribe(tracker.Add);

        var count = 0;
        Enumerable.Range(0, 10).AsParallel().Select(_ =>
        {
            stream.Update(state =>
            {
                var instance = new MyData(Instance, (++count).ToString());
                var existingInstance = state?.Collections.GetValueOrDefault(collectionName)?.Instances
                    .GetValueOrDefault(Instance);

                return stream.ApplyChanges(
                    new EntityStoreAndUpdates(
                        WorkspaceOperations.Update((state ?? new()), collectionName, i => i.Update(Instance, instance)),
                        [new EntityUpdate(collectionName, Instance, instance) { OldValue = existingInstance }],
                        stream.StreamId)
                );
            }, _ => Task.CompletedTask);
            return true;
        }).ToArray();
        await Task.Delay(100, CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken,
            new CancellationTokenSource(5.Seconds()).Token
        ).Token);
        await DisposeAsync();

        tracker.Should().HaveCount(10)
            .And.Subject.Select(t => t.Text).Should().Equal(Enumerable.Range(0, 10).Select(exp => (exp + 1).ToString()));
    }

    /// <summary>
    /// Tests concurrent workspace stream creation and updates to reveal race conditions
    /// This simulates the real scenario where multiple clients connect while data is being updated
    /// </summary>
    [Fact]
    public async Task ConcurrentWorkspaceStreamCreation_ShouldRevealRaceConditions()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        
        // Get the existing data source (configured in ConfigureHost)
        var collectionName = workspace.DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var dataSource = workspace.DataContext.DataSources.First();
        var dataSourceStream = dataSource.GetStreamForPartition(null);
        dataSourceStream.Should().NotBeNull();
        
        // Initialize with some data
        var initialData = new Dictionary<string, MyData>
        {
            ["1"] = new("1", "Initial Value"),
            ["2"] = new("2", "Second Item"),
        };

        var initialStore = new EntityStore
        {
            Collections = new Dictionary<string, InstanceCollection>
            {
                ["MyData"] = new InstanceCollection(initialData.Values.Cast<object>(), instance => ((MyData)instance).Id)
            }.ToImmutableDictionary()
        };

        dataSourceStream!.Initialize(initialStore);
        await Task.Delay(50, TestContext.Current.CancellationToken); // Let initialization complete

        // Track updates seen by each client stream
        var clientUpdates = new Dictionary<string, List<string>>();
        var clientStreams = new List<ISynchronizationStream<EntityStore>>();
        var lockObject = new object();

        // Start continuous updates to the data source in background
        var updateTask = Task.Run(async () =>
        {
            for (int i = 1; i <= 20; i++)
            {
                var updatedItem = new MyData("1", $"Update {i}");
                var updatedData = initialData.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                updatedData["1"] = updatedItem;

                var updatedStore = new EntityStore
                {
                    Collections = new Dictionary<string, InstanceCollection>
                    {
                        ["MyData"] = new InstanceCollection(updatedData.Values.Cast<object>(), instance => ((MyData)instance).Id)
                    }.ToImmutableDictionary()
                };

                dataSourceStream.Update(currentState =>
                {
                    return new ChangeItem<EntityStore>(
                        updatedStore,
                        "test-updater",
                        string.Empty,
                        ChangeType.Patch,
                        (long)(i + 1),
                        [new EntityUpdate("MyData", "1", updatedItem) { OldValue = initialData["1"] }]
                    );
                }, ex => 
                {
                    Output.WriteLine($"Data source update {i} failed: {ex.Message}");
                    return Task.CompletedTask;
                });

                // Small delay between updates to create more race condition opportunities
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        });

        // Concurrently create multiple workspace streams while updates are happening
        var clientTasks = Enumerable.Range(1, 5).Select(clientId =>
            Task.Run(async () =>
            {
                // Random delay to stagger client creation
                await Task.Delay(Random.Shared.Next(10, 50), TestContext.Current.CancellationToken);
                
                var clientName = $"client-{clientId}";
                var workspaceStream = workspace.GetStream(new CollectionsReference("MyData"));
                
                if (workspaceStream != null)
                {
                    clientStreams.Add(workspaceStream);
                    var updates = new List<string>();
                    
                    lock (lockObject)
                    {
                        clientUpdates[clientName] = updates;
                    }

                    workspaceStream.Subscribe(change =>
                    {
                        if (change.Value?.GetData<MyData>().FirstOrDefault(x => x.Id == "1") is { } item)
                        {
                            lock (lockObject)
                            {
                                updates.Add(item.Text);
                                Output.WriteLine($"{clientName} saw: {item.Text}");
                            }
                        }
                    });
                    
                    // Keep the client alive for a while to observe updates
                    await Task.Delay(200, TestContext.Current.CancellationToken);
                }
            })
        ).ToArray();

        // Wait for updates and clients to finish
        await Task.WhenAll(updateTask);
        await Task.WhenAll(clientTasks);
        await Task.Delay(100, TestContext.Current.CancellationToken); // Final propagation time

        // Clean up
        foreach (var stream in clientStreams)
        {
            stream.Dispose();
        }
        dataSource.Dispose();

        // Analyze results for race conditions
        Output.WriteLine("\n=== RACE CONDITION ANALYSIS ===");
        
        foreach (var (clientName, updates) in clientUpdates)
        {
            Output.WriteLine($"{clientName}: {updates.Count} updates [{string.Join(", ", updates)}]");
        }

        if (clientUpdates.Any())
        {
            var allUpdates = clientUpdates.Values.SelectMany(u => u).Distinct().ToList();
            Output.WriteLine($"Total unique updates seen across all clients: {allUpdates.Count}");
            Output.WriteLine($"Updates: [{string.Join(", ", allUpdates)}]");

            // Check for inconsistencies between clients
            var finalStates = clientUpdates.Values
                .Where(updates => updates.Any())
                .Select(updates => updates.Last())
                .Distinct()
                .ToList();

            if (finalStates.Count > 1)
            {
                Output.WriteLine($"RACE CONDITION DETECTED: Clients ended with different final states:");
                for (int i = 0; i < finalStates.Count; i++)
                {
                    Output.WriteLine($"  Final state {i + 1}: {finalStates[i]}");
                }
                
                // This assertion should fail if race conditions exist
                finalStates.Should().HaveCount(1, 
                    "All clients should end up with the same final state, but race conditions caused different states");
            }

            // Check for missing updates
            var updateCounts = clientUpdates.Values.Select(u => u.Count).ToList();
            var minUpdates = updateCounts.Min();
            var maxUpdates = updateCounts.Max();
            
            if (maxUpdates - minUpdates > 3) // Allow some tolerance
            {
                Output.WriteLine($"RACE CONDITION DETECTED: Significant difference in update counts (min: {minUpdates}, max: {maxUpdates})");
            }
        }
    }
}
