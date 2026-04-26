using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using NSubstitute;
using Xunit;
using NavigationContext = MeshWeaver.Mesh.Services.NavigationContext;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Tests for NavigationService path resolution, event firing, creatable types loading,
/// cancellation, and disposal.
/// </summary>
public class NavigationServiceTest
{
    private readonly MockNavigationManager _navigationManager;
    private readonly IPathResolver _pathResolver;
    private readonly IMeshService _meshQuery;
    private readonly IMessageHub _hub;
    private readonly IServiceProvider _hubServiceProvider;
    private readonly INodeTypeService _nodeTypeService;

    public NavigationServiceTest()
    {
        _navigationManager = new MockNavigationManager();
        _pathResolver = Substitute.For<IPathResolver>();
        _meshQuery = Substitute.For<IMeshService>();
        _hub = Substitute.For<IMessageHub>();
        _hubServiceProvider = Substitute.For<IServiceProvider>();
        _nodeTypeService = Substitute.For<INodeTypeService>();

        // INodeTypeService is registered at Hub level, not main DI
        _hub.ServiceProvider.Returns(_hubServiceProvider);
        _hubServiceProvider.GetService(typeof(INodeTypeService)).Returns(_nodeTypeService);
    }

    private NavigationService CreateService() =>
        new(_navigationManager, _pathResolver, _meshQuery, _hub);

    /// <summary>
    /// Capture the latest <see cref="NavigationContext"/> emitted by the reactive
    /// stream. NavigationService.NavigationContext is a ReplaySubject(1) — emits
    /// the current value on subscribe and on every change. Tests subscribe to
    /// capture into a local field for assertions.
    /// </summary>
    private sealed class ContextCapture : IDisposable
    {
        private readonly IDisposable _sub;
        public NavigationContext? Latest { get; private set; }
        public int EmissionCount { get; private set; }
        public ContextCapture(NavigationService service)
        {
            _sub = service.NavigationContext.Subscribe(ctx =>
            {
                Latest = ctx;
                EmissionCount++;
            });
        }
        public void Dispose() => _sub.Dispose();
    }

    /// <summary>
    /// Reactive bridge for tests — wait for the next NavigationContext emission.
    /// </summary>
    private static Task<NavigationContext?> NextContext(NavigationService service)
        => service.NavigationContext.Skip(0).FirstAsync().ToTask();

    #region InitializeAsync Tests

    [Fact]
    public async Task InitializeAsync_SubscribesToLocationChanged()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        // Act
        await service.InitializeAsync();

        // Assert - verify by simulating location change
        NavigationContext? receivedContext = null;
        service.NavigationContext.Subscribe(ctx => receivedContext = ctx);

        _pathResolver.ResolvePath("ACME/Project/Overview")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", "Overview")));

        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project/Overview");

        // Wait for async processing
        await Task.Delay(100, TestContext.Current.CancellationToken);

        receivedContext.Should().NotBeNull();
        receivedContext!.Area.Should().Be("Overview");
    }

    [Fact]
    public async Task InitializeAsync_CalledTwice_OnlySubscribesOnce()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));

        // Act
        await service.InitializeAsync();
        await service.InitializeAsync(); // Second call should be idempotent

        // Assert - verify only one subscription by checking only one event fires
        // .Skip(1) discards the cached value emitted by ReplaySubject(1) on subscribe.
        var eventCount = 0;
        service.NavigationContext.Skip(1).Subscribe(_ => eventCount++);

        _navigationManager.SimulateLocationChanged("http://localhost/ACME");

        await Task.Delay(100, TestContext.Current.CancellationToken);

        eventCount.Should().Be(1);
    }

    [Fact]
    public async Task InitializeAsync_ProcessesCurrentLocation()
    {
        // Arrange
        var service = CreateService();
        _navigationManager.SetUri("http://localhost/ACME/Project");
        _pathResolver.ResolvePath("ACME/Project")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        // Act
        await service.InitializeAsync();

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Namespace.Should().Be("ACME/Project");
    }

    #endregion

    #region Path Resolution Tests

    [Fact]
    public async Task OnLocationChanged_ResolvesPath_CreatesNavigationContext()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));
        await service.InitializeAsync();

        _pathResolver.ResolvePath("ACME/Project/Dashboard/123")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", "Dashboard/123")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project/Dashboard/123");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Path.Should().Be("ACME/Project/Dashboard/123");
        service.Context.Namespace.Should().Be("ACME/Project");
    }

    [Fact]
    public async Task OnLocationChanged_RaisesNavigationContextChangedEvent()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        await service.InitializeAsync();

        NavigationContext? receivedContext = null;
        service.NavigationContext.Subscribe(ctx => receivedContext = ctx);

        _pathResolver.ResolvePath("ACME/Project")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        receivedContext.Should().NotBeNull();
        receivedContext!.Namespace.Should().Be("ACME/Project");
    }

    [Fact]
    public async Task OnLocationChanged_WhenResolutionNull_KeepsStaleContext_UntilRetriesExhaust()
    {
        // The old behavior cleared Context and fired OnNavigationContextChanged(null)
        // immediately, which caused the "Page Not Found" card to flash before the
        // retry loop had a chance to succeed. The fix: keep the previous context
        // stale while we retry, and only fire the null/NotFound transition once
        // retries are exhausted.
        //
        // We can't easily override retry delays from this test class (it uses the
        // public ctor), but the 500 ms first-retry window is enough headroom to
        // assert that the null callback is NOT fired in the first ~100 ms.
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        await service.InitializeAsync();

        var previousContext = service.Context;
        previousContext.Should().NotBeNull();

        var nullContextInvocations = 0;
        service.NavigationContext.Subscribe(ctx =>
        {
            if (ctx is null) nullContextInvocations++;
        });

        _pathResolver.ResolvePath("unknown/path")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(null));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/unknown/path");
        await Task.Delay(100, TestContext.Current.CancellationToken); // < first retry delay (500 ms)

        // Assert: during the retry window the UI still reports the previous resolved
        // context (or at least did not receive a null-context invocation that would
        // trigger the "Page Not Found" render).
        nullContextInvocations.Should().Be(0,
            "the 404 flash bug was caused by firing a null context before retries had a chance");
    }

    [Fact]
    public async Task OnLocationChanged_ParsesRemainderIntoAreaAndId()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        await service.InitializeAsync();

        _pathResolver.ResolvePath("ACME/Project/Dashboard/item-123")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", "Dashboard/item-123")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project/Dashboard/item-123");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Area.Should().Be("Dashboard");
        service.Context.Id.Should().Be("item-123");
    }

    [Fact]
    public async Task OnLocationChanged_WithNoRemainder_SetsAreaAndIdNull()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        await service.InitializeAsync();

        _pathResolver.ResolvePath("ACME/Project")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Area.Should().BeNull();
        service.Context.Id.Should().BeNull();
    }

    [Fact]
    public async Task OnLocationChanged_WithAreaOnlyRemainder_SetsIdNull()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        await service.InitializeAsync();

        _pathResolver.ResolvePath("ACME/Project/Dashboard")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", "Dashboard")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project/Dashboard");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Area.Should().Be("Dashboard");
        service.Context.Id.Should().BeNull();
    }

    [Fact]
    public async Task OnLocationChanged_ConfigNodeWithAreaSuffix_ResolvesAreaCorrectly()
    {
        // Arrange - simulates Organization/Search where Organization is a config node
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        await service.InitializeAsync();

        // Organization/Search: address is "Organization", remainder is "Search"
        _pathResolver.ResolvePath("Organization/Search")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("Organization", "Search")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/Organization/Search");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Area.Should().Be("Search");
        service.Context.Id.Should().BeNull();
        service.Context.Namespace.Should().Be("Organization");
    }

    [Fact]
    public async Task OnLocationChanged_ConfigNodeWithAreaAndId_ResolvesCorrectly()
    {
        // Arrange - simulates Organization/Settings/Metadata
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        await service.InitializeAsync();

        _pathResolver.ResolvePath("Organization/Settings/Metadata")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("Organization", "Settings/Metadata")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/Organization/Settings/Metadata");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Area.Should().Be("Settings");
        service.Context.Id.Should().Be("Metadata");
        service.Context.Namespace.Should().Be("Organization");
    }

    [Fact]
    public async Task OnLocationChanged_UserNodeWithArea_ResolvesCorrectly()
    {
        // Arrange - simulates User/Roland/Settings where User/Roland is a persisted node
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        await service.InitializeAsync();

        _pathResolver.ResolvePath("User/Roland/Settings")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("User/Roland", "Settings")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/User/Roland/Settings");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Area.Should().Be("Settings");
        service.Context.Id.Should().BeNull();
        service.Context.Namespace.Should().Be("User/Roland");
    }

    #endregion

    #region Creatable Types Tests

    [Fact]
    public async Task OnLocationChanged_WhenMeshNodeChanges_ReloadsCreatableTypes()
    {
        // Arrange
        var service = CreateService();
        CreatableTypesSnapshot? lastSnapshot = null;
        service.CreatableTypes.Subscribe(s => lastSnapshot = s);

        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));

        _nodeTypeService.GetCreatableTypesAsync("ACME", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new CreatableTypeInfo("ACME/Todo")));

        await service.InitializeAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Change to different node path
        _pathResolver.ResolvePath("ACME/Project")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        _nodeTypeService.GetCreatableTypesAsync("ACME/Project", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(
                new CreatableTypeInfo("ACME/Project/Story"),
                new CreatableTypeInfo("ACME/Project/Todo")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project");
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert
        lastSnapshot.Should().NotBeNull();
        lastSnapshot!.Items.Should().HaveCount(2);
        lastSnapshot.Items.Should().Contain(t => t.NodeTypePath == "ACME/Project/Story");
        lastSnapshot.Items.Should().Contain(t => t.NodeTypePath == "ACME/Project/Todo");
    }

    [Fact]
    public async Task OnLocationChanged_WhenMeshNodeSame_DoesNotReloadCreatableTypes()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        var loadCount = 0;
        _nodeTypeService.GetCreatableTypesAsync("ACME/Project", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                loadCount++;
                return ToAsyncEnumerable(new CreatableTypeInfo("ACME/Project/Todo"));
            });

        await service.InitializeAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Navigate to different area within same node
        _pathResolver.ResolvePath("ACME/Project/Dashboard")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", "Dashboard")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project/Dashboard");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert - should only load once (during initialization)
        loadCount.Should().Be(1);
    }

    [Fact]
    public async Task LoadCreatableTypes_EmitsIncrementalSnapshots()
    {
        // Arrange
        var service = CreateService();
        var snapshots = new List<CreatableTypesSnapshot>();

        service.CreatableTypes.Subscribe(s => snapshots.Add(s));

        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        _nodeTypeService.GetCreatableTypesAsync("ACME/Project", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerableWithDelay(
                new CreatableTypeInfo("ACME/Project/Story"),
                new CreatableTypeInfo("ACME/Project/Todo")));

        // Act
        await service.InitializeAsync();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Assert - should see incremental updates: 0 (loading), 1 (loading), 2 (loading), 2 (done)
        snapshots.Should().Contain(s => s.Items.Count == 0 && s.IsLoading); // Initial clear
        snapshots.Should().Contain(s => s.Items.Count == 1 && s.IsLoading); // First item
        snapshots.Should().Contain(s => s.Items.Count == 2 && !s.IsLoading); // Final
    }

    [Fact]
    public async Task CreatableTypesSnapshot_IsLoadingTransitions()
    {
        // Arrange
        var service = CreateService();
        var loadingStates = new List<bool>();

        service.CreatableTypes.Subscribe(s => loadingStates.Add(s.IsLoading));

        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));

        _nodeTypeService.GetCreatableTypesAsync("ACME", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerableWithDelay(new CreatableTypeInfo("ACME/Todo")));

        // Act
        await service.InitializeAsync();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert - should see true while loading, false when done
        loadingStates.Should().Contain(true);
        loadingStates.Last().Should().BeFalse();
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public async Task Dispose_UnsubscribesFromLocationChanged()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        await service.InitializeAsync();

        var eventFired = false;
        // .Skip(1) discards the cached value (ReplaySubject) so we only count NEW emissions.
        service.NavigationContext.Skip(1).Subscribe(_ => eventFired = true);

        // Act
        service.Dispose();
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/New");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        eventFired.Should().BeFalse();
    }

    #endregion

    #region Satellite Node Tests

    [Fact]
    public async Task OnLocationChanged_SatelliteNode_CurrentNamespacePointsAtMainNode()
    {
        // User browses to a thread under PartnerRe/AIConsulting. The thread node's MainNode
        // points back at the parent that owns it, so CurrentNamespace â€” which downstream
        // chat/autocomplete/attachment code uses to resolve relative paths â€” must surface
        // the main node, not the satellite path.
        var service = CreateService();
        const string SatellitePath = "PartnerRe/AIConsulting/_Thread/abc-123";
        const string MainNode = "PartnerRe/AIConsulting";

        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution(SatellitePath, null)));

        var threadNode = new MeshNode("abc-123", "PartnerRe/AIConsulting/_Thread")
        {
            NodeType = "Thread",
            MainNode = MainNode
        };
        _meshQuery.QueryAsync(Arg.Any<MeshQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncObjects(threadNode));

        var ready = NextContext(service);
        await service.InitializeAsync();
        await ready;

        service.CurrentNamespace.Should().Be(MainNode);
        service.Context!.Namespace.Should().Be(SatellitePath);
        service.Context.PrimaryPath.Should().Be(MainNode);
        service.Context.IsSatellite.Should().BeTrue();
    }

    [Fact]
    public async Task OnLocationChanged_RegularNode_CurrentNamespaceMatchesNamespace()
    {
        // For a non-satellite node, CurrentNamespace and Namespace are the same.
        // PrimaryPath falls back to Namespace when Node is null, so this also covers
        // the no-node-found path (existing tests rely on this fallback).
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("PartnerRe/AIConsulting", null)));

        var mainNode = new MeshNode("AIConsulting", "PartnerRe")
        {
            NodeType = "Group"
            // MainNode defaults to Path â†’ "PartnerRe/AIConsulting"
        };
        _meshQuery.QueryAsync(Arg.Any<MeshQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncObjects(mainNode));

        await service.InitializeAsync();

        service.CurrentNamespace.Should().Be("PartnerRe/AIConsulting");
        service.Context!.PrimaryPath.Should().Be("PartnerRe/AIConsulting");
        service.Context.IsSatellite.Should().BeFalse();
    }

    [Fact]
    public async Task OnLocationChanged_SatelliteNode_LoadsCreatableTypesForMainNode()
    {
        // The creatable-types background load also keys off PrimaryPath so menus on
        // satellite pages reflect what can be created on the parent node.
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("PartnerRe/AIConsulting/_Thread/abc-123", null)));

        var threadNode = new MeshNode("abc-123", "PartnerRe/AIConsulting/_Thread")
        {
            NodeType = "Thread",
            MainNode = "PartnerRe/AIConsulting"
        };
        _meshQuery.QueryAsync(Arg.Any<MeshQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncObjects(threadNode));

        _nodeTypeService
            .GetCreatableTypesAsync("PartnerRe/AIConsulting", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new CreatableTypeInfo("PartnerRe/AIConsulting/Story")));

        CreatableTypesSnapshot? lastSnapshot = null;
        service.CreatableTypes.Subscribe(s => lastSnapshot = s);

        await service.InitializeAsync();
        await Task.Delay(150, TestContext.Current.CancellationToken);

        _nodeTypeService.Received().GetCreatableTypesAsync(
            "PartnerRe/AIConsulting", Arg.Any<CancellationToken>());
        lastSnapshot!.Items.Should().Contain(t => t.NodeTypePath == "PartnerRe/AIConsulting/Story");
    }

    #endregion

    #region Helper Methods

    private static async IAsyncEnumerable<CreatableTypeInfo> ToAsyncEnumerable(params CreatableTypeInfo[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<object> ToAsyncObjects(params object[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<CreatableTypeInfo> ToAsyncEnumerableWithDelay(
        params CreatableTypeInfo[] items)
    {
        foreach (var item in items)
        {
            await Task.Delay(30, TestContext.Current.CancellationToken);
            yield return item;
        }
    }

    #endregion

    #region Mock Navigation Manager

    /// <summary>
    /// Mock NavigationManager that allows simulating location changes.
    /// </summary>
    private class MockNavigationManager : NavigationManager
    {
        public MockNavigationManager()
        {
            Initialize("http://localhost/", "http://localhost/");
        }

        public void SetUri(string uri)
        {
            Uri = uri;
        }

        public void SimulateLocationChanged(string uri)
        {
            Uri = uri;
            NotifyLocationChanged(isInterceptedLink: false);
        }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Uri = new Uri(new Uri(BaseUri), uri).ToString();
            NotifyLocationChanged(isInterceptedLink: false);
        }
    }

    #endregion
}





