using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
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
    private readonly IMeshCatalog _meshCatalog;
    private readonly IMessageHub _hub;
    private readonly IServiceProvider _hubServiceProvider;
    private readonly INodeTypeService _nodeTypeService;

    public NavigationServiceTest()
    {
        _navigationManager = new MockNavigationManager();
        _meshCatalog = Substitute.For<IMeshCatalog>();
        _hub = Substitute.For<IMessageHub>();
        _hubServiceProvider = Substitute.For<IServiceProvider>();
        _nodeTypeService = Substitute.For<INodeTypeService>();

        // INodeTypeService is registered at Hub level, not main DI
        _hub.ServiceProvider.Returns(_hubServiceProvider);
        _hubServiceProvider.GetService(typeof(INodeTypeService)).Returns(_nodeTypeService);
    }

    private NavigationService CreateService() =>
        new(_navigationManager, _meshCatalog, _hub);

    #region InitializeAsync Tests

    [Fact]
    public async Task InitializeAsync_SubscribesToLocationChanged()
    {
        // Arrange
        var service = CreateService();
        _meshCatalog.ResolvePathAsync(Arg.Any<string>())
            .Returns(new AddressResolution("ACME/Project", null));

        // Act
        await service.InitializeAsync();

        // Assert - verify by simulating location change
        NavigationContext? receivedContext = null;
        service.OnNavigationContextChanged += ctx => receivedContext = ctx;

        _meshCatalog.ResolvePathAsync("ACME/Project/Overview")
            .Returns(new AddressResolution("ACME/Project", "Overview"));

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
        _meshCatalog.ResolvePathAsync(Arg.Any<string>())
            .Returns(new AddressResolution("ACME", null));

        // Act
        await service.InitializeAsync();
        await service.InitializeAsync(); // Second call should be idempotent

        // Assert - verify only one subscription by checking only one event fires
        var eventCount = 0;
        service.OnNavigationContextChanged += _ => eventCount++;

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
        _meshCatalog.ResolvePathAsync("ACME/Project")
            .Returns(new AddressResolution("ACME/Project", null));

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
        _meshCatalog.ResolvePathAsync(Arg.Any<string>())
            .Returns(new AddressResolution("ACME/Project", null));
        await service.InitializeAsync();

        _meshCatalog.ResolvePathAsync("ACME/Project/Dashboard/123")
            .Returns(new AddressResolution("ACME/Project", "Dashboard/123"));

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
        _meshCatalog.ResolvePathAsync(Arg.Any<string>())
            .Returns(new AddressResolution("ACME", null));
        await service.InitializeAsync();

        NavigationContext? receivedContext = null;
        service.OnNavigationContextChanged += ctx => receivedContext = ctx;

        _meshCatalog.ResolvePathAsync("ACME/Project")
            .Returns(new AddressResolution("ACME/Project", null));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        receivedContext.Should().NotBeNull();
        receivedContext!.Namespace.Should().Be("ACME/Project");
    }

    [Fact]
    public async Task OnLocationChanged_WhenResolutionNull_SetsContextNull()
    {
        // Arrange
        var service = CreateService();
        _meshCatalog.ResolvePathAsync(Arg.Any<string>())
            .Returns(new AddressResolution("ACME", null));
        await service.InitializeAsync();

        NavigationContext? receivedContext = new NavigationContext
        {
            Path = "test",
            Resolution = new AddressResolution("test", null)
        };
        service.OnNavigationContextChanged += ctx => receivedContext = ctx;

        _meshCatalog.ResolvePathAsync("unknown/path")
            .Returns((AddressResolution?)null);

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/unknown/path");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        receivedContext.Should().BeNull();
        service.Context.Should().BeNull();
        service.CurrentNamespace.Should().BeNull();
    }

    [Fact]
    public async Task OnLocationChanged_ParsesRemainderIntoAreaAndId()
    {
        // Arrange
        var service = CreateService();
        _meshCatalog.ResolvePathAsync(Arg.Any<string>())
            .Returns(new AddressResolution("ACME", null));
        await service.InitializeAsync();

        _meshCatalog.ResolvePathAsync("ACME/Project/Dashboard/item-123")
            .Returns(new AddressResolution("ACME/Project", "Dashboard/item-123"));

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
        _meshCatalog.ResolvePathAsync(Arg.Any<string>())
            .Returns(new AddressResolution("ACME", null));
        await service.InitializeAsync();

        _meshCatalog.ResolvePathAsync("ACME/Project")
            .Returns(new AddressResolution("ACME/Project", null));

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
        _meshCatalog.ResolvePathAsync(Arg.Any<string>())
            .Returns(new AddressResolution("ACME", null));
        await service.InitializeAsync();

        _meshCatalog.ResolvePathAsync("ACME/Project/Dashboard")
            .Returns(new AddressResolution("ACME/Project", "Dashboard"));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project/Dashboard");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Area.Should().Be("Dashboard");
        service.Context.Id.Should().BeNull();
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

        _meshCatalog.ResolvePathAsync(Arg.Any<string>())
            .Returns(new AddressResolution("ACME", null));

        _nodeTypeService.GetCreatableTypesAsync("ACME", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new CreatableTypeInfo("ACME/Todo")));

        await service.InitializeAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Change to different node path
        _meshCatalog.ResolvePathAsync("ACME/Project")
            .Returns(new AddressResolution("ACME/Project", null));

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
        _meshCatalog.ResolvePathAsync(Arg.Any<string>())
            .Returns(new AddressResolution("ACME/Project", null));

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
        _meshCatalog.ResolvePathAsync("ACME/Project/Dashboard")
            .Returns(new AddressResolution("ACME/Project", "Dashboard"));

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

        _meshCatalog.ResolvePathAsync(Arg.Any<string>())
            .Returns(new AddressResolution("ACME/Project", null));

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

        _meshCatalog.ResolvePathAsync(Arg.Any<string>())
            .Returns(new AddressResolution("ACME", null));

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
        _meshCatalog.ResolvePathAsync(Arg.Any<string>())
            .Returns(new AddressResolution("ACME", null));
        await service.InitializeAsync();

        var eventFired = false;
        service.OnNavigationContextChanged += _ => eventFired = true;

        // Act
        service.Dispose();
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/New");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        eventFired.Should().BeFalse();
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
