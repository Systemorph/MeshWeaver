using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests cascading progress propagation through thread hierarchies.
/// Simulates: parent thread with 2 sub-threads, each streaming.
/// Verifies that child progress updates propagate to parent's ActiveProgress
/// via remote stream subscriptions and UpdateMeshNode.
///
/// Hierarchy:
///   Parent (Orchestrator) — streaming response + 2 children
///     ├── Child1 (Navigator) — streaming response
///     └── Child2 (Researcher) — streaming response
///
/// Expected: Parent's ActiveProgress shows all 3 streaming entries.
/// When children complete, parent sees them as IsCompleted=true.
/// When all finish, parent clears ActiveProgress.
/// </summary>
public class CascadingProgressTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI().AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact]
    public async Task Progress_ChildUpdates_PropagateToParent()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Create thread hierarchy
        var parentPath = "User/Roland/_Thread/cascading-progress-test";
        var child1Path = $"{parentPath}/resp1/child-navigator";
        var child2Path = $"{parentPath}/resp1/child-researcher";

        // Create children first
        await meshService.CreateNodeAsync(new MeshNode("child-navigator", $"{parentPath}/resp1")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                ParentPath = "User/Roland",
                IsExecuting = true,
                ActiveMessageId = "nav-resp",
                ActiveProgress = new ThreadProgressEntry
                {
                    ThreadPath = child1Path,
                    ThreadName = "Navigator",
                    Status = "search_nodes"
                }
            }
        }, ct);

        await meshService.CreateNodeAsync(new MeshNode("child-researcher", $"{parentPath}/resp1")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                ParentPath = "User/Roland",
                IsExecuting = true,
                ActiveMessageId = "res-resp",
                ActiveProgress = new ThreadProgressEntry
                {
                    ThreadPath = child2Path,
                    ThreadName = "Researcher",
                    Status = "get_node"
                }
            }
        }, ct);

        // Create parent with initial progress (self only)
        await meshService.CreateNodeAsync(new MeshNode("cascading-progress-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                ParentPath = "User/Roland",
                IsExecuting = true,
                ActiveMessageId = "parent-resp",
                ActiveProgress = new ThreadProgressEntry
                {
                    ThreadPath = parentPath,
                    ThreadName = "Orchestrator",
                    Status = "Delegating..."
                }
            }
        }, ct);

        Output.WriteLine("Created thread hierarchy");

        // Subscribe to parent + children via remote streams
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var parentStream = workspace.GetRemoteStream<MeshNode>(
            new Address(parentPath), new MeshNodeReference());
        var child1Stream = workspace.GetRemoteStream<MeshNode>(
            new Address(child1Path), new MeshNodeReference());
        var child2Stream = workspace.GetRemoteStream<MeshNode>(
            new Address(child2Path), new MeshNodeReference());

        // 1. Simulate: parent subscribes to children and merges progress
        //    (this is what ChatClientAgentFactory does in production)
        child1Stream.Subscribe(change =>
        {
            var childThread = change.Value?.Content as MeshThread;
            if (childThread == null) return;

            var childEntry = childThread.ActiveProgress
                ?? new ThreadProgressEntry { ThreadPath = child1Path, ThreadName = "Navigator" };
            if (!childThread.IsExecuting)
                childEntry = childEntry with { IsCompleted = true };

            workspace.UpdateMeshNode(node =>
            {
                var thread = node.Content as MeshThread ?? new MeshThread();
                var selfEntry = thread.ActiveProgress
                    ?? new ThreadProgressEntry { ThreadPath = parentPath, ThreadName = "Orchestrator" };
                var children = selfEntry.Children
                    .Where(c => c.ThreadPath != child1Path)
                    .Append(childEntry).ToImmutableList();
                return node with { Content = thread with { ActiveProgress = selfEntry with { Children = children } } };
            }, new Address(parentPath), parentPath);
        });

        child2Stream.Subscribe(change =>
        {
            var childThread = change.Value?.Content as MeshThread;
            if (childThread == null) return;

            var childEntry = childThread.ActiveProgress
                ?? new ThreadProgressEntry { ThreadPath = child2Path, ThreadName = "Researcher" };
            if (!childThread.IsExecuting)
                childEntry = childEntry with { IsCompleted = true };

            workspace.UpdateMeshNode(node =>
            {
                var thread = node.Content as MeshThread ?? new MeshThread();
                var selfEntry = thread.ActiveProgress
                    ?? new ThreadProgressEntry { ThreadPath = parentPath, ThreadName = "Orchestrator" };
                var children = selfEntry.Children
                    .Where(c => c.ThreadPath != child2Path)
                    .Append(childEntry).ToImmutableList();
                return node with { Content = thread with { ActiveProgress = selfEntry with { Children = children } } };
            }, new Address(parentPath), parentPath);
        });

        // Wait for initial subscriptions to establish
        await Task.Delay(500, ct);

        // 2. Verify: parent should now have 2 children in ActiveProgress
        var parentWithChildren = parentStream
            .Select(ci => ci.Value?.Content as MeshThread)
            .Where(t => t?.ActiveProgress?.Children.Count >= 2)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        var parentThread = await parentWithChildren;
        parentThread.Should().NotBeNull();
        parentThread!.ActiveProgress!.Children.Should().HaveCount(2);
        Output.WriteLine($"Parent has {parentThread.ActiveProgress.Children.Count} children in progress");

        foreach (var child in parentThread.ActiveProgress.Children)
        {
            Output.WriteLine($"  {child.ThreadName}: {child.Status}, completed={child.IsCompleted}");
            child.IsCompleted.Should().BeFalse("children are still executing");
        }

        // 3. Simulate: child1 completes (Navigator finishes)
        workspace.UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with
            {
                Content = thread with
                {
                    IsExecuting = false,
                    ExecutionStatus = null,
                    ActiveProgress = thread.ActiveProgress != null
                        ? thread.ActiveProgress with { IsCompleted = true }
                        : null
                }
            };
        }, new Address(child1Path), child1Path);

        Output.WriteLine("Child1 (Navigator) completed");

        // 4. Verify: parent sees child1 as completed, child2 still running
        var parentAfterChild1 = await parentStream
            .Select(ci => ci.Value?.Content as MeshThread)
            .Where(t => t?.ActiveProgress?.Children.Any(c => c.IsCompleted) == true)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        var nav = parentAfterChild1!.ActiveProgress!.Children.First(c => c.ThreadName == "Navigator");
        var res = parentAfterChild1.ActiveProgress.Children.First(c => c.ThreadName == "Researcher");
        nav.IsCompleted.Should().BeTrue("Navigator completed");
        res.IsCompleted.Should().BeFalse("Researcher still running");
        Output.WriteLine($"After child1 done: Navigator={nav.IsCompleted}, Researcher={res.IsCompleted}");

        // 5. Simulate: child2 completes (Researcher finishes)
        workspace.UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with
            {
                Content = thread with
                {
                    IsExecuting = false,
                    ExecutionStatus = null,
                    ActiveProgress = thread.ActiveProgress != null
                        ? thread.ActiveProgress with { IsCompleted = true }
                        : null
                }
            };
        }, new Address(child2Path), child2Path);

        Output.WriteLine("Child2 (Researcher) completed");

        // 6. Verify: parent sees both children completed
        var parentAllDone = await parentStream
            .Select(ci => ci.Value?.Content as MeshThread)
            .Where(t => t?.ActiveProgress?.Children.All(c => c.IsCompleted) == true
                        && t?.ActiveProgress?.Children.Count >= 2)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        parentAllDone!.ActiveProgress!.Children.Should().HaveCount(2);
        parentAllDone.ActiveProgress.Children.Should().AllSatisfy(c => c.IsCompleted.Should().BeTrue());
        Output.WriteLine("All children completed — parent sees full progress tree with all IsCompleted=true");
    }

    [Fact]
    public async Task Progress_ThreeLevelHierarchy_PropagatesThroughChain()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Three levels: Parent → Child → Grandchild
        var parentPath = "User/Roland/_Thread/three-level-progress";
        var childPath = $"{parentPath}/resp1/child-research";
        var grandchildPath = $"{childPath}/resp2/grandchild-executor";

        // Create grandchild (leaf)
        await meshService.CreateNodeAsync(new MeshNode("grandchild-executor", $"{childPath}/resp2")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                ParentPath = "User/Roland",
                IsExecuting = true,
                ActiveProgress = new ThreadProgressEntry
                {
                    ThreadPath = grandchildPath, ThreadName = "Executor", Status = "search_nodes"
                }
            }
        }, ct);

        // Create child (subscribes to grandchild)
        await meshService.CreateNodeAsync(new MeshNode("child-research", $"{parentPath}/resp1")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                ParentPath = "User/Roland",
                IsExecuting = true,
                ActiveProgress = new ThreadProgressEntry
                {
                    ThreadPath = childPath, ThreadName = "Research", Status = "Delegating...",
                    Children = [new ThreadProgressEntry
                    {
                        ThreadPath = grandchildPath, ThreadName = "Executor", Status = "search_nodes"
                    }]
                }
            }
        }, ct);

        // Create parent
        await meshService.CreateNodeAsync(new MeshNode("three-level-progress", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                ParentPath = "User/Roland",
                IsExecuting = true,
                ActiveProgress = new ThreadProgressEntry
                {
                    ThreadPath = parentPath, ThreadName = "Orchestrator", Status = "Delegating..."
                }
            }
        }, ct);

        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Parent subscribes to child
        var childStream = workspace.GetRemoteStream<MeshNode>(
            new Address(childPath), new MeshNodeReference());

        childStream.Subscribe(change =>
        {
            var childThread = change.Value?.Content as MeshThread;
            if (childThread == null) return;

            var childEntry = childThread.ActiveProgress
                ?? new ThreadProgressEntry { ThreadPath = childPath, ThreadName = "Research" };
            if (!childThread.IsExecuting)
                childEntry = childEntry with { IsCompleted = true };

            workspace.UpdateMeshNode(node =>
            {
                var thread = node.Content as MeshThread ?? new MeshThread();
                var selfEntry = thread.ActiveProgress
                    ?? new ThreadProgressEntry { ThreadPath = parentPath, ThreadName = "Orchestrator" };
                var children = selfEntry.Children
                    .Where(c => c.ThreadPath != childPath)
                    .Append(childEntry).ToImmutableList();
                return node with { Content = thread with { ActiveProgress = selfEntry with { Children = children } } };
            }, new Address(parentPath), parentPath);
        });

        await Task.Delay(500, ct);

        // Verify parent sees child's grandchild in the tree
        var parentStream = workspace.GetRemoteStream<MeshNode>(
            new Address(parentPath), new MeshNodeReference());

        var parentWithTree = await parentStream
            .Select(ci => ci.Value?.Content as MeshThread)
            .Where(t => t?.ActiveProgress?.Children.Count >= 1
                        && t?.ActiveProgress?.Children.Any(c => c.Children.Count > 0) == true)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        var researchChild = parentWithTree!.ActiveProgress!.Children.First();
        researchChild.ThreadName.Should().Be("Research");
        researchChild.Children.Should().HaveCount(1);
        researchChild.Children[0].ThreadName.Should().Be("Executor");
        Output.WriteLine($"Three-level tree: Orchestrator → {researchChild.ThreadName} → {researchChild.Children[0].ThreadName}");

        // Grandchild completes → child updates → parent sees it
        workspace.UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with
            {
                Content = thread with
                {
                    IsExecuting = false,
                    ActiveProgress = new ThreadProgressEntry
                    {
                        ThreadPath = grandchildPath, ThreadName = "Executor",
                        Status = "Done", IsCompleted = true
                    }
                }
            };
        }, new Address(grandchildPath), grandchildPath);

        // Child sees grandchild done, marks itself done
        await Task.Delay(500, ct);
        workspace.UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with
            {
                Content = thread with
                {
                    IsExecuting = false,
                    ActiveProgress = new ThreadProgressEntry
                    {
                        ThreadPath = childPath, ThreadName = "Research",
                        Status = "Done", IsCompleted = true,
                        Children = [new ThreadProgressEntry
                        {
                            ThreadPath = grandchildPath, ThreadName = "Executor",
                            Status = "Done", IsCompleted = true
                        }]
                    }
                }
            };
        }, new Address(childPath), childPath);

        // Parent should see entire tree as completed
        var allDone = await parentStream
            .Select(ci => ci.Value?.Content as MeshThread)
            .Where(t => t?.ActiveProgress?.Children.All(c => c.IsCompleted) == true)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        allDone!.ActiveProgress!.Children.Should().HaveCount(1);
        var done = allDone.ActiveProgress.Children[0];
        done.IsCompleted.Should().BeTrue();
        done.Children[0].IsCompleted.Should().BeTrue();
        Output.WriteLine("Three-level hierarchy all completed");
    }
}
